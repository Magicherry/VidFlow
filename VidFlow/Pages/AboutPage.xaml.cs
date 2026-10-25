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
        BuildToolRows();
        _ = RefreshToolsAsync(refresh: false);
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
