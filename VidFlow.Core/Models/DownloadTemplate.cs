using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Models;

/// <summary>
/// A reusable set of download options. Built-in presets are read-only (duplicate them to customize);
/// the user's own templates are stored in templates.json. The home page edits a copy, so a per-task change
/// never alters the template itself.
/// </summary>
public sealed class DownloadTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Name of a user template.</summary>
    public string Name { get; set; } = "";

    /// <summary>Localization key for built-in presets (their name follows the UI language).</summary>
    public string? NameKey { get; set; }

    public bool IsBuiltIn => NameKey is not null;
    public string DisplayName => NameKey is null ? Name : L.Get(NameKey);

    public string Mode { get; set; } = "video";            // video | audio | cover
    public int MaxHeight { get; set; }                     // 0 = best (original)
    public string Codec { get; set; } = VideoCodecs.Auto;

    /// <summary>Picture: auto (HDR when offered), dv (Dolby Vision) or sdr. See <see cref="MediaFeatures"/>.</summary>
    public string DynamicRange { get; set; } = MediaFeatures.Auto;

    public string Container { get; set; } = "mp4";
    public string AudioFormat { get; set; } = "auto";
    public int AudioQuality { get; set; }
    public string AudioLanguage { get; set; } = "";
    public bool Recode { get; set; }
    public bool Subtitles { get; set; }
    public string FileNameTemplate { get; set; } = NamingTemplate.DefaultFileName;
    public string FolderTemplate { get; set; } = "";

    public DownloadTemplate Clone() => new()
    {
        Id = Id,
        Name = Name,
        NameKey = NameKey,
        Mode = Mode,
        MaxHeight = MaxHeight,
        Codec = Codec,
        DynamicRange = DynamicRange,
        Container = Container,
        AudioFormat = AudioFormat,
        AudioQuality = AudioQuality,
        AudioLanguage = AudioLanguage,
        Recode = Recode,
        Subtitles = Subtitles,
        FileNameTemplate = FileNameTemplate,
        FolderTemplate = FolderTemplate,
    };

    /// <summary>True when the download options (not the name) differ.</summary>
    public bool SameOptionsAs(DownloadTemplate o) =>
        Mode == o.Mode && MaxHeight == o.MaxHeight && Codec == o.Codec && DynamicRange == o.DynamicRange && Container == o.Container
        && AudioFormat == o.AudioFormat && AudioQuality == o.AudioQuality && AudioLanguage == o.AudioLanguage
        && Recode == o.Recode && Subtitles == o.Subtitles && FileNameTemplate == o.FileNameTemplate
        && FolderTemplate == o.FolderTemplate;

    public DownloadRequest ToRequest(string url, string baseFolder) =>
        new(url, Mode is "audio" or "cover" ? Mode : "video", Mode == "video" ? MaxHeight : 0, Subtitles && Mode != "cover", baseFolder,
            Codec, Container, AudioFormat, AudioQuality, AudioLanguage, Recode, FileNameTemplate, FolderTemplate, DynamicRange);
}
