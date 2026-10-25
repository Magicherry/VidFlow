using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Models;

public enum Platform { YouTube, Bilibili, Douyin, Weibo, Xiaohongshu, X, Other }

public static class PlatformExtensions
{
    /// <summary>The platforms VidFlow supports, in the order they are listed.</summary>
    public static readonly Platform[] Supported =
        [Platform.Bilibili, Platform.Douyin, Platform.Xiaohongshu, Platform.Weibo, Platform.YouTube, Platform.X];

    public static string DisplayName(this Platform p) => p switch
    {
        Platform.YouTube => "YouTube",
        Platform.Bilibili => L.Get("Platform_Bilibili"),
        Platform.Douyin => L.Get("Platform_Douyin"),
        Platform.Weibo => L.Get("Platform_Weibo"),
        Platform.Xiaohongshu => L.Get("Platform_Xiaohongshu"),
        Platform.X => "X",
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

    /// <summary>Audio track languages offered (YouTube multi-language audio); the original track first.</summary>
    public IReadOnlyList<string> AudioLanguages { get; init; } = [];

    /// <summary>High dynamic range kinds offered ("DV", "HDR10", "HDR10+", "HLG"), best first; empty when SDR only.</summary>
    public IReadOnlyList<string> HdrKinds { get; init; } = [];

    /// <summary>A Hi-Res lossless (FLAC) audio track is offered (Bilibili, with membership).</summary>
    public bool HasHiRes { get; init; }

    /// <summary>A Dolby Atmos (E-AC-3) audio track is offered (Bilibili).</summary>
    public bool HasAtmos { get; init; }

    public bool HasHdr => HdrKinds.Any(k => k != "DV");
    public bool HasDolbyVision => HdrKinds.Contains("DV");

    /// <summary>Badges for the preview: picture kinds first, then sound.</summary>
    public IEnumerable<string> Features =>
        HdrKinds.Concat(HasHiRes ? [MediaFeatures.HiRes] : []).Concat(HasAtmos ? [MediaFeatures.Atmos] : []);
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

/// <summary>What the user chose to download. Usually produced from a <see cref="DownloadTemplate"/>; the optional
/// parameters have defaults so download lists saved by older versions still load.</summary>
public sealed record DownloadRequest(
    string Url,
    string Mode,                      // video | audio (older lists may contain mp3 / m4a, meaning audio)
    int MaxHeight,                    // 0 = best
    bool WriteSubtitles,
    string OutputFolder,              // base folder; FolderTemplate subfolders go inside it
    string Codec = "auto",            // auto, h264, h265, av1, vp9 (see VideoCodecs)
    string Container = "mp4",         // mp4, mkv, webm
    string AudioFormat = "auto",      // video: auto / aac / opus;  audio: mp3 / m4a / opus / flac / wav
    int AudioQuality = 0,             // kbps; 0 = best
    string AudioLanguage = "",        // "" = default (original) track, else a language code
    bool Recode = false,              // re-encode when the streams don't fit the container
    string FileNameTemplate = NamingTemplate.DefaultFileName,
    string FolderTemplate = "",
    string DynamicRange = MediaFeatures.Auto) // auto, dv, sdr (see MediaFeatures)
{
    public bool IsAudio => Mode is "audio" or "mp3" or "m4a";

    /// <summary>Output format for audio-only downloads ("best" keeps the source's own format).</summary>
    public string AudioOutputFormat => Mode switch
    {
        "mp3" or "m4a" => Mode,
        _ => AudioFormat is "mp3" or "m4a" or "opus" or "flac" or "wav" or MediaFeatures.Original ? AudioFormat : "mp3",
    };

    /// <summary>The picture or sound chosen can't go in MP4 (yt-dlp would merge into MKV anyway): say MKV up front.</summary>
    public bool NeedsMkv => !IsAudio && (DynamicRange == MediaFeatures.DolbyVision || AudioFormat == MediaFeatures.HiRes);
}
