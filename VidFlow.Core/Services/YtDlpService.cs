using System.Diagnostics;
using System.Text;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>A yt-dlp failure: <see cref="Exception.Message"/> is user-facing, <see cref="Detail"/> is the raw output.</summary>
public sealed class YtDlpException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}

/// <summary>Runs yt-dlp. Argument building and output parsing live in <see cref="YtDlpArgs"/> / <see cref="YtDlpOutputParser"/>.</summary>
public static class YtDlpService
{
    /// <summary>Raised (on a background thread) when browser cookies could not be read and were skipped.</summary>
    public static event Action? BrowserCookiesFailed;

    /// <summary>The browser whose cookies failed to read this session (reset when the setting changes).</summary>
    static volatile string? _unreadableBrowser;

    /// <summary>
    /// Douyin: given douyin.com/video/ID, returns the JSON of its "aweme/detail" API. Douyin signs those requests
    /// in page script (which yt-dlp can't do), so the app loads the page in a hidden WebView2 and reads the answer.
    /// </summary>
    public static Func<string, CancellationToken, Task<string>>? FetchDouyinDetail { get; set; }

    const string DouyinReferer = "https://www.douyin.com/";

    static async Task<DouyinVideo> GetDouyinAsync(string url, CancellationToken ct)
    {
        if (FetchDouyinDetail is not { } fetch) throw new YtDlpException(L.Get("Error_DouyinUnavailable"));
        if (UrlTools.CanonicalDouyinUrl(url) is null) throw new YtDlpException(L.Get("Error_DouyinNotVideo"));
        return DouyinParser.Parse(await fetch(url, ct));
    }

    public static async Task<MediaInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        url = await ShortLinks.NormalizeAsync(url, ct);
        if (UrlTools.DetectPlatform(url) == Platform.Douyin) return DouyinParser.ToVideoInfo(await GetDouyinAsync(url, ct));
        var stdout = new StringBuilder();
        var (code, error) = await RunWithCookieFallbackAsync(url,
            env => { stdout.Clear(); return YtDlpArgs.Info(url, AppSettings.Current, env); },
            line => stdout.AppendLine(line), ct);
        if (code != 0) throw new YtDlpException(ErrorMapper.Friendly(error), error);

        var info = InfoParser.Parse(stdout.ToString(), url);
        if (info is PlaylistInfo { Entries.Count: 0 })
            throw new YtDlpException(L.Get("Error_EmptyPlaylist"));
        if (info is PlaylistInfo { Platform: Platform.Bilibili } parts)
            info = await BilibiliApi.EnrichAsync(parts, ct);
        return info;
    }

    /// <summary>Runs the download, reporting events as they happen. Returns the final file path when yt-dlp reports one.</summary>
    public static async Task<string?> DownloadAsync(DownloadRequest req, Action<DownloadEvent> onEvent, CancellationToken ct)
    {
        if (req.IsAudio && ToolManager.FfmpegPath is null)
            throw new YtDlpException(L.Get("Error_AudioNeedsFfmpeg"));
        req = req with { Url = await ShortLinks.NormalizeAsync(req.Url, ct) };
        Directory.CreateDirectory(req.OutputFolder);

        string? filePath = null;
        var parser = new YtDlpOutputParser();
        void OnLine(string line)
        {
            if (parser.Feed(line) is not { } e) return;
            if (e is FileEvent f) filePath = f.Path;
            onEvent(e);
        }

        int code;
        string error;
        if (UrlTools.DetectPlatform(req.Url) == Platform.Douyin)
        {
            // Stream addresses expire, so look them up again right before downloading (also after a restart).
            var video = await GetDouyinAsync(req.Url, ct);
            var stream = DouyinParser.Pick(video, req.MaxHeight, req.Codec);
            var date = video.UploadDate is { Length: 8 } d ? $"{d[..4]}-{d[4..6]}-{d[6..]}" : null;
            var known = new Dictionary<string, string?>
            {
                ["title"] = video.Title,
                ["uploader"] = video.Author ?? "Unknown",
                ["date"] = date ?? "unknown",
                ["year"] = date?[..4] ?? "unknown",
                ["month"] = date?[5..7] ?? "00",
                ["resolution"] = req.IsCover ? "cover" : req.IsAudio ? "audio" : $"{stream.Resolution}p",
                ["id"] = video.Id,
            };
            var ffmpegDir = ToolManager.FfmpegPath is { } ff ? Path.GetDirectoryName(ff) : null;
            // The media host needs no cookies; only the Referer.
            var env = new YtDlpArgs.Environment(ffmpegDir, null, SkipBrowserCookies: true);
            // The cover is a plain image address too, downloaded the same way.
            var media = req.IsCover ? video.Cover ?? throw new YtDlpException(L.Get("Error_NoCover")) : stream.Url;
            var direct = new YtDlpArgs.DirectMedia(media, DouyinReferer, known);
            (code, error) = await RunAsync(YtDlpArgs.Download(req with { Url = $"https://www.douyin.com/video/{video.Id}" }, AppSettings.Current, env, direct), OnLine, ct);
        }
        else
        {
            if (req.IsAudio && req.AudioOutputFormat == MediaFeatures.Original)
                req = req with { AudioFormat = await OriginalAudioFormatAsync(req, ct) };
            (code, error) = await RunWithCookieFallbackAsync(req.Url,
                env => YtDlpArgs.Download(req, AppSettings.Current, env), OnLine, ct, accelerate: true);
        }

        ct.ThrowIfCancellationRequested();
        if (code != 0) throw new YtDlpException(ErrorMapper.Friendly(error), error);
        return filePath;
    }

    /// <summary>"Original" audio: which codec the track has decides the file type (Hi-Res FLAC → .flac). Falls back to
    /// yt-dlp's "best" if the question can't be answered.</summary>
    static async Task<string> OriginalAudioFormatAsync(DownloadRequest req, CancellationToken ct)
    {
        string? codec = null;
        var (code, _) = await RunWithCookieFallbackAsync(req.Url,
            env => YtDlpArgs.ProbeAudio(req, AppSettings.Current, env),
            line => codec ??= line.Trim().Length > 0 ? line.Trim() : null, ct);
        var format = code == 0 ? MediaFeatures.OriginalOutput(codec) : MediaFeatures.Original;
        Log.Info($"Original audio: {codec ?? "?"} -> {format}");
        return format;
    }

    /// <summary>
    /// Runs yt-dlp with cookie fallbacks:
    /// <list type="bullet">
    /// <item>If the configured browser's cookies can't be read (Chrome/Edge lock them while running), retry
    /// without them and skip them for the rest of the session.</item>
    /// <item>YouTube: signed-in requests often come back with no downloadable formats (YouTube treats them
    /// differently, and exported cookies get rotated), so try without the in-app login first and only use it
    /// when YouTube says sign-in is needed (age / members-only / bot check). Bilibili always uses the login.</item>
    /// </list>
    /// </summary>
    static async Task<(int Code, string Error)> RunWithCookieFallbackAsync(
        string url, Func<YtDlpArgs.Environment, List<string>> buildArgs, Action<string> onLine, CancellationToken ct,
        bool accelerate = false)
    {
        var ffmpegDir = ToolManager.FfmpegPath is { } ff ? Path.GetDirectoryName(ff) : null;
        var browser = AppSettings.Current.CookiesBrowser;
        var loginFile = AccountStore.LoginCookieFileOrNull;
        var youTubeLogin = loginFile is not null && UrlTools.DetectPlatform(url) == Platform.YouTube
                           && AccountStore.IsLoggedIn(Platform.YouTube);
        var env = new YtDlpArgs.Environment(ffmpegDir, loginFile,
            SkipBrowserCookies: _unreadableBrowser == browser,
            SkipLoginCookies: youTubeLogin,
            DenoPath: ToolManager.DenoPath);
        if (accelerate) env = Accelerated(env, url);

        var result = await RunAsync(buildArgs(env), onLine, ct);

        if (result.Code != 0 && !env.SkipBrowserCookies && YtDlpArgs.UsesBrowserCookies(AppSettings.Current)
            && ErrorMapper.IsCookieReadError(result.Error))
        {
            Log.Warn($"Browser cookies unreadable, skipping them this session: {ErrorMapper.LastErrorLine(result.Error)}");
            _unreadableBrowser = browser;
            BrowserCookiesFailed?.Invoke();
            env = env with { SkipBrowserCookies = true };
            result = await RunAsync(buildArgs(env), onLine, ct);
        }

        if (result.Code != 0 && youTubeLogin && env.SkipLoginCookies && ErrorMapper.NeedsSignIn(result.Error))
        {
            Log.Info($"YouTube needs sign-in, retrying with the in-app login: {ErrorMapper.LastErrorLine(result.Error)}");
            env = env with { SkipLoginCookies = false };
            result = await RunAsync(buildArgs(env), onLine, ct);
        }

        // aria2c can trip over what yt-dlp's own downloader copes with (a host refusing parallel connections, an
        // unusual proxy): fall back to one connection rather than fail.
        if (result.Code != 0 && env.Aria2Path is not null && ErrorMapper.IsAria2Failure(result.Error))
        {
            Log.Warn($"aria2c failed, retrying with yt-dlp's own downloader: {ErrorMapper.LastErrorLine(result.Error)}");
            result = await RunAsync(buildArgs(env with { Aria2Path = null }), onLine, ct);
        }
        return result;
    }

    /// <summary>Adds aria2 (when installed and switched on) and the proxy Windows would use for <paramref name="url"/>.
    /// Not for the Chinese platforms: their CDNs are fast on one connection, and some of Bilibili's nodes answer a
    /// burst of connections with 503.</summary>
    static YtDlpArgs.Environment Accelerated(YtDlpArgs.Environment env, string url)
    {
        if (!AppSettings.Current.MultiConnection || ToolManager.Aria2Path is not { } aria2) return env;
        if (UrlTools.DetectPlatform(url) is Platform.Bilibili or Platform.Douyin or Platform.Weibo or Platform.Xiaohongshu) return env;
        string? proxy = null;
        try
        {
            var target = new Uri(url);
            // Environment variables (HTTPS_PROXY) first, then Windows' own proxy setting; null when bypassed.
            if (HttpClient.DefaultProxy.GetProxy(target) is { } p && p != target) proxy = p.GetLeftPart(UriPartial.Authority);
        }
        catch (Exception ex)
        {
            Log.Info($"System proxy lookup failed: {ex.Message}");
        }
        return env with { Aria2Path = aria2, SystemProxy = proxy };
    }

    /// <summary>Runs yt-dlp, streaming stdout lines to <paramref name="onLine"/>. Returns exit code and stderr.</summary>
    static async Task<(int Code, string Error)> RunAsync(List<string> args, Action<string> onLine, CancellationToken ct)
    {
        var exe = ToolManager.YtDlpPath ?? throw new YtDlpException(L.Get("Error_NoYtDlp"));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);
        Log.Info("yt-dlp " + string.Join(' ', args.Select(a => a.Contains(' ') || a.Contains('|') ? $"\"{a}\"" : a)));

        using var p = new Process { StartInfo = psi };
        var errors = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errors)
                if (errors.Length < 16000) errors.AppendLine(e.Data);
        };

        p.Start();
        ProcessJob.Add(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });

        await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        string error;
        lock (errors) error = errors.ToString().Trim();
        if (p.ExitCode != 0) Log.Warn($"yt-dlp exited {p.ExitCode}: {ErrorMapper.LastErrorLine(error)}");
        return (p.ExitCode, error);
    }
}
