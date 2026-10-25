using System.Net;
using System.Text.Json;
using System.Web;

namespace VidFlow.Services;

public enum QrLoginState { Waiting, Scanned, Expired, Success }

/// <summary>A login QR code: the link it encodes and the key used to ask about it.</summary>
public sealed record QrCode(string Url, string Key);

public sealed record QrPollResult(QrLoginState State, IReadOnlyList<CookieRecord> Cookies);

/// <summary>
/// Bilibili's web QR login: get a code, show it, and ask every couple of seconds whether the Bilibili app has
/// scanned and confirmed it. On success the response sets the login cookies (SESSDATA, bili_jct, DedeUserID…).
/// </summary>
public static class BilibiliQrLogin
{
    const string GenerateUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    const string PollUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=";
    static readonly Uri Site = new("https://www.bilibili.com/");

    public static async Task<QrCode> GenerateAsync(CancellationToken ct)
    {
        using var client = Http.Create(Http.CreateHandler(), TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, GenerateUrl);
        req.Headers.Referrer = Site;
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var data = doc.RootElement.GetProperty("data");
        return new QrCode(data.GetProperty("url").GetString()!, data.GetProperty("qrcode_key").GetString()!);
    }

    public static async Task<QrPollResult> PollAsync(string key, CancellationToken ct)
    {
        // Its own cookie jar: the login cookies arrive as Set-Cookie headers on the successful poll.
        var handler = Http.CreateHandler();
        handler.CookieContainer = new CookieContainer();
        using var client = Http.Create(handler, TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, PollUrl + Uri.EscapeDataString(key));
        req.Headers.Referrer = Site;
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var (state, url) = ParsePoll(await resp.Content.ReadAsStringAsync(ct));
        if (state != QrLoginState.Success) return new QrPollResult(state, []);

        var cookies = handler.CookieContainer.GetAllCookies()
            .Select(c => new CookieRecord(c.Domain.StartsWith('.') ? c.Domain : "." + c.Domain, c.Path, c.Name, c.Value,
                c.Secure, c.HttpOnly, c.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(c.Expires).ToUnixTimeSeconds()))
            .ToList();
        // The redirect URL carries the same values; use it for anything the headers didn't set.
        foreach (var c in CookiesFromUrl(url))
            if (!cookies.Any(x => x.Name == c.Name)) cookies.Add(c);
        return new QrPollResult(QrLoginState.Success, cookies);
    }

    /// <summary>The poll answer's inner code: 0 signed in, 86090 scanned (waiting for confirmation on the phone),
    /// 86038 expired, 86101 (and anything else) not scanned yet.</summary>
    public static (QrLoginState State, string Url) ParsePoll(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var state = data.GetProperty("code").GetInt32() switch
        {
            0 => QrLoginState.Success,
            86090 => QrLoginState.Scanned,
            86038 => QrLoginState.Expired,
            _ => QrLoginState.Waiting,
        };
        return (state, data.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "");
    }

    /// <summary>Login cookies from the success URL's query (DedeUserID, SESSDATA, bili_jct…), valid until its Expires.</summary>
    public static IReadOnlyList<CookieRecord> CookiesFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return [];
        var query = HttpUtility.ParseQueryString(uri.Query);
        // Normally an absolute Unix time; a small number would be a lifetime in seconds.
        var expires = !long.TryParse(query["Expires"], out var e) ? 0
            : e > 1_000_000_000 ? e
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + e;
        return query.AllKeys
            .Where(k => k is "DedeUserID" or "DedeUserID__ckMd5" or "SESSDATA" or "bili_jct" or "sid")
            .Select(k => new CookieRecord(".bilibili.com", "/", k!, query[k]!, false, k == "SESSDATA", expires))
            .ToList();
    }
}
