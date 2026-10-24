using System.Text.RegularExpressions;
using VidFlow.Models;

namespace VidFlow.Services;

public static partial class UrlTools
{
    [GeneratedRegex(@"https?://[^\s""'<>，。【】]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Pulls the first URL out of pasted share text such as "【标题】 https://b23.tv/xxx".</summary>
    public static string? ExtractUrl(string? text) => ExtractUrls(text).FirstOrDefault();

    /// <summary>All distinct URLs in the text, in order (for batch pasting).</summary>
    public static IReadOnlyList<string> ExtractUrls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return UrlRegex().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', ']')).Distinct().ToList();
    }

    public static bool IsSupported(string? url) => url is not null && DetectPlatform(url) != Platform.Other;

    public static Platform DetectPlatform(string url)
    {
        var u = url.ToLowerInvariant();
        if (u.Contains("youtube.com") || u.Contains("youtu.be")) return Platform.YouTube;
        if (u.Contains("bilibili.com") || u.Contains("b23.tv")) return Platform.Bilibili;
        return Platform.Other;
    }

    /// <summary>A YouTube watch link that also carries a playlist (watch?v=…&amp;list=…): we treat it as the single video.</summary>
    public static bool IsYouTubeVideoInPlaylist(string url) =>
        DetectPlatform(url) == Platform.YouTube && url.Contains("v=", StringComparison.Ordinal) && url.Contains("list=", StringComparison.Ordinal);
}
