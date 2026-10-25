using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Pages;

/// <summary>The components section: a status row (overall state, last check, "check for updates", "update all")
/// and one row per component saying what it is for, its version, and either "up to date" or the action it needs.</summary>
public sealed partial class AboutPage
{
    enum ToolState { Checking, UpToDate, UpdateAvailable, Missing, SystemCopy, CheckFailed, Installing }

    /// <summary>One component: how to find, version, check and install it, and its row on the page.</summary>
    sealed class Tool
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

        public ToolState State;
        public string? Version;
        public string? Latest;

        public SettingsCard Card = null!;
        public Button Button = null!;
        public ProgressBar Bar = null!;
        public ProgressRing Ring = null!;
        public StackPanel Status = null!;
        public FontIcon StatusIcon = null!;
        public TextBlock StatusText = null!;
    }

    /// <summary>When the latest versions were last fetched this session (kept across visits to the page).</summary>
    static DateTime? s_lastToolCheck;

    static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    Tool[] _tools = [];
    bool _installing;

    static Tool[] CreateTools() =>
    [
        new()
        {
            Name = "yt-dlp", Glyph = "", PurposeKey = "Tools_YtDlpPurpose", MissingKey = "Settings_YtDlpMissing", Required = true,
            Path = () => ToolManager.YtDlpPath,
            GetVersion = async exe => await ToolManager.GetVersionAsync(exe, "--version"),
            GetLatest = refresh => ToolManager.GetLatestYtDlpVersionAsync(refresh),
            IsUpToDate = ToolManager.IsUpToDate,
            Install = ToolManager.InstallYtDlpAsync,
        },
        new()
        {
            Name = "ffmpeg", Glyph = "", PurposeKey = "Tools_FfmpegPurpose", MissingKey = "Settings_FfmpegMissing", Required = false,
            Path = () => ToolManager.FfmpegPath,
            GetVersion = async exe => await ToolManager.GetVersionAsync(exe, "-version"),
            GetLatest = async refresh => (await ToolManager.GetLatestFfmpegDateAsync(refresh))?.ToString("yyyy-MM-dd"),
            IsUpToDate = (installed, latest) => ToolManager.IsFfmpegUpToDate(installed,
                DateOnly.ParseExact(latest, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            Install = ToolManager.InstallFfmpegAsync,
        },
        new()
        {
            Name = "Deno", Glyph = "", PurposeKey = "Tools_DenoPurpose", MissingKey = "Settings_DenoMissing", Required = false,
            Path = () => ToolManager.DenoPath,
            GetVersion = async exe => ToolManager.DenoVersion(await ToolManager.GetVersionAsync(exe, "--version")),
            GetLatest = refresh => ToolManager.GetLatestDenoVersionAsync(refresh),
            IsUpToDate = ToolManager.IsUpToDate,
            Install = ToolManager.InstallDenoAsync,
        },
    ];

    void BuildToolRows()
    {
        _tools = CreateTools();
        ToolList.Children.Clear();
        foreach (var tool in _tools)
        {
            tool.Bar = new ProgressBar { Width = 120, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            tool.Ring = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
            tool.StatusIcon = new FontIcon { FontSize = 14 };
            tool.StatusText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            tool.Status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            tool.Status.Children.Add(tool.StatusIcon);
            tool.Status.Children.Add(tool.StatusText);
            tool.Button = new Button { MinWidth = 96 };
            var t = tool;
            tool.Button.Click += async (_, _) => await InstallAsync([t]);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            actions.Children.Add(tool.Bar);
            actions.Children.Add(tool.Ring);
            actions.Children.Add(tool.Status);
            actions.Children.Add(tool.Button);
            tool.Card = new SettingsCard { Header = tool.Name, HeaderIcon = new FontIcon { Glyph = tool.Glyph }, Content = actions };
            AutomationProperties.SetName(tool.Button, tool.Name);
            ToolList.Children.Add(tool.Card);
        }
    }

    async void CheckTools_Click(object sender, RoutedEventArgs e) => await RefreshToolsAsync(refresh: true);

    async void UpdateAllTools_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync(_tools.Where(t => t.State is ToolState.UpdateAvailable or ToolState.Missing).ToArray());

    /// <summary>Reads the installed versions and compares VidFlow's own copies with the latest releases
    /// (<paramref name="refresh"/>: ask GitHub again instead of using this session's cached answer).</summary>
    async Task RefreshToolsAsync(bool refresh)
    {
        if (_installing) return;
        foreach (var t in _tools)
        {
            var exe = t.Path();
            t.State = exe is null ? ToolState.Missing : ToolState.Checking;
            t.Version = null;
            ToolTipService.SetToolTip(t.Card, exe);
            ShowTool(t);
        }
        CheckToolsButton.IsEnabled = false;
        ShowSummary(checking: true);

        // All three at once: each reads its version, then (for VidFlow's own copy) the latest release.
        await Task.WhenAll(_tools.Select(t => CheckAsync(t, refresh)));
        if (refresh || s_lastToolCheck is null) s_lastToolCheck = DateTime.Now;

        CheckToolsButton.IsEnabled = !_installing;
        ShowSummary(checking: false);
    }

    async Task CheckAsync(Tool t, bool refresh)
    {
        var exe = t.Path();
        if (exe is null) return;
        t.Version = await t.GetVersion(exe);
        if (!ToolManager.IsManaged(exe))
        {
            // A copy found on PATH is the user's to update; offer to install VidFlow's own instead.
            t.State = ToolState.SystemCopy;
            ShowTool(t);
            return;
        }
        ShowTool(t);
        try
        {
            // A slow network shouldn't leave the row spinning: give up after a while and offer to check again.
            var latest = t.GetLatest(refresh);
            t.Latest = await Task.WhenAny(latest, Task.Delay(CheckTimeout)) == latest ? await latest : null;
            t.State = t.Latest is null ? ToolState.CheckFailed
                : t.IsUpToDate(t.Version, t.Latest) ? ToolState.UpToDate
                : ToolState.UpdateAvailable;
        }
        catch (Exception ex)
        {
            Log.Info($"{t.Name} update check failed: {ex.Message}");
            t.State = ToolState.CheckFailed;
        }
        ShowTool(t);
    }

    /// <summary>Description (purpose · version) and the right-hand side: a status, a spinner or the one action needed.</summary>
    void ShowTool(Tool t)
    {
        var purpose = L.Get(t.PurposeKey);
        var version = t.Name == "ffmpeg" ? ShortFfmpeg(t.Version) : t.Version;
        t.Card.Description = t.State switch
        {
            ToolState.Missing => L.Get(t.MissingKey),
            ToolState.SystemCopy => L.Format("Tools_SystemCopy", purpose, version ?? "?"),
            _ when version is not null => L.Format("Tools_Installed", purpose, version),
            _ => purpose,
        };

        var busy = t.State is ToolState.Checking;
        t.Ring.IsActive = busy && t.Version is not null;
        t.Ring.Visibility = busy && t.Version is not null ? Visibility.Visible : Visibility.Collapsed;
        t.Bar.Visibility = t.State == ToolState.Installing ? Visibility.Visible : Visibility.Collapsed;

        (string Glyph, string Brush, string Text)? status = t.State switch
        {
            ToolState.UpToDate => ("", "SystemFillColorSuccessBrush", L.Get("Settings_UpToDate")),
            ToolState.CheckFailed => ("", "SystemFillColorCautionBrush", L.Get("Tools_CheckFailed")),
            _ => null,
        };
        t.Status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (status is { } s)
        {
            t.StatusIcon.Glyph = s.Glyph;
            t.StatusIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[s.Brush];
            t.StatusText.Text = s.Text;
        }

        string? action = t.State switch
        {
            ToolState.Missing => L.Get("Settings_Install"),
            ToolState.UpdateAvailable => L.Format("Settings_UpdateTo", t.Latest),
            ToolState.SystemCopy => L.Get("Settings_InstallManaged"),
            _ => null,
        };
        t.Button.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        if (action is not null) t.Button.Content = action;
        t.Button.IsEnabled = !_installing;
        // The things that need doing stand out; installing a separate copy is optional.
        t.Button.Style = (Style)Application.Current.Resources[
            t.State is ToolState.Missing or ToolState.UpdateAvailable ? "AccentButtonStyle" : "DefaultButtonStyle"];
    }

    /// <summary>Overall state in the header, last check time below, and "Update all" when there is something to do.</summary>
    void ShowSummary(bool checking)
    {
        var updates = _tools.Count(t => t.State == ToolState.UpdateAvailable);
        var missingRequired = _tools.Where(t => t.State == ToolState.Missing && t.Required).Select(t => t.Name).ToList();
        var missing = _tools.Count(t => t.State == ToolState.Missing);
        var failed = _tools.Any(t => t.State == ToolState.CheckFailed);

        (ToolsSummaryCard.Header, ToolsSummaryIcon.Glyph) = (checking, missingRequired.Count, updates + missing, failed) switch
        {
            (true, _, _, _) => (L.Get("Tools_Checking"), ""),
            (_, > 0, _, _) => (L.Format("Tools_MissingRequired", string.Join(", ", missingRequired)), ""),
            (_, _, > 0, _) => (L.Format("Tools_ActionsNeeded", updates + missing), ""),
            (_, _, _, true) => (L.Get("Tools_CheckFailedSummary"), ""),
            _ => (L.Get("Tools_AllUpToDate"), ""),
        };
        ToolsSummaryCard.Description = s_lastToolCheck is { } at
            // 24-hour numbers read the same in either UI language (the system's "4:46 PM" wouldn't match Chinese text).
            ? L.Format("Tools_LastChecked", at.ToString(at.Date == DateTime.Today ? "HH:mm" : "yyyy-MM-dd HH:mm"))
            : L.Get("Tools_ManagedNote");
        UpdateAllToolsButton.Visibility = !checking && updates + missing > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllToolsButton.IsEnabled = !_installing;
    }

    /// <summary>Installs (or updates) the given components one after another, then checks again.</summary>
    async Task InstallAsync(Tool[] tools)
    {
        if (_installing || tools.Length == 0) return;
        _installing = true;
        ToolErrorBar.IsOpen = false;
        CheckToolsButton.IsEnabled = UpdateAllToolsButton.IsEnabled = false;
        var done = new List<string>();
        try
        {
            foreach (var t in tools)
            {
                t.State = ToolState.Installing;
                t.Bar.Value = 0;
                t.Bar.IsIndeterminate = true;
                foreach (var other in _tools) ShowTool(other);
                var progress = new Progress<double>(p =>
                {
                    t.Bar.IsIndeterminate = false;
                    t.Bar.Value = p;
                });
                await Task.Run(() => t.Install(progress, CancellationToken.None));
                var version = await t.GetVersion(t.Path());
                done.Add($"{t.Name} {(t.Name == "ffmpeg" ? ShortFfmpeg(version) : version) ?? ""}".Trim());
            }
            ToolErrorBar.Severity = InfoBarSeverity.Success;
            ToolErrorBar.Title = L.Get("Settings_InstallDone");
            ToolErrorBar.Message = L.Format("Tools_InstallDoneMessage", string.Join(", ", done));
            ToolErrorBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Tool install failed: {ex.Message}");
            ToolErrorBar.Severity = InfoBarSeverity.Error;
            ToolErrorBar.Title = L.Get("Settings_InstallFailed");
            ToolErrorBar.Message = L.Format("Settings_InstallFailedMessage", ex.Message);
            ToolErrorBar.IsOpen = true;
        }
        finally
        {
            _installing = false;
            await RefreshToolsAsync(refresh: false);
        }
    }

    void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.AutoUpdateYtDlp = AutoUpdateToggle.IsOn;
        AppSettings.Save();
    }

    /// <summary>"N-128018-ga881f0a9ad-20261024" → "N-128018 (2026-10-24)".</summary>
    static string? ShortFfmpeg(string? version)
    {
        if (version is null) return null;
        var parts = version.Split('-');
        if (parts.Length < 2 || parts[0] != "N") return version;
        return ToolManager.FfmpegBuildDate(version) is { } date ? $"N-{parts[1]} ({date:yyyy-MM-dd})" : $"N-{parts[1]}";
    }
}
