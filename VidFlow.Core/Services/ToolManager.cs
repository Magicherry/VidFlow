using System.Diagnostics;
using System.IO.Compression;
using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>Locates, installs and updates the external tools (yt-dlp, ffmpeg).</summary>
public static class ToolManager
{
    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public static string BinDir => Path.Combine(AppSettings.DataDir, "bin");

    /// <summary>The app's own copy wins over one found on PATH.</summary>
    public static string? YtDlpPath => Find("yt-dlp.exe");
    public static string? FfmpegPath => Find("ffmpeg.exe");
    /// <summary>JavaScript runtime yt-dlp uses for YouTube.</summary>
    public static string? DenoPath => Find("deno.exe");

    const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    public static bool IsYtDlpManaged => File.Exists(Path.Combine(BinDir, "yt-dlp.exe"));
    public static bool IsManaged(string exe) => exe.StartsWith(BinDir, StringComparison.OrdinalIgnoreCase);

    static string? Find(string exe)
    {
        var local = Path.Combine(BinDir, exe);
        if (File.Exists(local)) return local;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    public static async Task<string?> GetVersionAsync(string? exe, string arg)
    {
        if (exe is null) return null;
        var output = await RunAsync(exe, [arg]);
        var line = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (line is null) return null;
        // "ffmpeg version N-12345-g... Copyright ..." -> "N-12345-g..."
        if (line.StartsWith("ffmpeg version ", StringComparison.Ordinal))
            line = line["ffmpeg version ".Length..].Split(' ')[0];
        return line;
    }

    /// <summary>
    /// Keeps yt-dlp current, since sites change often and an outdated yt-dlp is the most common cause of failures.
    /// At most once a day: self-updates the app's own copy, or installs one if only a PATH copy exists.
    /// </summary>
    public static async Task AutoUpdateYtDlpAsync(CancellationToken ct = default)
    {
        var s = AppSettings.Current;
        if (!s.AutoUpdateYtDlp || DateTime.Now - s.LastYtDlpUpdateCheck < TimeSpan.FromDays(1)) return;
        try
        {
            if (IsYtDlpManaged)
            {
                var args = new List<string> { "-U" };
                if (s.Proxy.Trim().Length > 0) args.AddRange(["--proxy", s.Proxy.Trim()]);
                var output = await RunAsync(Path.Combine(BinDir, "yt-dlp.exe"), args, ct);
                Log.Info($"yt-dlp -U: {output?.Trim()}");
            }
            else if (YtDlpPath is not null)
            {
                await InstallYtDlpAsync(null, ct);
                Log.Info("Installed managed yt-dlp copy (replacing PATH copy).");
            }
            else
            {
                return; // Nothing installed yet: the first-run wizard / settings page handles that with consent.
            }
            s.LastYtDlpUpdateCheck = DateTime.Now;
            AppSettings.Save();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"yt-dlp auto-update failed: {ex.Message}");
        }
    }

    static (string Version, DateTime At)? _latestYtDlp;

    /// <summary>Latest yt-dlp release tag from GitHub (e.g. "2026.10.21"), cached for 10 minutes; null if unreachable.</summary>
    public static async Task<string?> GetLatestYtDlpVersionAsync(bool refresh = false)
    {
        if (!refresh && _latestYtDlp is { } c && DateTime.Now - c.At < TimeSpan.FromMinutes(10)) return c.Version;
        try
        {
            // github.com/.../releases/latest redirects to .../releases/tag/<version>. Unlike the REST API this is
            // not limited to 60 requests/hour per IP, which shared proxy exit IPs exhaust quickly.
            using var resp = await Http.Client.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest",
                HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var tag = TagFromReleaseUrl(resp.RequestMessage?.RequestUri);
            if (tag is null) return null;
            _latestYtDlp = (tag, DateTime.Now);
            return tag;
        }
        catch (Exception ex)
        {
            Log.Info($"Latest yt-dlp version check failed: {ex.Message}");
            return null;
        }
    }

    static (DateOnly Date, DateTime At)? _latestFfmpeg;

    /// <summary>Build date of the latest ffmpeg (the date the release asset was uploaded), cached for 10 minutes;
    /// null if unreachable. The builds are nightly, so the date is what identifies a version.</summary>
    public static async Task<DateOnly?> GetLatestFfmpegDateAsync(bool refresh = false)
    {
        if (!refresh && _latestFfmpeg is { } c && DateTime.Now - c.At < TimeSpan.FromMinutes(10)) return c.Date;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, FfmpegUrl);
            using var resp = await Http.Client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.LastModified is not { } modified) return null;
            var date = DateOnly.FromDateTime(modified.UtcDateTime);
            _latestFfmpeg = (date, DateTime.Now);
            return date;
        }
        catch (Exception ex)
        {
            Log.Info($"Latest ffmpeg check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>"N-128018-ga881f0a9ad-20261024" → 2026-10-24.</summary>
    public static DateOnly? FfmpegBuildDate(string? version)
    {
        var m = System.Text.RegularExpressions.Regex.Match(version ?? "", @"-(\d{8})$");
        return m.Success && DateOnly.TryParseExact(m.Groups[1].Value, "yyyyMMdd", out var d) ? d : null;
    }

    public static bool IsFfmpegUpToDate(string? installedVersion, DateOnly? latest) =>
        FfmpegBuildDate(installedVersion) is { } installed && latest is { } l && installed >= l;

    /// <summary>".../releases/tag/2026.10.21" → "2026.10.21".</summary>
    public static string? TagFromReleaseUrl(Uri? url)
    {
        var path = url?.AbsolutePath ?? "";
        var i = path.IndexOf("/releases/tag/", StringComparison.Ordinal);
        return i < 0 ? null : Uri.UnescapeDataString(path[(i + "/releases/tag/".Length)..].Trim('/'));
    }

    /// <summary>True when <paramref name="installed"/> is the same as or newer than <paramref name="latest"/>
    /// (yt-dlp versions are dates like 2026.10.21, nightlies add a fourth part).</summary>
    public static bool IsUpToDate(string? installed, string? latest)
    {
        if (installed is null || latest is null) return false;
        return Version.TryParse(installed.Trim(), out var a) && Version.TryParse(latest.Trim(), out var b)
            ? a >= b
            : string.Equals(installed.Trim(), latest.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    static (string Version, DateTime At)? _latestDeno;

    /// <summary>Latest deno release version ("2.5.4"), cached for 10 minutes; null if unreachable.</summary>
    public static async Task<string?> GetLatestDenoVersionAsync(bool refresh = false)
    {
        if (!refresh && _latestDeno is { } c && DateTime.Now - c.At < TimeSpan.FromMinutes(10)) return c.Version;
        try
        {
            using var resp = await Http.Client.GetAsync("https://github.com/denoland/deno/releases/latest",
                HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var tag = TagFromReleaseUrl(resp.RequestMessage?.RequestUri)?.TrimStart('v');
            if (tag is null) return null;
            _latestDeno = (tag, DateTime.Now);
            return tag;
        }
        catch (Exception ex)
        {
            Log.Info($"Latest deno check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>"deno 2.5.4 (stable, release, x86_64-pc-windows-msvc)" → "2.5.4".</summary>
    public static string? DenoVersion(string? versionLine) =>
        versionLine?.StartsWith("deno ", StringComparison.Ordinal) == true ? versionLine.Split(' ')[1] : versionLine;

    public static async Task InstallDenoAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(BinDir);
        var zipPath = Path.Combine(BinDir, "deno.zip.download");
        await DownloadFileAsync(DenoUrl, zipPath, progress, ct);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(L.Format("Error_ZipMissing", "deno.exe"));
            entry.ExtractToFile(Path.Combine(BinDir, "deno.exe"), overwrite: true);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    public static async Task InstallYtDlpAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(BinDir);
        var tmp = Path.Combine(BinDir, "yt-dlp.exe.download");
        await DownloadFileAsync(YtDlpUrl, tmp, progress, ct);
        File.Move(tmp, Path.Combine(BinDir, "yt-dlp.exe"), overwrite: true);
    }

    public static async Task InstallFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(BinDir);
        var zipPath = Path.Combine(BinDir, "ffmpeg.zip.download");
        await DownloadFileAsync(FfmpegUrl, zipPath, progress, ct);
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(L.Format("Error_ZipMissing", name));
                entry.ExtractToFile(Path.Combine(BinDir, name), overwrite: true);
            }
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    static async Task<string?> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            ProcessJob.Add(p);
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return await stdout + await stderr;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Running {Path.GetFileName(exe)} failed: {ex.Message}");
            return null;
        }
    }

    static async Task DownloadFileAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using (var dst = File.Create(dest))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(done * 100.0 / total);
            }
        }
    }
}
