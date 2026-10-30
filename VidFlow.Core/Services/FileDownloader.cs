using System.Net;
using System.Net.Http.Headers;

namespace VidFlow.Services;

/// <summary>
/// Downloads a file over several connections at once. Through many proxies (and from GitHub's release CDN) a single
/// connection's speed swings widely, from tens of MB/s down to well under 1 MB/s; spreading the file over several
/// connections keeps it fast. The file is cut into chunks that a few workers take in turn, so one slow connection
/// only holds up its current chunk. Servers that don't support ranges get a plain single-connection download.
/// </summary>
public static class FileDownloader
{
    public const int DefaultConnections = 8;
    /// <summary>Below this, one connection is as quick as setting up several.</summary>
    const long MinSplitSize = 4 << 20;
    /// <summary>Attempts in a row that bring in nothing before a chunk gives up.</summary>
    const int ChunkAttempts = 4;
    /// <summary>A chunk that takes longer than this carries on from where it got to on a fresh connection.</summary>
    static readonly TimeSpan ChunkTimeout = TimeSpan.FromMinutes(2);

    public static async Task DownloadAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct,
        int connections = DefaultConnections, HttpClient? client = null)
    {
        client ??= Http.Client;
        // Ask for the whole file as a range: a 206 answer says ranges work and gives the size, and the final
        // address after redirects (GitHub sends release downloads on to a signed CDN address).
        using var probe = new HttpRequestMessage(HttpMethod.Get, url);
        probe.Headers.Range = new RangeHeaderValue(0, null);
        var resp = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.StatusCode == HttpStatusCode.PartialContent ? resp.Content.Headers.ContentRange?.Length ?? 0 : 0;
            if (connections <= 1 || total < MinSplitSize)
            {
                await SingleAsync(resp, dest, progress, ct);
                return;
            }
            var target = resp.RequestMessage?.RequestUri ?? new Uri(url);
            resp.Dispose();
            await SplitAsync(client, url, target, total, dest, progress, connections, ct);
        }
        finally
        {
            resp.Dispose();
        }
    }

    static async Task SingleAsync(HttpResponseMessage resp, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        var total = resp.Content.Headers.ContentLength ?? 0;
        var report = new Reporter(progress, total);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            report.Add(read);
        }
        report.Done();
    }

    static async Task SplitAsync(HttpClient client, string url, Uri target, long total, string dest,
        IProgress<double>? progress, int connections, CancellationToken ct)
    {
        // About 32 chunks (1–8 MB each): enough that the workers keep swapping slow connections for new ones.
        var chunkSize = Math.Clamp(total / 32, 1L << 20, 8L << 20);
        var chunks = new Queue<(long From, long To)>();
        for (long from = 0; from < total; from += chunkSize) chunks.Enqueue((from, Math.Min(from + chunkSize, total) - 1));
        var report = new Reporter(progress, total);
        var gate = new object();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var handle = File.OpenHandle(dest, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous);
        RandomAccess.SetLength(handle, total);

        async Task Worker()
        {
            var buffer = new byte[81920];
            while (true)
            {
                (long From, long To) chunk;
                lock (gate)
                {
                    if (!chunks.TryDequeue(out chunk)) return;
                }
                var pos = chunk.From;
                for (var attempt = 1; ; attempt++)
                {
                    var started = pos;
                    using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    chunkCts.CancelAfter(ChunkTimeout);
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, target);
                        req.Headers.Range = new RangeHeaderValue(pos, chunk.To);
                        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, chunkCts.Token);
                        resp.EnsureSuccessStatusCode();
                        if (resp.StatusCode != HttpStatusCode.PartialContent)
                            throw new IOException($"Server ignored the range request ({(int)resp.StatusCode})");
                        await using var src = await resp.Content.ReadAsStreamAsync(chunkCts.Token);
                        int read;
                        while (pos <= chunk.To && (read = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, chunk.To - pos + 1)), chunkCts.Token)) > 0)
                        {
                            await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), pos, cts.Token);
                            pos += read;
                            report.Add(read);
                        }
                        if (pos <= chunk.To) throw new IOException("Connection closed early");
                        break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
                                               && (pos > started || attempt < ChunkAttempts) && !cts.IsCancellationRequested)
                    {
                        // Slow is not stuck: as long as data arrives, keep going.
                        if (pos > started) attempt = 0;
                        Log.Info($"Chunk {chunk.From}-{chunk.To} attempt {attempt} failed: {ex.Message}");
                        // Signed CDN addresses expire after a few minutes: ask the original address again.
                        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.NotFound })
                            target = await ResolveAsync(client, url, cts.Token) ?? target;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, attempt)), cts.Token);
                    }
                }
            }
        }

        // One chunk giving up fails the download: stop the others rather than fetch the rest for nothing.
        async Task Guarded()
        {
            try
            {
                await Worker();
            }
            catch
            {
                cts.Cancel();
                throw;
            }
        }

        await Task.WhenAll(Enumerable.Range(0, (int)Math.Min(connections, chunks.Count)).Select(_ => Guarded()).ToList());
        report.Done();
    }

    static async Task<Uri?> ResolveAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode ? resp.RequestMessage?.RequestUri : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Totals the bytes from all connections and reports at most every 100 ms, so a fast download
    /// doesn't flood the UI thread with progress updates.</summary>
    sealed class Reporter(IProgress<double>? progress, long total)
    {
        long _done;
        long _lastTick;

        public void Add(int bytes)
        {
            var done = Interlocked.Add(ref _done, bytes);
            if (progress is null || total <= 0) return;
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastTick);
            if (now - last < 100 || Interlocked.CompareExchange(ref _lastTick, now, last) != last) return;
            progress.Report(Math.Min(100, done * 100.0 / total));
        }

        public void Done() => progress?.Report(100);
    }
}
