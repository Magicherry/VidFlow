using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>A problem with the chosen container / codec combination and the suggested fix.</summary>
/// <param name="MessageKey">Localization key of the explanation.</param>
/// <param name="SuggestedContainer">Container that would work as-is, if switching is the fix.</param>
/// <param name="SuggestedAudioFormat">Audio format that would work as-is, if switching is the fix.</param>
/// <param name="Info">Not a problem, just what will happen (e.g. "saved as MKV"): shown as information, not a warning.</param>
public sealed record CompatibilityIssue(string MessageKey, string? SuggestedContainer = null, string? SuggestedAudioFormat = null,
    bool Info = false);

/// <summary>
/// Which codecs each container can hold. Downloads never fail over it (yt-dlp falls back to MKV, or re-encodes
/// when "convert automatically" is on); this is to warn and recommend in the UI before downloading.
/// </summary>
public static class FormatCompatibility
{
    public static readonly string[] Containers = ["mp4", "mkv", "webm"];

    /// <param name="video">The parsed video, when known: what it offers (HDR, Hi-Res) changes the advice.</param>
    public static CompatibilityIssue? Check(DownloadTemplate t, VideoInfo? video = null)
    {
        if (t.Mode == "audio")
        {
            var hiRes = video?.HasHiRes == true;
            if (t.AudioFormat is "flac" or "wav")
                return hiRes ? null : new CompatibilityIssue("Compat_LosslessFromLossy");
            // A lossy output from a Hi-Res source throws the lossless track away.
            return hiRes && t.AudioFormat != MediaFeatures.Original
                ? new CompatibilityIssue("Compat_HiResAvailable", SuggestedAudioFormat: "flac")
                : null;
        }
        if (t.Container != "mkv")
        {
            // Dolby Vision and Hi-Res FLAC only fit MKV; downloads go there automatically, so this is just a heads-up.
            if (t.DynamicRange == MediaFeatures.DolbyVision || t.AudioFormat == MediaFeatures.HiRes)
                return new CompatibilityIssue("Compat_SavedAsMkv", SuggestedContainer: "mkv", Info: true);
            // HDR is mostly VP9 / AV1 / HEVC with 10-bit colour, which yt-dlp usually can't merge into MP4.
            if (video?.HasHdr == true && t.DynamicRange == MediaFeatures.Auto && t.Container == "mp4")
                return new CompatibilityIssue("Compat_HdrMkv", SuggestedContainer: "mkv", Info: true);
        }
        if (t.Container == "webm")
        {
            if (t.Codec is "h264" or "h265")
                return new CompatibilityIssue("Compat_WebmVideo", SuggestedContainer: "mkv");
            if (t.AudioFormat == "aac")
                return new CompatibilityIssue("Compat_WebmAudio", SuggestedAudioFormat: "opus");
        }
        if (t.Container == "mp4" && t.AudioFormat == "opus")
            return new CompatibilityIssue("Compat_Mp4Opus", SuggestedAudioFormat: "aac");
        return null;
    }

    /// <summary>yt-dlp --merge-output-format: the chosen container, falling back to MKV (which holds anything)
    /// when the selected streams don't fit it.</summary>
    public static string MergeFormats(string container) => container switch
    {
        "mkv" => "mkv",
        "webm" => "webm/mkv",
        _ => "mp4/mkv",
    };
}
