using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>Published next to each release as update.json.</summary>
public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size, string Notes, DateTime Published)
{
    public Version? ParsedVersion => System.Version.TryParse(Version, out var v) ? v : null;
}

/// <summary>Written just before the installer runs, so the next start can tell whether the update took.</summary>
public sealed record PendingUpdate(string Version, DateTime StartedAt);

/// <summary>Outcome of the last update attempt, reported once at startup.</summary>
public enum UpdateOutcome { None, Succeeded, Failed }

/// <summary>
/// App self-update: read the release manifest, download the installer (with retries), verify its size and SHA-256,
/// run it silently, and on the next start check that the new version is actually running. If the installer fails,
/// Inno Setup rolls its changes back and the old version keeps running; that is detected and reported here.
/// </summary>
public static class UpdateService
{
    /// <summary>VIDFLOW_UPDATE_MANIFEST overrides the source (an http(s) URL or a local file) for testing.</summary>
    public static string ManifestUrl =>
        Environment.GetEnvironmentVariable("VIDFLOW_UPDATE_MANIFEST") is { Length: > 0 } custom
            ? custom
            : "https://github.com/Magicherry/VidFlow/releases/latest/download/update.json";

    static string UpdatesDir => Path.Combine(AppSettings.DataDir, "updates");
    static string PendingFile => Path.Combine(UpdatesDir, "pending.json");

    public static async Task<UpdateManifest?> FetchManifestAsync(CancellationToken ct = default)
    {
        var url = ManifestUrl;
        var json = File.Exists(url)
            ? await File.ReadAllTextAsync(url, ct)
            : await Http.Client.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize(json, VidFlowJson.Default.UpdateManifest);
    }

    public static bool IsNewer(UpdateManifest m, Version current) => m.ParsedVersion is { } v && v > current;

    /// <summary>Downloads (or reuses an already verified copy of) the installer. Retries up to 3 times.</summary>
    public static async Task<string> DownloadAsync(UpdateManifest m, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        var file = Path.Combine(UpdatesDir, $"VidFlow-Setup-{m.Version}-x64.exe");
        if (File.Exists(file) && await VerifyAsync(file, m, ct)) return file;

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var tmp = file + ".download";
                if (File.Exists(m.Url)) File.Copy(m.Url, tmp, overwrite: true); // local manifest (testing)
                else await DownloadFileAsync(m.Url, tmp, progress, ct);
                if (!await VerifyAsync(tmp, m, ct))
                {
                    File.Delete(tmp);
                    throw new InvalidDataException(L.Get("Update_ChecksumMismatch"));
                }
                File.Move(tmp, file, overwrite: true);
                PruneOldInstallers(keep: file);
                return file;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 3)
            {
                last = ex;
                Log.Warn($"Update download attempt {attempt} failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
        throw last ?? new IOException(L.Get("Update_DownloadFailed"));
    }

    /// <summary>Size and SHA-256 must both match the manifest.</summary>
    public static async Task<bool> VerifyAsync(string file, UpdateManifest m, CancellationToken ct = default)
    {
        var info = new FileInfo(file);
        if (!info.Exists || (m.Size > 0 && info.Length != m.Size)) return false;
        await using var stream = File.OpenRead(file);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return hash.Equals(m.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Starts the installer silently. It closes VidFlow, installs, and relaunches it (/UPDATE=1).
    /// The caller must exit right after.</summary>
    public static void LaunchInstaller(string installer, UpdateManifest m)
    {
        Directory.CreateDirectory(UpdatesDir);
        File.WriteAllText(PendingFile, JsonSerializer.Serialize(new PendingUpdate(m.Version, DateTime.Now), VidFlowJson.Default.PendingUpdate));
        Log.Info($"Starting update installer for {m.Version}");
        // /CLOSEAPPLICATIONS: if VidFlow hasn't finished exiting yet, the installer closes it instead of waiting.
        Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /UPDATE=1") { UseShellExecute = true });
    }

    /// <summary>Call at startup: did the last update install? (Inno Setup rolls back a failed install, leaving the
    /// old version running, which is how a failure shows up here.)</summary>
    public static (UpdateOutcome Outcome, string? Version) CheckPendingResult(Version current)
    {
        try
        {
            if (!File.Exists(PendingFile)) return (UpdateOutcome.None, null);
            var pending = JsonSerializer.Deserialize(File.ReadAllText(PendingFile), VidFlowJson.Default.PendingUpdate);
            File.Delete(PendingFile);
            if (pending is null || !System.Version.TryParse(pending.Version, out var target)) return (UpdateOutcome.None, null);
            var outcome = current >= target ? UpdateOutcome.Succeeded : UpdateOutcome.Failed;
            Log.Info($"Update to {pending.Version}: {outcome}");
            return (outcome, pending.Version);
        }
        catch (Exception ex)
        {
            Log.Warn($"Pending update state unreadable: {ex.Message}");
            return (UpdateOutcome.None, null);
        }
    }

    static void PruneOldInstallers(string keep)
    {
        foreach (var f in Directory.GetFiles(UpdatesDir, "VidFlow-Setup-*.exe"))
            if (!f.Equals(keep, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(f); } catch (IOException) { }
    }

    static async Task DownloadFileAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
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
