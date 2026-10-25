using Microsoft.Web.WebView2.Core;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>In-app login via an embedded WebView2. Its cookies are exported to <see cref="AccountStore.CookieFile"/>
/// (read by yt-dlp); the sign-in itself persists in the WebView2 profile.</summary>
public static class AccountService
{
    public static string WebViewDataDir => Path.Combine(AppSettings.DataDir, "WebView2");

    static readonly string[] CookieSites = ["https://www.bilibili.com/", "https://www.youtube.com/", "https://www.douyin.com/"];

    /// <summary>One hidden Douyin page at a time (a batch parses several links at once).</summary>
    static readonly SemaphoreSlim DouyinLock = new(1, 1);

    static readonly TimeSpan DouyinTimeout = TimeSpan.FromSeconds(30);

    public static string LoginUrl(Platform p) => p switch
    {
        Platform.Bilibili => "https://passport.bilibili.com/login",
        Platform.Douyin => "https://www.douyin.com/",
        _ => "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F",
    };

    static string Site(Platform p) => $"https://www.{AccountStore.Domain(p)}/";

    public static Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        CoreWebView2Environment.CreateWithOptionsAsync(null, WebViewDataDir, new CoreWebView2EnvironmentOptions()).AsTask();

    public static async Task<bool> HasSessionCookieAsync(CoreWebView2 webView, Platform p)
    {
        var names = AccountStore.SessionCookieNames(p);
        return (await webView.CookieManager.GetCookiesAsync(Site(p))).Any(c => names.Contains(c.Name));
    }

    /// <summary>Writes the current WebView2 cookies for all supported sites to the cookie file.</summary>
    public static async Task ExportAsync(CoreWebView2 webView)
    {
        var all = new List<CookieRecord>();
        foreach (var site in CookieSites)
            foreach (var c in await webView.CookieManager.GetCookiesAsync(site))
                all.Add(new CookieRecord(c.Domain, c.Path, c.Name, c.Value, c.IsSecure, c.IsHttpOnly, c.IsSession ? 0 : (long)c.Expires));
        await AccountStore.WriteAsync(all);
    }

    /// <summary>Adds cookies obtained outside the browser (QR login) to the WebView2 profile, which stays the one
    /// source of truth: later exports and sign-outs then include them.</summary>
    public static void AddCookies(CoreWebView2 webView, IEnumerable<CookieRecord> cookies)
    {
        var mgr = webView.CookieManager;
        foreach (var c in cookies)
        {
            var cookie = mgr.CreateCookie(c.Name, c.Value, c.Domain, c.Path);
            cookie.IsHttpOnly = c.HttpOnly;
            cookie.IsSecure = c.Secure;
            if (c.Expires > 0) cookie.Expires = c.Expires;
            mgr.AddOrUpdateCookie(cookie);
        }
    }

    /// <summary>Signs out of one platform: clears its cookies from the WebView2 profile and re-exports.</summary>
    public static Task LogoutAsync(Platform p, IntPtr parentHwnd) => WithHiddenWebViewAsync(parentHwnd, async web =>
    {
        var mgr = web.CookieManager;
        foreach (var c in await mgr.GetCookiesAsync(Site(p)))
            mgr.DeleteCookie(c);
        await ExportAsync(web);
    });

    /// <summary>
    /// Opens a Douyin video page in a hidden WebView2 (the shared profile, so a Douyin sign-in applies) and returns
    /// the JSON of the "aweme/detail" request the page makes for it. Douyin signs that request in page script,
    /// which is why the page has to make it. The page is muted and its media requests blocked.
    /// </summary>
    public static async Task<string> FetchDouyinDetailAsync(IntPtr parentHwnd, string videoUrl, CancellationToken ct)
    {
        var id = videoUrl.TrimEnd('/').Split('/')[^1];
        await DouyinLock.WaitAsync(ct);
        try
        {
            return await WithHiddenWebViewAsync(parentHwnd, async web =>
            {
                var detail = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                web.IsMuted = true;
                web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
                web.WebResourceRequested += (_, e) =>
                    e.Response = web.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                web.WebResourceResponseReceived += async (_, e) =>
                {
                    var uri = e.Request.Uri;
                    if (!uri.Contains("/aweme/v1/web/aweme/detail/", StringComparison.Ordinal) || !uri.Contains(id, StringComparison.Ordinal)) return;
                    try
                    {
                        using var stream = (await e.Response.GetContentAsync()).AsStreamForRead();
                        var body = await new StreamReader(stream).ReadToEndAsync();
                        if (body.Length > 0) detail.TrySetResult(body);
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"Douyin detail unreadable: {ex.Message}");
                    }
                };
                web.Navigate(videoUrl);
                if (await Task.WhenAny(detail.Task, Task.Delay(DouyinTimeout, ct)) != detail.Task)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new YtDlpException(L.Get("Error_DouyinUnavailable"));
                }
                return await detail.Task;
            });
        }
        finally
        {
            DouyinLock.Release();
        }
    }

    static Task WithHiddenWebViewAsync(IntPtr parentHwnd, Func<CoreWebView2, Task> action) =>
        WithHiddenWebViewAsync(parentHwnd, async web =>
        {
            await action(web);
            return true;
        });

    static async Task<T> WithHiddenWebViewAsync<T>(IntPtr parentHwnd, Func<CoreWebView2, Task<T>> action)
    {
        var env = await CreateEnvironmentAsync();
        var controller = await env.CreateCoreWebView2ControllerAsync(
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)parentHwnd));
        try
        {
            controller.IsVisible = false;
            return await action(controller.CoreWebView2);
        }
        finally
        {
            controller.Close();
        }
    }
}
