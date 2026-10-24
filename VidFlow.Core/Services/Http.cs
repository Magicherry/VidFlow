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
                var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
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
                var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
                _client = client;
                _clientProxy = proxy;
            }
            return _client;
        }
    }
}
