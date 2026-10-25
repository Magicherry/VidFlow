using Microsoft.Web.WebView2.Core;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>In-app login via an embedded WebView2. Its cookies are exported to <see cref="AccountStore.CookieFile"/>
/// (read by yt-dlp); the sign-in itself persists in the WebView2 profile.</summary>
public static class AccountService
{
    public static string WebViewDataDir => Path.Combine(AppSettings.DataDir, "WebView2");

    static readonly string[] CookieSites = ["https://www.bilibili.com/", "https://www.youtube.com/"];

    public static string LoginUrl(Platform p) => p switch
    {
        Platform.Bilibili => "https://passport.bilibili.com/login",
        _ => "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F",
    };

    public static Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        CoreWebView2Environment.CreateWithOptionsAsync(null, WebViewDataDir, new CoreWebView2EnvironmentOptions()).AsTask();

    public static async Task<bool> HasSessionCookieAsync(CoreWebView2 webView, Platform p)
    {
        var site = p == Platform.Bilibili ? "https://www.bilibili.com/" : "https://www.youtube.com/";
        var names = AccountStore.SessionCookieNames(p);
        return (await webView.CookieManager.GetCookiesAsync(site)).Any(c => names.Contains(c.Name));
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
    public static async Task LogoutAsync(Platform p, IntPtr parentHwnd)
    {
        var env = await CreateEnvironmentAsync();
        var controller = await env.CreateCoreWebView2ControllerAsync(
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)parentHwnd));
        try
        {
            controller.IsVisible = false;
            var mgr = controller.CoreWebView2.CookieManager;
            var domain = p == Platform.Bilibili ? "bilibili.com" : "youtube.com";
            foreach (var c in await mgr.GetCookiesAsync($"https://www.{domain}/"))
                mgr.DeleteCookie(c);
            await ExportAsync(controller.CoreWebView2);
        }
        finally
        {
            controller.Close();
        }
    }
}
