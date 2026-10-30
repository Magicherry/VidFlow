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
        if (r.IsCover)
        {
            mode = L.Get("Mode_Cover");
        }
        else if (r.IsAudio)
        {
            mode = AudioOutputLabel(r.AudioOutputFormat);
        }
        else
        {
            var container = (r.NeedsMkv ? "mkv" : string.IsNullOrEmpty(r.Container) ? "mp4" : r.Container).ToUpperInvariant();
            mode = r.MaxHeight > 0 ? $"{container} · {r.MaxHeight}p" : $"{container} · {L.Get("Quality_Best")}";
            if (VideoCodecs.ShortName(r.Codec) is { Length: > 0 } codec) mode += $" · {codec}";
            if (PictureLabel(r.DynamicRange) is { } picture) mode += $" · {picture}";
            if (SoundLabel(r.AudioFormat) is { } sound) mode += $" · {sound}";
        }
        var label = $"{UrlTools.DetectPlatform(r.Url).DisplayName()}  ·  {mode}";
        return partIndex is { } p ? $"{label}  ·  P{p}" : label;
    }

    /// <summary>One-line description of a template that spells out its choices, defaults included:
    /// "Video · MP4 · 1080p · H.264 · AAC · No subtitles", "Audio only · MP3 · Best available".</summary>
    public static string Summary(DownloadTemplate t)
    {
        var parts = new List<string>();
        if (t.Mode == "cover")
        {
            parts.Add(L.Get("Mode_Cover"));
        }
        else if (t.Mode == "audio")
        {
            var format = t.AudioFormat is "auto" ? "mp3" : t.AudioFormat;
            if (format == MediaFeatures.Original)
            {
                parts.Add(L.Get("Label_AudioOriginal"));
            }
            else
            {
                parts.Add(L.Get("Mode_Audio"));
                parts.Add(format.ToUpperInvariant());
                if (format is not ("flac" or "wav"))
                    parts.Add(t.AudioQuality > 0 ? $"{t.AudioQuality} kbps" : L.Get("AudioQuality_Best"));
            }
            if (t.Subtitles) parts.Add(L.Get("Options_Subtitles"));
        }
        else
        {
            parts.Add(L.Get("Mode_Video"));
            parts.Add(t.Container.ToUpperInvariant());
            parts.Add(t.MaxHeight > 0 ? $"{t.MaxHeight}p" : L.Get("Quality_Best"));
            parts.Add(VideoCodecs.ShortName(t.Codec) is { Length: > 0 } codec ? codec : L.Get("Label_CodecAuto"));
            if (PictureLabel(t.DynamicRange) is { } picture) parts.Add(picture);
            if (t.AudioFormat is "aac" or "opus") parts.Add(t.AudioFormat == "aac" ? "AAC" : "Opus");
            if (SoundLabel(t.AudioFormat) is { } sound) parts.Add(sound);
            if (t.AudioQuality > 0) parts.Add($"{t.AudioQuality} kbps");
            parts.Add(L.Get(t.Subtitles ? "Options_Subtitles" : "Label_NoSubtitles"));
        }
        if (t.Mode != "cover" && t.AudioLanguage.Length > 0) parts.Add(t.AudioLanguage);
        if (t.FolderTemplate.Length > 0) parts.Add(t.FolderTemplate.Replace('/', '\\'));
        return string.Join(" · ", parts);
    }

    /// <summary>"MP3 audio", or "Original audio" when the source format is kept.</summary>
    static string AudioOutputLabel(string format) =>
        format == MediaFeatures.Original ? L.Get("Label_AudioOriginal") : L.Format("Label_Audio", format.ToUpperInvariant());

    /// <summary>Only the non-default picture choices are worth a word in a summary.</summary>
    static string? PictureLabel(string? picture) => picture switch
    {
        MediaFeatures.DolbyVision => MediaFeatures.Label("DV"),
        MediaFeatures.Sdr => "SDR",
        _ => null,
    };

    static string? SoundLabel(string? audioFormat) => audioFormat switch
    {
        MediaFeatures.HiRes or MediaFeatures.Atmos => MediaFeatures.Label(audioFormat),
        _ => null,
    };

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
