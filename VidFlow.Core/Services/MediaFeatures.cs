using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>
/// High-end picture and sound: HDR (HDR10 / HDR10+ / HLG), Dolby Vision, Hi-Res lossless (FLAC) and Dolby Atmos
/// (E-AC-3). Recognising them in yt-dlp's format list, the choices offered, and how a choice steers yt-dlp.
/// </summary>
public static class MediaFeatures
{
    // ---- Picture (DownloadTemplate.DynamicRange) -----------------------------------------------------------------

    /// <summary>HDR10 / HLG when offered, otherwise SDR; not Dolby Vision (see <see cref="DolbyVision"/>).</summary>
    public const string Auto = "auto";

    /// <summary>Dolby Vision when offered. Not the default: Bilibili's profile 5 has no HDR10 base layer, so players
    /// without Dolby Vision support show it with green / purple colours.</summary>
    public const string DolbyVision = "dv";

    /// <summary>Standard dynamic range, for ordinary screens and players.</summary>
    public const string Sdr = "sdr";

    public static readonly string[] Pictures = [Auto, DolbyVision, Sdr];

    /// <summary>yt-dlp's "hdr" sort field (DV &gt; HDR12 &gt; HDR10+ &gt; HDR10 &gt; HLG &gt; SDR), capped per choice.
    /// Compatibility mode (H.264) means SDR: H.264 is practically never HDR.</summary>
    public static string HdrSort(string picture, bool compatibilityMode) => picture switch
    {
        DolbyVision => "hdr",
        Sdr => "hdr:SDR",
        _ when compatibilityMode => "hdr:SDR",
        _ => "hdr:HDR10",
    };

    // ---- Sound (DownloadTemplate.AudioFormat, video mode) ------------------------------------------------------

    public const string HiRes = "hires";
    public const string Atmos = "atmos";

    /// <summary>Audio-only output that keeps the source as it is (FLAC stays FLAC).</summary>
    public const string Original = "best";

    public static bool IsHiResCodec(string? acodec) => acodec?.ToLowerInvariant() is "flac" or "alac";

    /// <summary>The audio-only output for "original": the source codec in its own file type (FLAC → .flac, AAC → .m4a,
    /// Opus → .opus, MP3 → .mp3), so nothing is re-encoded; anything else is left to yt-dlp ("best").</summary>
    public static string OriginalOutput(string? acodec) => acodec?.Trim().ToLowerInvariant() switch
    {
        "flac" or "alac" => "flac",
        "opus" => "opus",
        "mp3" => "mp3",
        { } a when a.StartsWith("mp4a") || a.StartsWith("aac") => "m4a",
        _ => Original,
    };

    public static bool IsAtmosCodec(string? acodec) =>
        acodec?.ToLowerInvariant() is { } a && (a.StartsWith("ec-3") || a.StartsWith("eac3") || a.StartsWith("ac-4"));

    // ---- What a video offers -----------------------------------------------------------------------------------

    /// <summary>yt-dlp's dynamic_range values ("HDR10", "DV", …), or null for SDR / unknown.</summary>
    public static string? HdrKind(string? dynamicRange) =>
        dynamicRange is null or "" || dynamicRange.Equals("SDR", StringComparison.OrdinalIgnoreCase) ? null : dynamicRange.ToUpperInvariant();

    public static string Label(string feature) => feature switch
    {
        "DV" => L.Get("Feature_DolbyVision"),
        HiRes => L.Get("Feature_HiRes"),
        Atmos => L.Get("Feature_Atmos"),
        _ => feature, // HDR10, HDR10+, HLG, HDR12 read the same in every language
    };
}
