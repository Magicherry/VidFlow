using System.Net;

namespace VidFlow.Services;

/// <summary>Shared HttpClient that honours the proxy configured in settings.</summary>
public static class Http
{
    static HttpClient? _client;
    static string? _clientProxy;

    public static HttpClient Client
    {
        get
        {
            var proxy = AppSettings.Current.Proxy.Trim();
            if (_client is null || _clientProxy != proxy)
            {
                _client = Create(CreateHandler());
                _clientProxy = proxy;
            }
            return _client;
        }
    }

    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    /// <summary>A handler with the configured proxy (for clients that need their own cookies).</summary>
    public static HttpClientHandler CreateHandler()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var proxy = AppSettings.Current.Proxy.Trim();
        if (proxy.Length > 0)
        {
            try
            {
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
            }
            catch (UriFormatException)
            {
                // Invalid proxy string: fall back to the system proxy.
            }
        }
        return handler;
    }

    public static HttpClient Create(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
