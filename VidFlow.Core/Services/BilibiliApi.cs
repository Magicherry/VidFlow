using System.Text.Json;
using System.Text.RegularExpressions;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>
/// yt-dlp's fast (--flat-playlist) listing of a Bilibili multi-part video has no part names or cover.
/// One call to Bilibili's public video-info endpoint fills them in, however many parts there are.
/// </summary>
public static partial class BilibiliApi
{
    [GeneratedRegex(@"BV[0-9A-Za-z]{10}")]
    private static partial Regex BvidRegex();

    public static async Task<PlaylistInfo> EnrichAsync(PlaylistInfo playlist, CancellationToken ct)
    {
        var bvid = BvidRegex().Match(playlist.Url).Value;
        if (bvid.Length == 0) return playlist;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}");
            req.Headers.Referrer = new Uri("https://www.bilibili.com/");
            using var resp = await Http.Client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            return ApplyView(playlist, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Bilibili part names unavailable: {ex.Message}");
            return playlist;
        }
    }

    /// <summary>Merges the view API response into the playlist (pure; unit tested).</summary>
    public static PlaylistInfo ApplyView(PlaylistInfo playlist, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return playlist;

        var parts = new Dictionary<int, (string Name, double Duration)>();
        if (data.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            foreach (var p in pages.EnumerateArray())
                if (p.TryGetProperty("page", out var n) && n.TryGetInt32(out var page))
                    parts[page] = (p.TryGetProperty("part", out var part) ? part.GetString() ?? "" : "",
                                   p.TryGetProperty("duration", out var d) && d.TryGetDouble(out var sec) ? sec : 0);

        var cover = data.TryGetProperty("pic", out var pic) ? pic.GetString() : null;
        if (cover?.StartsWith("http://", StringComparison.Ordinal) == true) cover = "https://" + cover[7..];
        var owner = data.TryGetProperty("owner", out var o) && o.TryGetProperty("name", out var name) ? name.GetString() : null;

        return new PlaylistInfo
        {
            Url = playlist.Url,
            Title = playlist.Title,
            Platform = playlist.Platform,
            Uploader = playlist.Uploader ?? owner,
            ThumbnailUrl = playlist.ThumbnailUrl ?? cover,
            Entries = playlist.Entries.Select(e => parts.TryGetValue(e.Index, out var p) && p.Name.Length > 0
                ? new PlaylistEntry
                {
                    Index = e.Index,
                    Url = e.Url,
                    Title = $"P{e.Index} {p.Name}",
                    Duration = e.Duration ?? (p.Duration > 0 ? p.Duration : null),
                    ThumbnailUrl = e.ThumbnailUrl ?? cover,
                }
                : e).ToList(),
        };
    }
}
