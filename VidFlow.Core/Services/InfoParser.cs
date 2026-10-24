using System.Text.Json;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Turns yt-dlp's <c>-J</c> JSON into <see cref="VideoInfo"/> or <see cref="PlaylistInfo"/>.</summary>
public static class InfoParser
{
    public static MediaInfo Parse(string json, string requestedUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var url = Str(root, "webpage_url") ?? requestedUrl;
        var platform = UrlTools.DetectPlatform(url) is var p && p != Platform.Other ? p : UrlTools.DetectPlatform(requestedUrl);

        if (Str(root, "_type") is "playlist" or "multi_video")
        {
            var entries = new List<PlaylistEntry>();
            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue; // deleted / private entries come back as null
                    var entryUrl = Str(e, "webpage_url") ?? Str(e, "url");
                    if (entryUrl is null) continue;
                    if (!entryUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        entryUrl = platform == Platform.YouTube ? $"https://www.youtube.com/watch?v={entryUrl}" : entryUrl;
                    entries.Add(new PlaylistEntry
                    {
                        Index = entries.Count + 1,
                        Url = entryUrl,
                        Title = Str(e, "title") ?? L.Format("Playlist_EntryFallback", entries.Count + 1),
                        Duration = Num(e, "duration"),
                        ThumbnailUrl = PickThumbnail(e),
                    });
                }
            }
            return new PlaylistInfo
            {
                Url = url,
                Title = Str(root, "title") ?? L.Get("Untitled"),
                Uploader = Str(root, "uploader") ?? Str(root, "channel"),
                ThumbnailUrl = PickThumbnail(root) ?? entries.FirstOrDefault(e => e.ThumbnailUrl is not null)?.ThumbnailUrl,
                Platform = platform,
                Entries = entries,
            };
        }

        var duration = Num(root, "duration");
        var (qualities, audioBytes) = Qualities(root, duration);
        return new VideoInfo
        {
            Url = url,
            Title = Str(root, "title") ?? L.Get("Untitled"),
            Uploader = Str(root, "uploader") ?? Str(root, "channel"),
            Duration = duration,
            ThumbnailUrl = PickThumbnail(root),
            UploadDate = Str(root, "upload_date"),
            Platform = platform,
            Qualities = qualities,
            AudioBytes = audioBytes,
        };
    }

    /// <summary>One option per height (highest first), with the best fps seen and an approximate
    /// size (largest video stream at that height + the best audio stream).</summary>
    static (List<QualityOption>, long?) Qualities(JsonElement root, double? duration)
    {
        var byHeight = new Dictionary<int, (int Fps, long? Bytes)>();
        long? audio = null;
        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
            return ([], null);

        foreach (var f in formats.EnumerateArray())
        {
            var vcodec = Str(f, "vcodec");
            var acodec = Str(f, "acodec");
            var size = Size(f, duration);
            if (vcodec is "none")
            {
                if (acodec is not "none" && size is > 0 && (audio is null || size > audio)) audio = size;
                continue;
            }
            var height = (int)(Num(f, "height") ?? 0);
            if (height <= 0) continue;
            var fps = (int)Math.Round(Num(f, "fps") ?? 0);
            byHeight.TryGetValue(height, out var cur);
            byHeight[height] = (Math.Max(cur.Fps, fps), Max(cur.Bytes, size));
        }

        var list = byHeight.OrderByDescending(kv => kv.Key)
            .Select(kv => new QualityOption(kv.Key, kv.Value.Fps, kv.Value.Bytes is > 0 ? kv.Value.Bytes + (audio ?? 0) : null))
            .ToList();
        return (list, audio);
    }

    static long? Size(JsonElement f, double? duration)
    {
        if (Num(f, "filesize") is > 0 and var s) return (long)s;
        if (Num(f, "filesize_approx") is > 0 and var a) return (long)a;
        // tbr is in kbit/s.
        if (Num(f, "tbr") is > 0 and var tbr && duration is > 0) return (long)(tbr * 1000 / 8 * duration.Value);
        return null;
    }

    static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    public static string? PickThumbnail(JsonElement root)
    {
        string? best = null;
        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            // yt-dlp sorts thumbnails worst → best; prefer the best JPEG since every Windows install decodes it.
            foreach (var t in thumbs.EnumerateArray())
            {
                var u = Str(t, "url");
                if (u is not null && u.Contains(".jpg", StringComparison.OrdinalIgnoreCase)) best = u;
            }
        }
        best ??= Str(root, "thumbnail");
        if (best?.StartsWith("//", StringComparison.Ordinal) == true) best = "https:" + best;
        if (best?.StartsWith("http://", StringComparison.Ordinal) == true) best = "https://" + best[7..];
        return best;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
