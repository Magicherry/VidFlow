using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Display text for download rows, built from the stored request so it follows the current language.</summary>
public static class DownloadLabels
{
    /// <summary>"YouTube · MP4 · 1080p · P3".</summary>
    public static string Label(DownloadRequest r, int? partIndex)
    {
        string mode;
        if (r.IsAudio)
        {
            mode = L.Format("Label_Audio", r.AudioOutputFormat.ToUpperInvariant());
        }
        else
        {
            var container = (string.IsNullOrEmpty(r.Container) ? "mp4" : r.Container).ToUpperInvariant();
            mode = r.MaxHeight > 0 ? $"{container} · {r.MaxHeight}p" : $"{container} · {L.Get("Quality_Best")}";
            if (VideoCodecs.ShortName(r.Codec) is { Length: > 0 } codec) mode += $" · {codec}";
        }
        var label = $"{UrlTools.DetectPlatform(r.Url).DisplayName()}  ·  {mode}";
        return partIndex is { } p ? $"{label}  ·  P{p}" : label;
    }

    /// <summary>One-line description of a template: "MP4 · 1080p · H.264 · AAC · Subtitles".</summary>
    public static string Summary(DownloadTemplate t)
    {
        var parts = new List<string>();
        if (t.Mode == "audio")
        {
            parts.Add(L.Format("Label_Audio", (t.AudioFormat is "auto" ? "mp3" : t.AudioFormat).ToUpperInvariant()));
            if (t.AudioQuality > 0 && t.AudioFormat is not ("flac" or "wav")) parts.Add($"{t.AudioQuality} kbps");
        }
        else
        {
            parts.Add(t.Container.ToUpperInvariant());
            parts.Add(t.MaxHeight > 0 ? $"{t.MaxHeight}p" : L.Get("Quality_Best"));
            if (VideoCodecs.ShortName(t.Codec) is { Length: > 0 } codec) parts.Add(codec);
            if (t.AudioFormat is "aac" or "opus") parts.Add(t.AudioFormat == "aac" ? "AAC" : "Opus");
            if (t.AudioQuality > 0) parts.Add($"{t.AudioQuality} kbps");
        }
        if (t.AudioLanguage.Length > 0) parts.Add(t.AudioLanguage);
        if (t.Subtitles) parts.Add(L.Get("Options_Subtitles"));
        if (t.FolderTemplate.Length > 0) parts.Add(t.FolderTemplate.Replace('/', '\\'));
        return string.Join(" · ", parts);
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
