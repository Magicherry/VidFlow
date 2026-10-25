namespace VidFlow.Services;

/// <summary>Turns any Douyin link (short link, share page, feed with modal_id) into douyin.com/video/ID.</summary>
public static class DouyinLinks
{
    public static async Task<string> NormalizeAsync(string url, CancellationToken ct)
    {
        if (UrlTools.CanonicalDouyinUrl(url) is { } direct) return direct;
        if (!UrlTools.IsDouyinShortLink(url)) return url;

        // v.douyin.com/xxxx redirects to iesdouyin.com/share/video/ID/…; follow the hops by hand to read the id
        // without loading the page.
        var handler = Http.CreateHandler();
        handler.AllowAutoRedirect = false;
        using var client = Http.Create(handler, TimeSpan.FromSeconds(15));
        var current = new Uri(url);
        for (var hop = 0; hop < 5; hop++)
        {
            using var resp = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.Headers.Location is not { } next) break;
            current = next.IsAbsoluteUri ? next : new Uri(current, next);
            if (UrlTools.CanonicalDouyinUrl(current.ToString()) is { } found) return found;
        }
        Log.Info($"Douyin short link didn't lead to a video id: {url} -> {current}");
        return url;
    }
}
