using System.Text.Json;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>One playable rendition of a Douyin video: a single MP4 with picture and sound.</summary>
public sealed record DouyinStream(string Url, int Width, int Height, long Bitrate, long? Size, bool H265)
{
    /// <summary>Douyin videos are mostly portrait: the short side is what people call 1080p.</summary>
    public int Resolution => Math.Min(Width, Height);
}

public sealed record DouyinVideo(
    string Id, string Title, string? Author, string? UploadDate, double? Duration, string? Cover,
    IReadOnlyList<DouyinStream> Streams);

/// <summary>
/// Reads the response of Douyin's web "aweme/detail" API (fetched by its own page script, see the app's
/// DouyinFetcher), and picks the stream to download. yt-dlp's Douyin support can't sign these requests any more.
/// </summary>
public static class DouyinParser
{
    public static DouyinVideo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("aweme_detail", out var a) || a.ValueKind != JsonValueKind.Object)
            throw new YtDlpException(L.Get("Error_DouyinUnavailable"));

        var id = Str(a, "aweme_id") ?? "";
        var title = (Str(a, "desc") ?? "").Trim();
        if (title.Length == 0) title = $"Douyin {id}";
        var author = a.TryGetProperty("author", out var au) ? Str(au, "nickname") : null;
        var date = a.TryGetProperty("create_time", out var ct) && ct.TryGetInt64(out var secs) && secs > 0
            ? DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime().ToString("yyyyMMdd")
            : null;

        if (!a.TryGetProperty("video", out var v) || v.ValueKind != JsonValueKind.Object
            || a.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
            throw new YtDlpException(L.Get("Error_DouyinNotVideo"));

        var streams = new List<DouyinStream>();
        if (v.TryGetProperty("bit_rate", out var rates) && rates.ValueKind == JsonValueKind.Array)
            foreach (var r in rates.EnumerateArray())
                if (r.TryGetProperty("play_addr", out var addr) && Stream(addr, Long(r, "bit_rate"), Long(r, "is_h265") == 1) is { } s)
                    streams.Add(s);
        // Older / simpler answers only have the default address.
        if (streams.Count == 0 && v.TryGetProperty("play_addr", out var play) && Stream(play, 0, false) is { } single)
            streams.Add(single);
        if (streams.Count == 0) throw new YtDlpException(L.Get("Error_DouyinUnavailable"));

        var duration = Long(v, "duration") is > 0 and var ms ? ms / 1000.0 : (double?)null;
        string? cover = null;
        foreach (var key in new[] { "origin_cover", "cover", "dynamic_cover" })
            if (cover is null && v.TryGetProperty(key, out var c)) cover = FirstUrl(c);

        return new DouyinVideo(id, title, author, date, duration, cover,
            streams.OrderByDescending(s => s.Resolution).ThenByDescending(s => s.Bitrate).ToList());
    }

    static DouyinStream? Stream(JsonElement addr, long bitrate, bool h265)
    {
        var urls = Urls(addr);
        if (urls.Count == 0) return null;
        // Prefer a CDN address over douyin.com/aweme/v1/play (a redirect that sometimes refuses non-browser clients).
        var url = urls.FirstOrDefault(u => !u.Contains("/aweme/v1/play", StringComparison.Ordinal)) ?? urls[0];
        return new DouyinStream(url, (int)Long(addr, "width"), (int)Long(addr, "height"), bitrate,
            Long(addr, "data_size") is > 0 and var size ? size : null, h265);
    }

    /// <summary>The stream that fits the request: within the resolution limit (if any), the codec asked for when
    /// offered (H.264 for compatibility mode), otherwise the highest resolution and bitrate.</summary>
    public static DouyinStream Pick(DouyinVideo video, int maxHeight, string codec)
    {
        var fitting = video.Streams.Where(s => maxHeight <= 0 || s.Resolution <= maxHeight).ToList();
        if (fitting.Count == 0) fitting = [video.Streams.MinBy(s => s.Resolution)!];
        var wanted = codec switch
        {
            "h264" => fitting.Where(s => !s.H265).ToList(),
            "h265" => fitting.Where(s => s.H265).ToList(),
            _ => fitting,
        };
        return (wanted.Count > 0 ? wanted : fitting)
            .OrderByDescending(s => s.Resolution).ThenByDescending(s => s.Bitrate).First();
    }

    /// <summary>The same shape the yt-dlp path produces, so the rest of the app needn't know.</summary>
    public static VideoInfo ToVideoInfo(DouyinVideo v) => new()
    {
        Url = $"https://www.douyin.com/video/{v.Id}",
        Title = v.Title,
        Uploader = v.Author,
        UploadDate = v.UploadDate,
        Duration = v.Duration,
        ThumbnailUrl = v.Cover,
        Platform = Platform.Douyin,
        Qualities = v.Streams.GroupBy(s => s.Resolution)
            .Select(g => new QualityOption(g.Key, 0, g.Max(s => s.Size)))
            .OrderByDescending(q => q.Height).ToList(),
        Codecs = v.Streams.GroupBy(s => s.H265 ? "h265" : "h264")
            .ToDictionary(g => g.Key, g => g.Max(s => s.Resolution)),
    };

    static List<string> Urls(JsonElement e) =>
        e.TryGetProperty("url_list", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(u => u.GetString()).Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).ToList()
            : [];

    static string? FirstUrl(JsonElement e) => Urls(e).FirstOrDefault();

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n : 0;
}
