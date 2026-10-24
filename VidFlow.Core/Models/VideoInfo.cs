using VidFlow.Localization;

namespace VidFlow.Models;

public enum Platform { YouTube, Bilibili, Other }

public static class PlatformExtensions
{
    public static string DisplayName(this Platform p) => p switch
    {
        Platform.YouTube => "YouTube",
        Platform.Bilibili => L.Get("Platform_Bilibili"),
        _ => L.Get("Platform_Other"),
    };
}

/// <summary>One selectable resolution, with what yt-dlp reported about it.</summary>
public sealed record QualityOption(int Height, int Fps, long? EstimatedBytes);

/// <summary>Result of parsing a link: either a single video or a playlist / multi-part video.</summary>
public abstract class MediaInfo
{
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string? Uploader { get; init; }
    public string? ThumbnailUrl { get; init; }
    public Platform Platform { get; init; }
}

public sealed class VideoInfo : MediaInfo
{
    public double? Duration { get; init; }
    public string? UploadDate { get; init; }

    /// <summary>Available resolutions, highest first.</summary>
    public IReadOnlyList<QualityOption> Qualities { get; init; } = [];

    /// <summary>Estimated size of the best audio-only stream (for audio downloads).</summary>
    public long? AudioBytes { get; init; }

    /// <summary>Video codecs offered (see <c>VideoCodecs</c>) and the highest resolution available in each.</summary>
    public IReadOnlyDictionary<string, int> Codecs { get; init; } = new Dictionary<string, int>();
}

public sealed class PlaylistInfo : MediaInfo
{
    public IReadOnlyList<PlaylistEntry> Entries { get; init; } = [];
}

public sealed class PlaylistEntry
{
    public required int Index { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public double? Duration { get; init; }
    public string? ThumbnailUrl { get; init; }
}

/// <summary>What the user chose to download.</summary>
public sealed record DownloadRequest(
    string Url,
    string Mode,          // video, mp3, m4a
    int MaxHeight,        // 0 = best
    bool WriteSubtitles,
    string OutputFolder,
    string Codec = "auto"); // auto, h264, h265, av1, vp9 (see VideoCodecs)
