using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Pages;
using VidFlow.Services;

namespace VidFlow;

/// <summary>First-run setup in three steps: install components, pick the download folder, optionally sign in. Each step
/// has an icon, title and one line of explanation; components and accounts are rows in one card; the footer has the
/// step dots and skip / back / next.</summary>
public sealed partial class FirstRunDialog : ContentDialog
{
    int _step;
    bool _installing;

    /// <summary>A component: its icon, whether it is in place, and whether VidFlow can't work without it.</summary>
    sealed record Component(string Name, string Glyph, string PurposeKey, bool Required, Func<bool> Ready,
        Func<IProgress<double>, CancellationToken, Task> Install);

    static readonly Component[] Components =
    [
        // A copy of yt-dlp found on PATH works, but VidFlow keeps its own up to date.
        new("yt-dlp", "", "Tools_YtDlpPurpose", true, () => ToolManager.IsYtDlpManaged, ToolManager.InstallYtDlpAsync),
        new("ffmpeg", "", "Tools_FfmpegPurpose", false, () => ToolManager.FfmpegPath is not null, ToolManager.InstallFfmpegAsync),
        // JavaScript runtime yt-dlp needs for YouTube (without it some formats are missing).
        new("Deno", "", "Tools_DenoPurpose", false, () => ToolManager.DenoPath is not null, ToolManager.InstallDenoAsync),
        // Several connections per download: a single one is often held well under 1 MB/s.
        new("aria2", "", "Tools_Aria2Purpose", false, () => ToolManager.Aria2Path is not null, ToolManager.InstallAria2Async),
    ];

    /// <summary>Header icon (none: VidFlow's own), title and explanation of each step.</summary>
    static readonly (string Glyph, string TitleKey, string BodyKey)[] Steps =
    [
        ("", "Wizard_WelcomeTitle", "Wizard_WelcomeBody"),
        ("", "Wizard_FolderTitle", "Wizard_FolderBody"),
        ("", "Wizard_AccountsTitle", "Wizard_AccountsBody"),
    ];

    static readonly (Platform Platform, string InKey, string OutKey)[] Accounts =
    [
        (Platform.Bilibili, "Account_BiliIn", "Account_BiliOut"),
        (Platform.YouTube, "Account_YtIn", "Account_YtOut"),
        (Platform.Douyin, "Account_DouyinIn", "Account_DouyinOut"),
    ];

    public FirstRunDialog()
    {
        InitializeComponent();
        FolderText.Text = AppSettings.Current.DownloadFolder;
        ToolTipService.SetToolTip(FolderText, AppSettings.Current.DownloadFolder);
        UpdateComponents();
        UpdateAccounts();
        ShowStep(0);
    }

    void ShowStep(int step)
    {
        _step = step;
        Pips.SelectedPageIndex = step;
        StepTitle.Text = L.Get(Steps[step].TitleKey);
        StepBody.Text = L.Get(Steps[step].BodyKey);
        StepImage.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepGlyph.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
        StepGlyph.Glyph = Steps[step].Glyph;
        Step1.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        var last = step == Steps.Length - 1;
        NextButton.Content = last ? L.Get("Wizard_Finish") : L.Get("Wizard_Next");
        BackButton.Visibility = step > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateComponents()
    {
        ComponentList.Children.Clear();
        foreach (var c in Components)
        {
            if (ComponentList.Children.Count > 0) ComponentList.Children.Add(Divider());
            var ready = c.Ready();
            var (glyph, brush, text) = ready ? ("", "SystemFillColorSuccessBrush", L.Get("Wizard_Ready"))
                : c.Required ? ("", "SystemFillColorCautionBrush", L.Get("Wizard_Required"))
                : ("", "TextFillColorSecondaryBrush", L.Get("Wizard_Recommended"));
            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (glyph.Length > 0) status.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = Brush(brush) });
            status.Children.Add(new TextBlock { Text = text, Foreground = Brush("TextFillColorSecondaryBrush") });
            var icon = new FontIcon { Glyph = c.Glyph, FontSize = 18, Foreground = Brush("AccentTextFillColorPrimaryBrush") };
            ComponentList.Children.Add(Row(icon, c.Name, L.Get(c.PurposeKey), status));
        }
        // The rows already say "ready": only offer to install when something is missing (or still installing).
        InstallRow.Visibility = _installing || Components.Any(c => !c.Ready()) ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateAccounts()
    {
        AccountList.Children.Clear();
        foreach (var (platform, inKey, outKey) in Accounts)
        {
            if (AccountList.Children.Count > 0) AccountList.Children.Add(Divider());
            var signedIn = AccountStore.IsLoggedIn(platform);
            FrameworkElement action;
            if (signedIn)
            {
                var done = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                done.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = Brush("SystemFillColorSuccessBrush") });
                done.Children.Add(new TextBlock { Text = L.Get("Wizard_SignedIn"), Foreground = Brush("TextFillColorSecondaryBrush") });
                action = done;
            }
            else
            {
                var button = new Button { Content = L.Get("Account_Login"), MinWidth = 80 };
                AutomationProperties.SetName(button, $"{L.Get("Account_Login")} {platform.DisplayName()}");
                button.Click += async (_, _) => await LoginAsync(platform);
                action = button;
            }
            var logo = new Image { Source = HomePlatform.LogoFor(platform), Width = 28, Height = 28 };
            AccountList.Children.Add(Row(logo, platform.DisplayName(), L.Get(signedIn ? inKey : outKey), action));
        }
    }

    /// <summary>A row in a card: an icon tile, the name over a one-line description, and a status or button on the right.</summary>
    static Grid Row(FrameworkElement icon, string name, string description, FrameworkElement right)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var tile = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = Brush("SubtleFillColorSecondaryBrush"),
            Child = icon,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Brush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        ToolTipService.SetToolTip(text, description);
        right.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 10, 16, 10), MinHeight = 60 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(tile);
        grid.Children.Add(text);
        grid.Children.Add(right);
        return grid;
    }

    static Border Divider() => new() { Height = 1, Background = Brush("CardStrokeColorDefaultBrush") };

    static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        InstallButton.IsEnabled = NextButton.IsEnabled = SkipButton.IsEnabled = false;
        InstallError.IsOpen = false;
        InstallStatus.Visibility = Visibility.Visible;
        try
        {
            // One after another, so the status line can say which one is downloading.
            foreach (var c in Components.Where(c => !c.Ready()))
            {
                InstallStatusText.Text = L.Format("Wizard_Installing", c.Name);
                InstallProgress.IsIndeterminate = true;
                var progress = new Progress<double>(p =>
                {
                    InstallProgress.IsIndeterminate = false;
                    InstallProgress.Value = p;
                });
                await Task.Run(() => c.Install(progress, CancellationToken.None));
                UpdateComponents();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Wizard install failed: {ex.Message}");
            InstallError.Title = L.Get("Settings_InstallFailed");
            InstallError.Message = ex.Message;
            InstallError.IsOpen = true;
        }
        finally
        {
            _installing = false;
            InstallButton.IsEnabled = NextButton.IsEnabled = SkipButton.IsEnabled = true;
            InstallStatus.Visibility = Visibility.Collapsed;
            UpdateComponents();
        }
    }

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;
        AppSettings.Current.DownloadFolder = folder;
        AppSettings.Save();
        FolderText.Text = folder;
    }

    async Task LoginAsync(Platform platform)
    {
        await new LoginWindow(platform).ShowAsync();
        UpdateAccounts();
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < Steps.Length - 1) ShowStep(_step + 1);
        else Hide();
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0) ShowStep(_step - 1);
    }

    void Skip_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>However it closes (finish, skip or Esc), the guide doesn't open by itself again.</summary>
    void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        AppSettings.Current.FirstRunCompleted = true;
        AppSettings.Save();
    }
}
