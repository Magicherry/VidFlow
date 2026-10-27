using Microsoft.UI.Dispatching;
using VidFlow.Localization;

namespace VidFlow.Services;

public enum ToolState { Checking, UpToDate, UpdateAvailable, Missing, SystemCopy, CheckFailed, Installing }

/// <summary>One external component: how to find, version, check and install it, and where it stands.</summary>
public sealed class Component
{
    public required string Name { get; init; }
    public required string Glyph { get; init; }
    public required string PurposeKey { get; init; }
    public required string MissingKey { get; init; }
    public required bool Required { get; init; }
    public required Func<string?> Path { get; init; }
    public required Func<string?, Task<string?>> GetVersion { get; init; }
    public required Func<bool, Task<string?>> GetLatest { get; init; }
    public required Func<string?, string, bool> IsUpToDate { get; init; }
    public required Func<IProgress<double>, CancellationToken, Task> Install { get; init; }

    public ToolState State { get; internal set; }
    public string? Version { get; internal set; }
    public string? Latest { get; internal set; }
    /// <summary>Install progress in percent; null until the download reports its size.</summary>
    public double? Progress { get; internal set; }
}

/// <summary>
/// Checks and installs the external components (yt-dlp, ffmpeg, Deno, aria2) for the whole app, so an install keeps
/// going when the About page is left and shows its progress when it is opened again. With automatic updates on,
/// checks once a day in the background and updates VidFlow's own copies while nothing is downloading (a running
/// yt-dlp or ffmpeg can't be replaced). Missing components are only installed when the user asks.
/// </summary>
public sealed class ComponentUpdater
{
    static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan AutoInterval = TimeSpan.FromDays(1);

    readonly DispatcherQueueTimer _timer;

    public ComponentUpdater(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.Tick += async (_, _) =>
        {
            // Not due, or put off because something was downloading: look again in an hour.
            _timer.Interval = TimeSpan.FromHours(1);
            await AutoUpdateAsync();
        };
    }

    public IReadOnlyList<Component> Components { get; } = Create();
    public bool Checking { get; private set; }
    public bool Installing { get; private set; }
    /// <summary>When the latest versions were last fetched this session.</summary>
    public DateTime? LastCheck { get; private set; }
    /// <summary>Outcome of the last install, shown on the About page until dismissed.</summary>
    public (bool Succeeded, string Message)? LastResult { get; set; }

    /// <summary>Raised on the UI thread whenever anything above changes.</summary>
    public event Action? Changed;

    /// <summary>First automatic check a minute after startup, out of the way of whatever the user opened VidFlow for.</summary>
    public void Start()
    {
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Start();
    }

    /// <summary>Reads the installed versions and compares VidFlow's own copies with the latest releases
    /// (<paramref name="refresh"/>: ask GitHub again instead of using the cached answer).</summary>
    public async Task RefreshAsync(bool refresh)
    {
        if (Installing || Checking) return;
        Checking = true;
        foreach (var c in Components)
        {
            c.State = c.Path() is null ? ToolState.Missing : ToolState.Checking;
            c.Version = null;
        }
        Changed?.Invoke();
        await Task.WhenAll(Components.Select(c => CheckAsync(c, refresh)));
        if (refresh || LastCheck is null) LastCheck = DateTime.Now;
        Checking = false;
        Changed?.Invoke();
    }

    async Task CheckAsync(Component c, bool refresh)
    {
        var exe = c.Path();
        if (exe is null) return;
        c.Version = await c.GetVersion(exe);
        if (!ToolManager.IsManaged(exe))
        {
            // A copy found on PATH is the user's to update; the page offers to install VidFlow's own instead.
            c.State = ToolState.SystemCopy;
            Changed?.Invoke();
            return;
        }
        Changed?.Invoke();
        try
        {
            // A slow network shouldn't leave the row spinning: give up after a while and offer to check again.
            var latest = c.GetLatest(refresh);
            c.Latest = await Task.WhenAny(latest, Task.Delay(CheckTimeout)) == latest ? await latest : null;
            c.State = c.Latest is null ? ToolState.CheckFailed
                : c.IsUpToDate(c.Version, c.Latest) ? ToolState.UpToDate
                : ToolState.UpdateAvailable;
        }
        catch (Exception ex)
        {
            Log.Info($"{c.Name} update check failed: {ex.Message}");
            c.State = ToolState.CheckFailed;
        }
        Changed?.Invoke();
    }

    /// <summary>Installs (or updates) the given components one after another, then checks again.
    /// Returns false if one failed.</summary>
    public async Task<bool> InstallAsync(IReadOnlyList<Component> components)
    {
        if (Installing || Checking || components.Count == 0) return true;
        Installing = true;
        LastResult = null;
        var done = new List<string>();
        var ok = true;
        try
        {
            foreach (var c in components)
            {
                c.State = ToolState.Installing;
                c.Progress = null;
                Changed?.Invoke();
                var progress = new Progress<double>(p =>
                {
                    c.Progress = p;
                    Changed?.Invoke();
                });
                await Task.Run(() => c.Install(progress, CancellationToken.None));
                var version = await c.GetVersion(c.Path());
                done.Add($"{c.Name} {(c.Name == "ffmpeg" ? ShortFfmpeg(version) : version) ?? ""}".Trim());
                Log.Info($"Installed {done[^1]}");
            }
            LastResult = (true, L.Format("Tools_InstallDoneMessage", string.Join(", ", done)));
        }
        catch (Exception ex)
        {
            Log.Warn($"Tool install failed: {ex.Message}");
            LastResult = (false, L.Format("Settings_InstallFailedMessage", ex.Message));
            ok = false;
        }
        finally
        {
            Installing = false;
            await RefreshAsync(refresh: false);
        }
        return ok;
    }

    /// <summary>Once a day: update VidFlow's own copies that are out of date. Waits while anything downloads,
    /// and tries again later if a check or an install fails.</summary>
    async Task AutoUpdateAsync()
    {
        var s = AppSettings.Current;
        if (!s.AutoUpdateYtDlp || DateTime.Now - s.LastYtDlpUpdateCheck < AutoInterval) return;
        if (Installing || Checking || App.Downloads.ActiveCount > 0) return;
        await RefreshAsync(refresh: true);
        var due = Components.Where(c => c.State == ToolState.UpdateAvailable).ToList();
        // Something may have started while checking.
        if (App.Downloads.ActiveCount > 0) return;
        if (due.Count > 0)
        {
            Log.Info($"Updating in the background: {string.Join(", ", due.Select(c => $"{c.Name} {c.Version} → {c.Latest}"))}");
            if (!await InstallAsync(due)) return;
        }
        if (Components.Any(c => c.State == ToolState.CheckFailed)) return;
        s.LastYtDlpUpdateCheck = DateTime.Now;
        AppSettings.Save();
    }

    /// <summary>"N-128018-ga881f0a9ad-20261024" → "N-128018 (2026-10-24)".</summary>
    public static string? ShortFfmpeg(string? version)
    {
        if (version is null) return null;
        var parts = version.Split('-');
        if (parts.Length < 2 || parts[0] != "N") return version;
        return ToolManager.FfmpegBuildDate(version) is { } date ? $"N-{parts[1]} ({date:yyyy-MM-dd})" : $"N-{parts[1]}";
    }

    static Component[] Create() =>
    [
        new()
        {
            Name = "yt-dlp", Glyph = "\uE896", PurposeKey = "Tools_YtDlpPurpose", MissingKey = "Settings_YtDlpMissing", Required = true,
            Path = () => ToolManager.YtDlpPath,
            GetVersion = async exe => await ToolManager.GetVersionAsync(exe, "--version"),
            GetLatest = refresh => ToolManager.GetLatestYtDlpVersionAsync(refresh),
            IsUpToDate = ToolManager.IsUpToDate,
            Install = ToolManager.InstallYtDlpAsync,
        },
        new()
        {
            Name = "ffmpeg", Glyph = "\uE8B2", PurposeKey = "Tools_FfmpegPurpose", MissingKey = "Settings_FfmpegMissing", Required = false,
            Path = () => ToolManager.FfmpegPath,
            GetVersion = async exe => await ToolManager.GetVersionAsync(exe, "-version"),
            GetLatest = async refresh => (await ToolManager.GetLatestFfmpegDateAsync(refresh))?.ToString("yyyy-MM-dd"),
            IsUpToDate = (installed, latest) => ToolManager.IsFfmpegUpToDate(installed,
                DateOnly.ParseExact(latest, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            Install = ToolManager.InstallFfmpegAsync,
        },
        new()
        {
            Name = "Deno", Glyph = "\uE943", PurposeKey = "Tools_DenoPurpose", MissingKey = "Settings_DenoMissing", Required = false,
            Path = () => ToolManager.DenoPath,
            GetVersion = async exe => ToolManager.DenoVersion(await ToolManager.GetVersionAsync(exe, "--version")),
            GetLatest = refresh => ToolManager.GetLatestDenoVersionAsync(refresh),
            IsUpToDate = ToolManager.IsUpToDate,
            Install = ToolManager.InstallDenoAsync,
        },
        new()
        {
            Name = "aria2", Glyph = "\uEC4A", PurposeKey = "Tools_Aria2Purpose", MissingKey = "Settings_Aria2Missing", Required = false,
            Path = () => ToolManager.Aria2Path,
            GetVersion = async exe => ToolManager.Aria2Version(await ToolManager.GetVersionAsync(exe, "--version")),
            GetLatest = refresh => ToolManager.GetLatestAria2VersionAsync(refresh),
            IsUpToDate = ToolManager.IsUpToDate,
            Install = ToolManager.InstallAria2Async,
        },
    ];
}
