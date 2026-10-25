using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Pages;

/// <summary>App-wide preferences. Templates, accounts and About &amp; updates have pages of their own.</summary>
public sealed partial class SettingsPage : Page
{
    bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        for (var i = 1; i <= 5; i++) ConcurrencyCombo.Items.Add(i);
        AddItems(LanguageCombo, ("", L.Get("Settings_LanguageSystem")), ("zh", "简体中文"), ("en", "English"));
        AddItems(ThemeCombo, ("System", L.Get("Settings_ThemeSystem")), ("Light", L.Get("Settings_ThemeLight")),
            ("Dark", L.Get("Settings_ThemeDark")));

        // The page is recreated on each visit: subscribe while shown, unsubscribe when it goes away.
        Loaded += (_, _) => App.Updates.Changed += RefreshAbout;
        Unloaded += (_, _) => App.Updates.Changed -= RefreshAbout;
    }

    internal static void AddItems(ComboBox combo, params (string Tag, string Text)[] items)
    {
        foreach (var (tag, text) in items) combo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
    }

    internal static void Select(ComboBox combo, object tag) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag)) ?? combo.Items[0];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var s = AppSettings.Current;
        _loading = true;
        Select(LanguageCombo, s.Language);
        Select(ThemeCombo, s.Theme);
        TrayToggle.IsOn = s.MinimizeToTray;
        ClipboardToggle.IsOn = s.WatchClipboard;
        FolderCard.Description = s.DownloadFolder;
        ConcurrencyCombo.SelectedItem = Math.Clamp(s.MaxConcurrent, 1, 5);
        // Empty box = unlimited.
        SpeedBox.Value = s.SpeedLimitKBps > 0 ? Math.Round(s.SpeedLimitKBps / 1024.0, 2) : double.NaN;
        SubLangsBox.Text = s.SubtitleLanguages;
        DanmakuToggle.IsOn = s.SaveDanmaku;
        MetadataToggle.IsOn = s.EmbedMetadata;
        H264Toggle.IsOn = s.PreferH264;
        ProxyBox.Text = s.Proxy;
        NotifyToggle.IsOn = s.NotifyOnFinish;
        NotifyFailureToggle.IsOn = s.NotifyOnFailure;
        _loading = false;
        RefreshAbout();
    }

    /// <summary>"Version 1.2.0", plus the newer version when one is waiting.</summary>
    void RefreshAbout()
    {
        var description = L.Format("About_Version", UpdateController.Current.ToString(3));
        var pending = App.Updates.HasNewer;
        if (pending) description += " · " + L.Format("Settings_UpdateBadge", App.Updates.Latest!.Version);
        AboutCard.Description = description;
        AboutBadge.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
    }

    void AboutCard_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("about");

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

    void SpeedBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        var mb = args.NewValue;
        AppSettings.Current.SpeedLimitKBps = double.IsNaN(mb) || mb <= 0 ? 0 : Math.Max(1, (int)Math.Round(mb * 1024));
        AppSettings.Save();
        // 0 is shown as the "Unlimited" placeholder rather than a literal 0.
        if (!double.IsNaN(mb) && mb <= 0) sender.Value = double.NaN;
    }

    /// <summary>All toggles save the same way.</summary>
    void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.MinimizeToTray = TrayToggle.IsOn;
        s.WatchClipboard = ClipboardToggle.IsOn;
        s.SaveDanmaku = DanmakuToggle.IsOn;
        s.EmbedMetadata = MetadataToggle.IsOn;
        s.PreferH264 = H264Toggle.IsOn;
        s.NotifyOnFinish = NotifyToggle.IsOn;
        s.NotifyOnFailure = NotifyFailureToggle.IsOn;
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
}
