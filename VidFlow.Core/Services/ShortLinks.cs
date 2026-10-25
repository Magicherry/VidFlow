namespace VidFlow.Services;

/// <summary>
/// Turns share links into the page links the downloaders understand: Douyin (v.douyin.com, share pages, feed links)
/// to douyin.com/video/ID, Weibo (t.cn, video.weibo.com/show?fid=) to weibo.com/tv/show/ID, and Xiaohongshu
/// (xhslink.com) to its note page, keeping the access token the link carries.
/// </summary>
public static class ShortLinks
{
    public static async Task<string> NormalizeAsync(string url, CancellationToken ct)
    {
        if (UrlTools.Canonical(url) is { } direct) return direct;
        if (!UrlTools.IsShortLink(url)) return url;

        // Follow the shortener's redirects by hand, only as far as the first page on the platform itself: going
        // further would land on its login / visitor check page instead of the video.
        var handler = Http.CreateHandler();
        handler.AllowAutoRedirect = false;
        using var client = Http.Create(handler, TimeSpan.FromSeconds(15));
        var current = new Uri(url);
        for (var hop = 0; hop < 5 && UrlTools.IsShortLink(current.ToString()); hop++)
        {
            using var resp = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.Headers.Location is not { } next) break;
            current = next.IsAbsoluteUri ? next : new Uri(current, next);
        }
        var resolved = current.ToString();
        if (resolved == url) Log.Info($"Short link didn't redirect anywhere: {url}");
        return UrlTools.Canonical(resolved) ?? resolved;
    }
}
