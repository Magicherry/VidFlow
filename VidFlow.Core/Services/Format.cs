using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

public static class Format
{
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes:0} {units[i]}" : $"{bytes:0.0} {units[i]}";
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>"20240131" → "2024-01-31".</summary>
    public static string? Date(string? yyyymmdd) =>
        yyyymmdd is { Length: 8 } d ? $"{d[..4]}-{d[4..6]}-{d[6..]}" : null;

    /// <summary>"1080p60 (Full HD)".</summary>
    public static string Height(int h, int fps = 0)
    {
        var name = fps >= 50 ? $"{h}p{fps}" : $"{h}p";
        var tag = h switch
        {
            >= 4320 => "8K",
            >= 2160 => "4K",
            >= 1440 => "2K",
            >= 1080 => L.Get("Quality_FullHD"),
            >= 720 => L.Get("Quality_HD"),
            _ => null,
        };
        return tag is null ? name : $"{name} ({tag})";
    }

    /// <summary>Quality label with an approximate size: "1080p (Full HD) · ~120 MB".</summary>
    public static string Quality(QualityOption q) =>
        q.EstimatedBytes is > 0 ? $"{Height(q.Height, q.Fps)}  ·  {L.Format("Size_Approx", Bytes(q.EstimatedBytes.Value))}" : Height(q.Height, q.Fps);
}
