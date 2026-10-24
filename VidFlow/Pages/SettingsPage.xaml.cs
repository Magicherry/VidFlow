using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Pages;

public sealed partial class SettingsPage : Page
{
    static readonly int[] SpeedLimits = [0, 512, 1024, 2048, 5120, 10240];

    bool _loading;
    bool _installing;

    public SettingsPage()
    {
        InitializeComponent();
        for (var i = 1; i <= 5; i++) ConcurrencyCombo.Items.Add(i);
        foreach (var kb in SpeedLimits)
            SpeedCombo.Items.Add(new ComboBoxItem
            {
                Content = kb == 0 ? L.Get("Settings_SpeedUnlimited") : $"{Format.Bytes(kb * 1024.0)}/s",
                Tag = kb,
            });
        AddItems(BrowserCombo, ("", L.Get("Settings_BrowserNone")), ("edge", "Microsoft Edge"), ("chrome", "Google Chrome"),
            ("firefox", "Firefox"), ("brave", "Brave"), ("opera", "Opera"), ("vivaldi", "Vivaldi"));
        AddItems(LanguageCombo, ("", L.Get("Settings_LanguageSystem")), ("zh", "简体中文"), ("en", "English"));
        AddItems(ThemeCombo, ("System", L.Get("Settings_ThemeSystem")), ("Light", L.Get("Settings_ThemeLight")),
            ("Dark", L.Get("Settings_ThemeDark")));
        VersionText.Text = L.Format("About_Version", typeof(App).Assembly.GetName().Version?.ToString(3));
    }

    static void AddItems(ComboBox combo, params (string Tag, string Text)[] items)
    {
        foreach (var (tag, text) in items) combo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
    }

    static void Select(ComboBox combo, object tag) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag)) ?? combo.Items[0];

    protected override void OnNavigatedTo(NavigationEventArgs e) => LoadValues();

    void LoadValues()
    {
        var s = AppSettings.Current;
        _loading = true;
        FolderCard.Description = s.DownloadFolder;
        ConcurrencyCombo.SelectedItem = Math.Clamp(s.MaxConcurrent, 1, 5);
        Select(SpeedCombo, SpeedLimits.Contains(s.SpeedLimitKBps) ? s.SpeedLimitKBps : 0);
        H264Toggle.IsOn = s.PreferH264;
        MetadataToggle.IsOn = s.EmbedMetadata;
        SubLangsBox.Text = s.SubtitleLanguages;
        DanmakuToggle.IsOn = s.SaveDanmaku;
        ProxyBox.Text = s.Proxy;
        Select(BrowserCombo, s.CookiesBrowser);
        Select(LanguageCombo, s.Language);
        Select(ThemeCombo, s.Theme);
        AutoUpdateToggle.IsOn = s.AutoUpdateYtDlp;
        TrayToggle.IsOn = s.MinimizeToTray;
        NotifyToggle.IsOn = s.NotifyOnFinish;
        ClipboardToggle.IsOn = s.WatchClipboard;
        UpdateCookieFile();
        UpdateAccounts();
        _loading = false;

        _ = RefreshToolsAsync();
    }

    async Task RefreshToolsAsync()
    {
        var yt = ToolManager.YtDlpPath;
        var ff = ToolManager.FfmpegPath;
        YtDlpCard.Description = yt is null ? L.Get("Settings_YtDlpMissing") : L.Get("Settings_Checking");
        FfmpegCard.Description = ff is null ? L.Get("Settings_FfmpegMissing") : L.Get("Settings_Checking");
        YtDlpButton.Content = yt is null ? L.Get("Settings_Install") : ToolManager.IsYtDlpManaged ? L.Get("Settings_Checking") : L.Get("Settings_InstallManaged");
        YtDlpButton.IsEnabled = !_installing && !(yt is not null && ToolManager.IsYtDlpManaged); // re-enabled once the check finishes
        FfmpegButton.Content = ff is null ? L.Get("Settings_Install") : L.Get("Settings_Reinstall");

        // Paths can be very long; keep the description short and expose the full path as a tooltip.
        ToolTipService.SetToolTip(YtDlpCard, yt);
        ToolTipService.SetToolTip(FfmpegCard, ff);
        var (ytVerTask, ffVerTask) = (ToolManager.GetVersionAsync(yt, "--version"), ToolManager.GetVersionAsync(ff, "-version"));
        var ytVer = await ytVerTask;
        if (yt is not null) YtDlpCard.Description = L.Format("Settings_ToolVersion", ytVer ?? "?", Source(yt));
        if (ff is not null) FfmpegCard.Description = L.Format("Settings_ToolVersion", Shorten(await ffVerTask), Source(ff));

        // Only VidFlow's own copy can be updated from here: compare it with the latest release.
        if (yt is not null && ToolManager.IsYtDlpManaged)
        {
            var latest = await ToolManager.GetLatestYtDlpVersionAsync();
            if (latest is null)
            {
                YtDlpButton.Content = L.Get("Settings_CheckUpdate");
                YtDlpButton.IsEnabled = !_installing;
            }
            else if (ToolManager.IsUpToDate(ytVer, latest))
            {
                YtDlpButton.Content = L.Get("Settings_UpToDate");
                YtDlpButton.IsEnabled = false;
            }
            else
            {
                YtDlpButton.Content = L.Format("Settings_UpdateTo", latest);
                YtDlpButton.IsEnabled = !_installing;
            }
        }
    }

    static string Source(string exe) => ToolManager.IsManaged(exe) ? L.Get("Settings_SourceManaged") : L.Get("Settings_SourceSystem");

    /// <summary>"N-128018-ga881f0a9ad-20261024" → "N-128018".</summary>
    static string Shorten(string? version)
    {
        if (version is null) return "?";
        var parts = version.Split('-');
        return parts.Length >= 2 && parts[0] == "N" ? $"{parts[0]}-{parts[1]}" : version;
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

    async void InstallFfmpeg_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync("ffmpeg", "-version", FfmpegProgress, ToolManager.InstallFfmpegAsync);

    async Task InstallAsync(string tool, string versionArg, ProgressBar bar, Func<IProgress<double>, CancellationToken, Task> install)
    {
        if (_installing) return;
        _installing = true;
        ToolErrorBar.IsOpen = false;
        YtDlpButton.IsEnabled = FfmpegButton.IsEnabled = false;
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

            var exe = tool == "yt-dlp" ? ToolManager.YtDlpPath : ToolManager.FfmpegPath;
            var version = await ToolManager.GetVersionAsync(exe, versionArg);
            ToolErrorBar.Severity = InfoBarSeverity.Success;
            ToolErrorBar.Title = L.Get("Settings_InstallDone");
            ToolErrorBar.Message = L.Format("Settings_InstallDoneMessage", tool, tool == "ffmpeg" ? Shorten(version) : version ?? "?");
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
            YtDlpButton.IsEnabled = FfmpegButton.IsEnabled = true;
            _installing = false;
            await RefreshToolsAsync();
        }
    }

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;
        AppSettings.Current.DownloadFolder = folder;
        AppSettings.Save();
        FolderCard.Description = folder;
    }

    void ConcurrencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ConcurrencyCombo.SelectedItem is not int n) return;
        AppSettings.Current.MaxConcurrent = n;
        AppSettings.Save();
    }

    void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SpeedCombo.SelectedItem is not ComboBoxItem { Tag: int kb }) return;
        AppSettings.Current.SpeedLimitKBps = kb;
        AppSettings.Save();
    }

    /// <summary>All toggles save the same way.</summary>
    void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.PreferH264 = H264Toggle.IsOn;
        s.EmbedMetadata = MetadataToggle.IsOn;
        s.SaveDanmaku = DanmakuToggle.IsOn;
        s.AutoUpdateYtDlp = AutoUpdateToggle.IsOn;
        s.MinimizeToTray = TrayToggle.IsOn;
        s.NotifyOnFinish = NotifyToggle.IsOn;
        s.WatchClipboard = ClipboardToggle.IsOn;
        AppSettings.Save();
    }

    void SubLangsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.SubtitleLanguages = SubLangsBox.Text.Replace(" ", "").Trim(',');
        AppSettings.Save();
    }

    void ProxyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.Proxy = ProxyBox.Text.Trim();
        AppSettings.Save();
    }

    void BrowserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BrowserCombo.SelectedItem is not ComboBoxItem item) return;
        AppSettings.Current.CookiesBrowser = (string)item.Tag;
        AppSettings.Save();
    }

    void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem item) return;
        AppSettings.Current.Language = (string)item.Tag;
        AppSettings.Save();
        RestartBar.IsOpen = true;
    }

    void Restart_Click(object sender, RoutedEventArgs e) => App.Restart();

    void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        AppSettings.Current.Theme = (string)item.Tag;
        AppSettings.Save();
        App.MainWindow.ApplyTheme(AppSettings.Current.Theme);
    }

    async void PickCookieFile_Click(object sender, RoutedEventArgs e)
    {
        var file = await Pickers.PickFileAsync(".txt");
        if (file is null) return;
        AppSettings.Current.CookiesFile = file;
        AppSettings.Save();
        UpdateCookieFile();
    }

    void ClearCookieFile_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CookiesFile = "";
        AppSettings.Save();
        UpdateCookieFile();
    }

    void UpdateCookieFile()
    {
        var file = AppSettings.Current.CookiesFile;
        CookieFileCard.Description = file.Length > 0 ? file : L.Get("Settings_CookieFileDescription");
        ClearCookieFileButton.Visibility = file.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateAccounts()
    {
        var bili = AccountStore.IsLoggedIn(Platform.Bilibili);
        BiliAccountCard.Description = bili ? L.Get("Account_BiliIn") : L.Get("Account_BiliOut");
        BiliAccountButton.Content = bili ? L.Get("Account_Logout") : L.Get("Account_Login");

        var yt = AccountStore.IsLoggedIn(Platform.YouTube);
        YouTubeAccountCard.Description = yt ? L.Get("Account_YtIn") : L.Get("Account_YtOut");
        YouTubeAccountButton.Content = yt ? L.Get("Account_Logout") : L.Get("Account_Login");
    }

    async void BiliAccount_Click(object sender, RoutedEventArgs e) => await ToggleAccountAsync(Platform.Bilibili, BiliAccountButton);
    async void YouTubeAccount_Click(object sender, RoutedEventArgs e) => await ToggleAccountAsync(Platform.YouTube, YouTubeAccountButton);

    async Task ToggleAccountAsync(Platform platform, Button button)
    {
        button.IsEnabled = false;
        try
        {
            if (AccountStore.IsLoggedIn(platform))
                await AccountService.LogoutAsync(platform, App.MainWindow.Hwnd);
            else
                await new LoginWindow(platform).ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Account action failed: {ex.Message}");
            ToolErrorBar.Severity = InfoBarSeverity.Error;
            ToolErrorBar.Title = L.Get("Common_Failed");
            ToolErrorBar.Message = ex.Message;
            ToolErrorBar.IsOpen = true;
        }
        finally
        {
            button.IsEnabled = true;
            UpdateAccounts();
        }
    }

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
