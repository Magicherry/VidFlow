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
        if (u.Contains("douyin.com")) return Platform.Douyin;
        return Platform.Other;
    }

    [GeneratedRegex(@"(?:/video/|/share/video/|[?&]modal_id=|[?&]vid=)(\d{15,20})")]
    private static partial Regex DouyinIdRegex();

    /// <summary>
    /// yt-dlp only understands douyin.com/video/ID. Douyin links come in several other shapes: the feed
    /// (douyin.com/jingxuan?modal_id=ID), share pages (iesdouyin.com/share/video/ID) and short links
    /// (v.douyin.com/xxxx, see <see cref="DouyinLinks"/>). Returns the canonical link, or null if there is no video id.
    /// </summary>
    public static string? CanonicalDouyinUrl(string url)
    {
        var m = DouyinIdRegex().Match(url);
        return m.Success ? $"https://www.douyin.com/video/{m.Groups[1].Value}" : null;
    }

    public static bool IsDouyinShortLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("v.douyin.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>A YouTube watch link that also carries a playlist (watch?v=…&amp;list=…): we treat it as the single video.</summary>
    public static bool IsYouTubeVideoInPlaylist(string url) =>
        DetectPlatform(url) == Platform.YouTube && url.Contains("v=", StringComparison.Ordinal) && url.Contains("list=", StringComparison.Ordinal);
}
