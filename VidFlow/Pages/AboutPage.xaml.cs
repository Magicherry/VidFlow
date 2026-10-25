using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Pages;

/// <summary>Settings &gt; About &amp; updates: VidFlow's own updates, the external components (yt-dlp, ffmpeg, Deno),
/// and version / license / troubleshooting links.</summary>
public sealed partial class AboutPage : Page
{
    bool _loading;
    bool _installing;

    public AboutPage()
    {
        InitializeComponent();
        Breadcrumb.ItemsSource = new[] { L.Get("Nav_Settings"), L.Get("Settings_AboutUpdates") };
        VersionText.Text = L.Format("About_Version", typeof(App).Assembly.GetName().Version?.ToString(3));
        SettingsPage.AddItems(UpdateModeCombo, ("auto", L.Get("UpdateMode_Auto")), ("notify", L.Get("UpdateMode_Notify")),
            ("manual", L.Get("UpdateMode_Manual")));

        // The page is recreated on each visit: subscribe while shown, unsubscribe when it goes away.
        Loaded += (_, _) => App.Updates.Changed += RefreshUpdate;
        Unloaded += (_, _) => App.Updates.Changed -= RefreshUpdate;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => LoadValues();

    void LoadValues()
    {
        _loading = true;
        SettingsPage.Select(UpdateModeCombo, AppSettings.Current.UpdateMode);
        AutoUpdateToggle.IsOn = AppSettings.Current.AutoUpdateYtDlp;
        _loading = false;
        RefreshUpdate();
        _ = RefreshToolsAsync();
    }

    void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0) App.MainWindow.NavigateTo("settings");
    }

    // ---- App updates -------------------------------------------------------------------------------------------

    void RefreshUpdate()
    {
        var u = App.Updates;
        var current = UpdateController.Current.ToString(3);
        var latest = u.Latest?.Version;
        UpdateCard.Description = u.State switch
        {
            UpdateState.Checking => L.Get("Update_Checking"),
            UpdateState.UpToDate => L.Format("Update_UpToDate", current),
            UpdateState.Available => L.Format("Update_Available", latest, current),
            UpdateState.Downloading => L.Format("Update_Downloading", latest, u.Progress.ToString("0")),
            UpdateState.Ready => L.Format("Update_Ready", latest),
            UpdateState.Failed => L.Format("Update_Failed", u.Error),
            _ => L.Format("Update_Current", current),
        };
        if (u.LastResult is (UpdateOutcome.Failed, { } failed))
            UpdateCard.Description += "\n" + L.Format("Update_RolledBack", failed, current);

        var newer = u.Latest is { } m && UpdateService.IsNewer(m, UpdateController.Current);
        UpdateButton.Content = u.State switch
        {
            UpdateState.Available => L.Get("Update_Download"),
            UpdateState.Downloading => L.Get("Update_DownloadingShort"),
            UpdateState.Ready => L.Get("Update_InstallNow"),
            UpdateState.Failed when newer => L.Get("Update_Retry"),
            UpdateState.Checking => L.Get("Update_CheckingShort"),
            _ => L.Get("Settings_CheckUpdate"),
        };
        UpdateButton.IsEnabled = u.State is not (UpdateState.Checking or UpdateState.Downloading);
        // The primary action stands out while there is something to do.
        UpdateButton.Style = (Style)Application.Current.Resources[
            u.State is UpdateState.Available or UpdateState.Ready ? "AccentButtonStyle" : "DefaultButtonStyle"];
        UpdateProgress.Visibility = u.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = u.Progress;

        ReleaseNotesExpander.Visibility = u.Latest is null ? Visibility.Collapsed : Visibility.Visible;
        if (u.Latest is { } notes)
            ReleaseNotesText.Text = $"{L.Format("Update_NotesHeader", notes.Version, notes.Published.ToLocalTime().ToString("yyyy-MM-dd"))}\n\n{notes.Notes.Trim()}";
    }

    async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var u = App.Updates;
        var newer = u.Latest is { } m && UpdateService.IsNewer(m, UpdateController.Current);
        switch (u.State)
        {
            case UpdateState.Available:
            case UpdateState.Failed when newer:
                await u.DownloadAsync();
                break;
            case UpdateState.Ready:
                await u.InstallAsync(XamlRoot);
                break;
            default:
                await u.CheckAsync(userInitiated: true);
                break;
        }
    }

    void UpdateModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UpdateModeCombo.SelectedItem is not ComboBoxItem item) return;
        AppSettings.Current.UpdateMode = (string)item.Tag;
        AppSettings.Save();
    }

    // ---- Components --------------------------------------------------------------------------------------------

    void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.AutoUpdateYtDlp = AutoUpdateToggle.IsOn;
        AppSettings.Save();
    }

    async Task RefreshToolsAsync()
    {
        var yt = ToolManager.YtDlpPath;
        var ff = ToolManager.FfmpegPath;
        YtDlpCard.Description = yt is null ? L.Get("Settings_YtDlpMissing") : L.Get("Settings_Checking");
        FfmpegCard.Description = ff is null ? L.Get("Settings_FfmpegMissing") : L.Get("Settings_Checking");
        ShowInitialState(YtDlpButton, yt);
        ShowInitialState(FfmpegButton, ff);

        // Paths can be very long; keep the description short and expose the full path as a tooltip.
        ToolTipService.SetToolTip(YtDlpCard, yt);
        ToolTipService.SetToolTip(FfmpegCard, ff);
        var (ytVerTask, ffVerTask) = (ToolManager.GetVersionAsync(yt, "--version"), ToolManager.GetVersionAsync(ff, "-version"));
        var ytVer = await ytVerTask;
        var ffVer = await ffVerTask;
        if (yt is not null) YtDlpCard.Description = L.Format("Settings_ToolVersion", ytVer ?? "?", Source(yt));
        if (ff is not null) FfmpegCard.Description = L.Format("Settings_ToolVersion", Shorten(ffVer), Source(ff));

        // Both components follow the same rules; only VidFlow's own copies are compared with the latest release.
        if (yt is not null && ToolManager.IsManaged(yt))
        {
            var latest = await ToolManager.GetLatestYtDlpVersionAsync();
            ShowUpdateState(YtDlpButton, latest is null ? null : ToolManager.IsUpToDate(ytVer, latest), latest);
        }
        if (ff is not null && ToolManager.IsManaged(ff))
        {
            var latest = await ToolManager.GetLatestFfmpegDateAsync();
            ShowUpdateState(FfmpegButton, latest is null ? null : ToolManager.IsFfmpegUpToDate(ffVer, latest),
                latest?.ToString("yyyy-MM-dd"));
        }

        // Deno: the JavaScript runtime yt-dlp needs for YouTube.
        var deno = ToolManager.DenoPath;
        DenoCard.Description = deno is null ? L.Get("Settings_DenoMissing") : L.Get("Settings_Checking");
        ToolTipService.SetToolTip(DenoCard, deno);
        ShowInitialState(DenoButton, deno);
        if (deno is not null)
        {
            var denoVer = ToolManager.DenoVersion(await ToolManager.GetVersionAsync(deno, "--version"));
            DenoCard.Description = L.Format("Settings_ToolVersion", denoVer ?? "?", Source(deno));
            if (ToolManager.IsManaged(deno))
            {
                var latest = await ToolManager.GetLatestDenoVersionAsync();
                ShowUpdateState(DenoButton, latest is null ? null : ToolManager.IsUpToDate(denoVer, latest), latest);
            }
        }
    }

    /// <summary>Before the version check: Install / Install app copy, or "Checking…" (disabled) for our own copy.</summary>
    void ShowInitialState(Button button, string? exe)
    {
        button.Content = exe is null ? L.Get("Settings_Install")
            : ToolManager.IsManaged(exe) ? L.Get("Settings_Checking")
            : L.Get("Settings_InstallManaged");
        button.IsEnabled = !_installing && !(exe is not null && ToolManager.IsManaged(exe));
    }

    /// <summary>After the check: Up to date (disabled) / Update to X / Check for updates (the check failed).</summary>
    void ShowUpdateState(Button button, bool? upToDate, string? latest)
    {
        button.Content = upToDate switch
        {
            null => L.Get("Settings_CheckUpdate"),
            true => L.Get("Settings_UpToDate"),
            false => L.Format("Settings_UpdateTo", latest),
        };
        button.IsEnabled = upToDate != true && !_installing;
    }

    static string Source(string exe) => ToolManager.IsManaged(exe) ? L.Get("Settings_SourceManaged") : L.Get("Settings_SourceSystem");

    /// <summary>"N-128018-ga881f0a9ad-20261024" → "N-128018 (2026-10-24)".</summary>
    static string Shorten(string? version)
    {
        if (version is null) return "?";
        var parts = version.Split('-');
        if (parts.Length < 2 || parts[0] != "N") return version;
        return ToolManager.FfmpegBuildDate(version) is { } date ? $"N-{parts[1]} ({date:yyyy-MM-dd})" : $"N-{parts[1]}";
    }

    async void InstallYtDlp_Click(object sender, RoutedEventArgs e)
    {
        if (YtDlpButton.Content as string == L.Get("Settings_CheckUpdate"))
        {
            // The previous check failed (offline?): just try the check again.
            await ToolManager.GetLatestYtDlpVersionAsync(refresh: true);
            await RefreshToolsAsync();
            return;
        }
        await InstallAsync("yt-dlp", "--version", YtDlpProgress, ToolManager.InstallYtDlpAsync);
    }

    async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        if (FfmpegButton.Content as string == L.Get("Settings_CheckUpdate"))
        {
            await ToolManager.GetLatestFfmpegDateAsync(refresh: true);
            await RefreshToolsAsync();
            return;
        }
        await InstallAsync("ffmpeg", "-version", FfmpegProgress, ToolManager.InstallFfmpegAsync);
    }

    async void InstallDeno_Click(object sender, RoutedEventArgs e)
    {
        if (DenoButton.Content as string == L.Get("Settings_CheckUpdate"))
        {
            await ToolManager.GetLatestDenoVersionAsync(refresh: true);
            await RefreshToolsAsync();
            return;
        }
        await InstallAsync("deno", "--version", DenoProgress, ToolManager.InstallDenoAsync);
    }

    async Task InstallAsync(string tool, string versionArg, ProgressBar bar, Func<IProgress<double>, CancellationToken, Task> install)
    {
        if (_installing) return;
        _installing = true;
        ToolErrorBar.IsOpen = false;
        YtDlpButton.IsEnabled = FfmpegButton.IsEnabled = DenoButton.IsEnabled = false;
        bar.Value = 0;
        bar.IsIndeterminate = true;
        bar.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<double>(p =>
            {
                bar.IsIndeterminate = false;
                bar.Value = p;
            });
            await Task.Run(() => install(progress, CancellationToken.None));

            var exe = tool switch { "yt-dlp" => ToolManager.YtDlpPath, "deno" => ToolManager.DenoPath, _ => ToolManager.FfmpegPath };
            var version = await ToolManager.GetVersionAsync(exe, versionArg);
            var shown = tool switch { "ffmpeg" => Shorten(version), "deno" => ToolManager.DenoVersion(version) ?? "?", _ => version ?? "?" };
            ToolErrorBar.Severity = InfoBarSeverity.Success;
            ToolErrorBar.Title = L.Get("Settings_InstallDone");
            ToolErrorBar.Message = L.Format("Settings_InstallDoneMessage", tool, shown);
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
            bar.Visibility = Visibility.Collapsed;
            YtDlpButton.IsEnabled = FfmpegButton.IsEnabled = DenoButton.IsEnabled = true;
            _installing = false;
            await RefreshToolsAsync();
        }
    }

    // ---- About -------------------------------------------------------------------------------------------------

    async void Licenses_Click(object sender, RoutedEventArgs e) =>
        await new LicensesDialog { XamlRoot = XamlRoot }.ShowAsync();

    async void RunWizard_Click(object sender, RoutedEventArgs e)
    {
        await new FirstRunDialog { XamlRoot = XamlRoot }.ShowAsync();
        LoadValues();
    }

    void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(Log.Dir);

    void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.DataDir);

    static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
