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
        if (s.SpeedLimitKBps > 0) s.SpeedLimitValueKBps = s.SpeedLimitKBps;
        SpeedToggle.IsOn = s.SpeedLimitKBps > 0;
        ShowSpeed(s.SpeedLimitKBps > 0 ? s.SpeedLimitKBps : s.SpeedLimitValueKBps);
        LoadSubtitleLanguages();
        DanmakuToggle.IsOn = s.SaveDanmaku;
        MetadataToggle.IsOn = s.EmbedMetadata;
        H264Toggle.IsOn = s.PreferH264;
        ProxyBox.Text = s.Proxy;
        NotifyToggle.IsOn = s.NotifyOnFinish;
        NotifyFailureToggle.IsOn = s.NotifyOnFailure;
        _loading = false;
        UpdateSpeedSummary();
        RefreshAbout();
    }

    // ---- Speed limit -------------------------------------------------------------------------------------------

    /// <summary>Shows a KB/s value in the friendliest unit (whole MB/s when it divides evenly, or ≥ 1 MB/s).</summary>
    void ShowSpeed(int kbps)
    {
        var mb = kbps >= 1024;
        SpeedUnitCombo.SelectedIndex = mb ? 1 : 0;
        SpeedBox.Value = mb ? Math.Round(kbps / 1024.0, 2) : kbps;
        SpeedBox.SmallChange = mb ? 1 : 100;
        SpeedBox.Maximum = mb ? 10000 : 1024 * 1024;
    }

    int EnteredKBps()
    {
        var value = double.IsNaN(SpeedBox.Value) || SpeedBox.Value <= 0 ? 1 : SpeedBox.Value;
        return Math.Max(1, (int)Math.Round(SpeedUnitCombo.SelectedIndex == 1 ? value * 1024 : value));
    }

    void UpdateSpeedSummary()
    {
        var s = AppSettings.Current;
        SpeedValueCard.IsEnabled = SpeedToggle.IsOn;
        SpeedExpander.Description = s.SpeedLimitKBps > 0
            ? L.Format("Settings_SpeedOn", SpeedText(s.SpeedLimitKBps))
            : L.Get("Settings_SpeedLimitDescription");
    }

    static string SpeedText(int kbps) =>
        kbps >= 1024 ? $"{Math.Round(kbps / 1024.0, 2)} MB/s" : $"{kbps} KB/s";

    void SpeedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.SpeedLimitKBps = SpeedToggle.IsOn ? Math.Max(1, s.SpeedLimitValueKBps) : 0;
        AppSettings.Save();
        // Switching it on opens the value so it can be adjusted right away.
        if (SpeedToggle.IsOn) SpeedExpander.IsExpanded = true;
        UpdateSpeedSummary();
    }

    void SpeedBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        // An emptied box comes back as the previous value rather than "unlimited": the switch decides that.
        if (double.IsNaN(args.NewValue))
        {
            sender.Value = double.IsNaN(args.OldValue) ? 1 : args.OldValue;
            return;
        }
        SaveSpeed();
    }

    void SpeedUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SpeedBox is null) return;
        // Same speed, shown in the other unit (5 MB/s ⇄ 5120 KB/s).
        var kbps = AppSettings.Current.SpeedLimitValueKBps;
        var mb = SpeedUnitCombo.SelectedIndex == 1;
        _loading = true;
        SpeedBox.SmallChange = mb ? 1 : 100;
        SpeedBox.Maximum = mb ? 10000 : 1024 * 1024;
        SpeedBox.Value = mb ? Math.Max(0.1, Math.Round(kbps / 1024.0, 2)) : kbps;
        _loading = false;
    }

    void SaveSpeed()
    {
        var s = AppSettings.Current;
        s.SpeedLimitValueKBps = EnteredKBps();
        if (SpeedToggle.IsOn) s.SpeedLimitKBps = s.SpeedLimitValueKBps;
        AppSettings.Save();
        UpdateSpeedSummary();
    }

    // ---- Subtitles ---------------------------------------------------------------------------------------------

    void LoadSubtitleLanguages()
    {
        var (chosen, other) = SubtitleLanguageSet.Split(AppSettings.Current.SubtitleLanguages);
        SubLangsPanel.Children.Clear();
        foreach (var lang in SubtitleLanguageSet.Common)
        {
            var box = new CheckBox { Content = lang.NativeName, Tag = lang.Key, IsChecked = chosen.Contains(lang.Key), MinWidth = 120 };
            box.Checked += (_, _) => SaveSubtitleLanguages();
            box.Unchecked += (_, _) => SaveSubtitleLanguages();
            SubLangsPanel.Children.Add(box);
        }
        SubLangsOtherBox.Text = other;
        UpdateSubtitleSummary();
    }

    void SaveSubtitleLanguages()
    {
        if (_loading) return;
        var chosen = SubLangsPanel.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag);
        AppSettings.Current.SubtitleLanguages = SubtitleLanguageSet.Join(chosen, SubLangsOtherBox.Text);
        AppSettings.Save();
        UpdateSubtitleSummary();
    }

    void SubLangsOtherBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveSubtitleLanguages();
        // Show the codes as saved (separators tidied, duplicates of ticked languages dropped).
        SubLangsOtherBox.Text = SubtitleLanguageSet.Split(AppSettings.Current.SubtitleLanguages).Other;
    }

    void UpdateSubtitleSummary()
    {
        var names = SubtitleLanguageSet.Describe(AppSettings.Current.SubtitleLanguages);
        SubtitlesExpander.Description = names.Length > 0 ? L.Format("Settings_SubtitlesSummary", names) : L.Get("Settings_SubtitlesNone");
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
