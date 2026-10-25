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

    /// <summary>Each platform's domains, including its link shorteners.</summary>
    static readonly (Platform Platform, string[] Domains)[] Domains =
    [
        (Platform.YouTube, ["youtube.com", "youtu.be", "youtube-nocookie.com"]),
        (Platform.Bilibili, ["bilibili.com", "b23.tv"]),
        (Platform.Douyin, ["douyin.com", "iesdouyin.com"]),
        (Platform.Weibo, ["weibo.com", "weibo.cn", "t.cn"]),
        (Platform.Xiaohongshu, ["xiaohongshu.com", "xhslink.com"]),
        (Platform.X, ["x.com", "twitter.com", "t.co"]),
    ];

    /// <summary>Shorteners whose redirects VidFlow follows itself (yt-dlp can't: they end on a login or visitor
    /// check page, or need the id rewritten).</summary>
    static readonly string[] ShortLinkHosts = ["v.douyin.com", "t.cn", "xhslink.com"];

    /// <summary>By host name: "x.com" must not match netflix.com.</summary>
    public static Platform DetectPlatform(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return Platform.Other;
        var host = uri.Host.ToLowerInvariant();
        foreach (var (platform, domains) in Domains)
            if (domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal)))
                return platform;
        return Platform.Other;
    }

    public static bool IsShortLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && ShortLinkHosts.Contains(u.Host.ToLowerInvariant());

    [GeneratedRegex(@"(?:/video/|/share/video/|[?&]modal_id=|[?&]vid=)(\d{15,20})")]
    private static partial Regex DouyinIdRegex();

    /// <summary>
    /// yt-dlp only understands douyin.com/video/ID. Douyin links come in several other shapes: the feed
    /// (douyin.com/jingxuan?modal_id=ID), share pages (iesdouyin.com/share/video/ID) and short links
    /// (v.douyin.com/xxxx, see <see cref="ShortLinks"/>). Returns the canonical link, or null if there is no video id.
    /// </summary>
    public static string? CanonicalDouyinUrl(string url)
    {
        var m = DouyinIdRegex().Match(url);
        return m.Success ? $"https://www.douyin.com/video/{m.Groups[1].Value}" : null;
    }

    [GeneratedRegex(@"(?:/tv/show/|[?&]fid=)(\d+(?::|%3A)\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WeiboVideoIdRegex();

    /// <summary>Weibo's old video page (video.weibo.com/show?fid=1034:ID, where its t.cn links lead) becomes the
    /// weibo.com/tv/show/1034:ID page yt-dlp reads. Other Weibo links (posts) are left alone.</summary>
    public static string? CanonicalWeiboUrl(string url)
    {
        var m = WeiboVideoIdRegex().Match(url);
        return m.Success ? $"https://weibo.com/tv/show/{m.Groups[1].Value.Replace("%3A", ":").Replace("%3a", ":")}" : null;
    }

    /// <summary>The link yt-dlp (or VidFlow's Douyin reader) should get, when it can be rewritten without the network.</summary>
    public static string? Canonical(string url) => DetectPlatform(url) switch
    {
        Platform.Douyin => CanonicalDouyinUrl(url),
        Platform.Weibo => CanonicalWeiboUrl(url),
        _ => null,
    };

    /// <summary>A YouTube watch link that also carries a playlist (watch?v=…&amp;list=…): we treat it as the single video.</summary>
    public static bool IsYouTubeVideoInPlaylist(string url) =>
        DetectPlatform(url) == Platform.YouTube && url.Contains("v=", StringComparison.Ordinal) && url.Contains("list=", StringComparison.Ordinal);
}
