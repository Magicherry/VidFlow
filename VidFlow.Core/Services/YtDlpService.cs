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

    public static async Task<MediaInfo> GetInfoAsync(string url, CancellationToken ct)
    {
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
        if (req.Mode != "video" && ToolManager.FfmpegPath is null)
            throw new YtDlpException(L.Get("Error_AudioNeedsFfmpeg"));
        Directory.CreateDirectory(req.OutputFolder);

        string? filePath = null;
        var parser = new YtDlpOutputParser();
        var (code, error) = await RunWithCookieFallbackAsync(req.Url,
            env => YtDlpArgs.Download(req, AppSettings.Current, env),
            line =>
            {
                if (parser.Feed(line) is not { } e) return;
                if (e is FileEvent f) filePath = f.Path;
                onEvent(e);
            }, ct);

        ct.ThrowIfCancellationRequested();
        if (code != 0) throw new YtDlpException(ErrorMapper.Friendly(error), error);
        return filePath;
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
        string url, Func<YtDlpArgs.Environment, List<string>> buildArgs, Action<string> onLine, CancellationToken ct)
    {
        var ffmpegDir = ToolManager.FfmpegPath is { } ff ? Path.GetDirectoryName(ff) : null;
        var browser = AppSettings.Current.CookiesBrowser;
        var loginFile = AccountStore.LoginCookieFileOrNull;
        var youTubeLogin = loginFile is not null && UrlTools.DetectPlatform(url) == Platform.YouTube
                           && AccountStore.IsLoggedIn(Platform.YouTube);
        var env = new YtDlpArgs.Environment(ffmpegDir, loginFile,
            SkipBrowserCookies: _unreadableBrowser == browser,
            SkipLoginCookies: youTubeLogin);

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
            result = await RunAsync(buildArgs(env with { SkipLoginCookies = false }), onLine, ct);
        }
        return result;
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
