using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>Video codec choices: "auto" (best quality, any codec), "h264", "h265", "av1", "vp9".</summary>
public static class VideoCodecs
{
    public const string Auto = "auto";

    /// <summary>In the order offered to the user (most compatible first).</summary>
    public static readonly string[] All = ["h264", "h265", "av1", "vp9"];

    /// <summary>Maps a yt-dlp vcodec string ("avc1.640032", "hev1.1.6…", "av01.0.08M.08", "vp09.00…", "vp9") to a choice.</summary>
    public static string? Family(string? vcodec)
    {
        var v = vcodec?.ToLowerInvariant() ?? "";
        if (v.StartsWith("avc") || v.StartsWith("h264")) return "h264";
        // Dolby Vision streams are named after their base codec: dvh1 / dvhe (HEVC), dav1 (AV1).
        if (v.StartsWith("hev") || v.StartsWith("hvc") || v.StartsWith("h265") || v.StartsWith("dvh")) return "h265";
        if (v.StartsWith("av01") || v == "av1" || v.StartsWith("dav1")) return "av1";
        if (v.StartsWith("vp09") || v.StartsWith("vp9")) return "vp9";
        return null;
    }

    /// <summary>yt-dlp format filter selecting video streams of the codec ("" for auto).</summary>
    public static string Filter(string codec) => codec switch
    {
        "h264" => "[vcodec~='^(avc|h264)']",
        "h265" => "[vcodec~='^(hev|hvc|h265|dvh)']",
        "av1" => "[vcodec~='^(av01|dav1)']",
        "vp9" => "[vcodec~='^vp0?9']",
        _ => "",
    };

    public static string DisplayName(string codec) => codec switch
    {
        "h264" => "H.264 (AVC)",
        "h265" => "H.265 (HEVC)",
        "av1" => "AV1",
        "vp9" => "VP9",
        _ => L.Get("Codec_Auto"),
    };

    /// <summary>Short form for list labels: "H.264".</summary>
    public static string ShortName(string codec) => codec switch
    {
        "h264" => "H.264",
        "h265" => "H.265",
        "av1" => "AV1",
        "vp9" => "VP9",
        _ => "",
    };
}
