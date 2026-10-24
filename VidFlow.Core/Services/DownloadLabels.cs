using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Display text for download rows, built from the stored request so it follows the current language.</summary>
public static class DownloadLabels
{
    /// <summary>"YouTube · MP4 · 1080p · P3".</summary>
    public static string Label(DownloadRequest r, int? partIndex)
    {
        var mode = r.Mode switch
        {
            "mp3" => L.Get("Label_Mp3"),
            "m4a" => L.Get("Label_M4a"),
            _ => r.MaxHeight > 0 ? $"MP4 · {r.MaxHeight}p" : L.Get("Label_Mp4Best"),
        };
        if (r.Mode == "video" && VideoCodecs.ShortName(r.Codec) is { Length: > 0 } codec) mode += $" · {codec}";
        var label = $"{UrlTools.DetectPlatform(r.Url).DisplayName()}  ·  {mode}";
        return partIndex is { } p ? $"{label}  ·  P{p}" : label;
    }

    /// <summary>Status text for states that don't carry live progress; null for states whose text is specific
    /// (failures keep their error message).</summary>
    public static string? RestingStatus(DownloadRecord r) => r.State switch
    {
        DownloadState.Completed => r.FilePath is null ? L.Get("Status_Completed") : L.Format("Status_CompletedFile", Path.GetFileName(r.FilePath)),
        DownloadState.Canceled => L.Get("Status_Canceled"),
        DownloadState.Paused => L.Get("Status_Paused"),
        DownloadState.Queued => L.Get("Status_Resuming"),
        _ => null,
    };
}
