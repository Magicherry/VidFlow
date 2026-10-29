using System.Diagnostics;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>Outcome of one reachability check.</summary>
public sealed record ConnectivityResult(bool Reachable, TimeSpan Latency, string? Error, DateTime CheckedAt);

/// <summary>
/// Whether sites outside the Great Firewall (YouTube, and with it X) can be reached with the current proxy setting.
/// An HTTPS request rather than a ping: proxies such as Clash only carry TCP, so ICMP fails even when downloads work.
/// Any HTTP answer counts as reachable; a timeout or a connection error does not.
/// </summary>
public static class Connectivity
{
    /// <summary>YouTube's own "no content" address: tiny, quick, and only answers when YouTube itself is reachable.</summary>
    public static readonly Uri YouTubeProbe = new("https://www.youtube.com/generate_204");

    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
    static int _checking;

    /// <summary>The last YouTube check this run; null until one has finished.</summary>
    public static ConnectivityResult? YouTube { get; private set; }

    public static bool Checking => Volatile.Read(ref _checking) > 0;

    /// <summary>Platforms blocked in mainland China, so reachable only through a proxy there; the YouTube check
    /// stands for them all.</summary>
    public static bool NeedsProxy(Platform p) => p is Platform.YouTube or Platform.X;

    /// <summary>The last check says YouTube can't be reached.</summary>
    public static bool YouTubeUnreachable => YouTube is { Reachable: false };

    /// <summary>Raised (on a background thread) when a check starts or finishes.</summary>
    public static event Action? Changed;

    /// <summary>Checks YouTube through VidFlow's HTTP client (the proxy from settings, else the system proxy).</summary>
    public static async Task<ConnectivityResult> CheckYouTubeAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _checking);
        Changed?.Invoke();
        try
        {
            var result = await CheckAsync(YouTubeProbe, Http.Client, DefaultTimeout, ct);
            YouTube = result;
            Log.Info(result.Reachable
                ? $"YouTube reachable in {result.Latency.TotalMilliseconds:0} ms"
                : $"YouTube unreachable: {result.Error}");
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _checking);
            Changed?.Invoke();
        }
    }

    public static async Task<ConnectivityResult> CheckAsync(Uri url, HttpClient client, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return new(true, watch.Elapsed, null, DateTime.Now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, watch.Elapsed, $"no answer within {timeout.TotalSeconds:0} s", DateTime.Now);
        }
        catch (HttpRequestException ex)
        {
            return new(false, watch.Elapsed, ex.Message, DateTime.Now);
        }
    }
}
