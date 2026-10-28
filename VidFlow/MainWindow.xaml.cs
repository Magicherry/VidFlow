using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using VidFlow.Localization;
using VidFlow.Pages;
using VidFlow.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace VidFlow;

public sealed partial class MainWindow : Window
{
    bool _reallyClosing;
    bool _trayHintShown;
    bool _selecting;

    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        var scale = GetDpiForWindow(Hwnd) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1120 * scale), (int)(780 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(560 * scale);
            presenter.PreferredMinimumHeight = (int)(500 * scale);
        }
        CenterOnScreen();

        ApplyTheme(AppSettings.Current.Theme);

        App.Downloads.ActiveCountChanged += UpdateBadge;
        AppWindow.Closing += AppWindow_Closing;

        NavView.SelectedItem = NavView.MenuItems[0];
    }

    public IntPtr Hwnd { get; }

    public bool IsForeground => AppWindow.IsVisible && GetForegroundWindow() == Hwnd;

    /// <summary>Shows a page by its nav tag. "about" is a sub-page of Settings: Settings stays selected in the pane.</summary>
    public void NavigateTo(string tag)
    {
        var item = NavView.MenuItems.Concat(NavView.FooterMenuItems)
            .OfType<NavigationViewItem>().First(i => (string)i.Tag == (tag == "about" ? "settings" : tag));
        _selecting = true;
        try
        {
            if (!ReferenceEquals(NavView.SelectedItem, item)) NavView.SelectedItem = item;
        }
        finally
        {
            _selecting = false;
        }
        Show(tag == "about" ? typeof(AboutPage) : PageFor(tag));
    }

    /// <summary>App update state: a dot on Settings, and in the title bar a button for the next step (download,
    /// progress, restart to install, retry).</summary>
    public void ShowUpdateState()
    {
        var u = App.Updates;
        SettingsBadge.Visibility = u.HasNewer ? Visibility.Visible : Visibility.Collapsed;

        var version = u.Latest?.Version;
        (string Glyph, string Text, string Tip)? look = u.HasNewer ? u.State switch
        {
            UpdateState.Available => ("", L.Format("Title_UpdateAvailable", version), L.Format("Title_UpdateAvailableTip", version)),
            UpdateState.Downloading => ("", L.Format("Title_UpdateDownloading", u.Progress.ToString("0")), L.Format("Title_UpdateDownloadingTip", version)),
            UpdateState.Ready => ("", L.Get("Title_UpdateReady"), L.Format("Title_UpdateReadyTip", version)),
            UpdateState.Failed => ("", L.Get("Title_UpdateFailed"), L.Format("Update_Failed", u.Error)),
            _ => null,
        } : null;
        TitleUpdateButton.Visibility = look is null ? Visibility.Collapsed : Visibility.Visible;
        if (look is not { } l) return;
        var downloading = u.State == UpdateState.Downloading;
        TitleUpdateIcon.Glyph = l.Glyph;
        TitleUpdateIcon.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
        TitleUpdateRing.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        TitleUpdateRing.IsActive = downloading;
        TitleUpdateRing.Value = u.Progress;
        TitleUpdateText.Text = l.Text;
        ToolTipService.SetToolTip(TitleUpdateButton, l.Tip);
        // Ready to install is the one that asks to be clicked.
        TitleUpdateButton.Style = (Style)Application.Current.Resources[
            u.State == UpdateState.Ready ? "AccentButtonStyle" : "DefaultButtonStyle"];
    }

    async void TitleUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        switch (App.Updates.State)
        {
            case UpdateState.Available:
            case UpdateState.Failed:
                await App.Updates.DownloadAsync();
                break;
            case UpdateState.Ready:
                await App.Updates.InstallAsync(Content.XamlRoot);
                break;
            default:
                NavigateTo("about");
                break;
        }
    }

    /// <summary>Opens one or more links (from drag-and-drop, a notification or the tray) on the home page.</summary>
    public void OpenLinks(string text)
    {
        NavigateTo("home");
        (ContentFrame.Content as HomePage)?.LoadText(text);
    }

    public async void PasteFromClipboard()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text)) OpenLinks(await content.GetTextAsync());
        }
        catch (Exception ex)
        {
            Log.Info($"Clipboard paste failed: {ex.Message}");
        }
    }

    public void BringToFront()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        SetForegroundWindow(Hwnd);
        Activate();
    }

    /// <summary>Closes without going to the tray (used by Exit).</summary>
    public void CloseForReal()
    {
        _reallyClosing = true;
        Close();
    }

    public void ApplyTheme(string theme)
    {
        var requested = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        RootGrid.RequestedTheme = requested;
        AppWindow.TitleBar.PreferredTheme = requested switch
        {
            ElementTheme.Light => TitleBarTheme.Light,
            ElementTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_reallyClosing && AppSettings.Current.MinimizeToTray && TrayIcon.IsAvailable)
        {
            // Keep running in the notification area so downloads continue.
            args.Cancel = true;
            AppWindow.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                NotificationService.StillRunningInTray();
            }
            return;
        }
        App.Downloads.Shutdown();
        TrayIcon.Dispose();
    }

    async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (!AppSettings.Current.FirstRunCompleted)
            await new FirstRunDialog { XamlRoot = RootGrid.XamlRoot }.ShowAsync();
    }

    void UpdateBadge()
    {
        var n = App.Downloads.ActiveCount;
        ActiveBadge.Value = n;
        ActiveBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static Type PageFor(object? tag) => tag switch
    {
        "downloads" => typeof(DownloadsPage),
        "templates" => typeof(TemplatesPage),
        "accounts" => typeof(AccountsPage),
        "settings" => typeof(SettingsPage),
        _ => typeof(HomePage),
    };

    void Show(Type page)
    {
        if (ContentFrame.CurrentSourcePageType == page) return;
        // Settings ⇄ its sub-page slide like a drill-in; everything else uses the usual entrance.
        NavigationTransitionInfo transition =
            page == typeof(AboutPage) ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight }
            : page == typeof(SettingsPage) && ContentFrame.CurrentSourcePageType == typeof(AboutPage)
                ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft }
            : new EntranceNavigationTransitionInfo();
        ContentFrame.Navigate(page, null, transition);
    }

    void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // NavigateTo picks the page itself (it may be Settings' sub-page).
        if (_selecting) return;
        Show(PageFor((args.SelectedItem as NavigationViewItem)?.Tag));
    }

    /// <summary>Clicking Settings again while on its sub-page goes back up to Settings.</summary>
    void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is "settings" && ContentFrame.CurrentSourcePageType == typeof(AboutPage))
            Show(typeof(SettingsPage));
    }

    /// <summary>Narrow windows keep the icon strip (with the pane button on top) rather than hiding the pane, which
    /// would leave the button over the page title; widening again brings the full pane back, which WinUI only does
    /// by itself coming from the hidden pane.</summary>
    void NavView_DisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        if (args.DisplayMode == NavigationViewDisplayMode.Expanded) sender.IsPaneOpen = true;
    }

    void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.WebLink) || e.DataView.Contains(StandardDataFormats.Text)
            || e.DataView.Contains(StandardDataFormats.Uri))
        {
            e.AcceptedOperation = DataPackageOperation.Link | DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Localization.L.Get("Drop_Caption");
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    async void Root_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        try
        {
            string? text = null;
            if (e.DataView.Contains(StandardDataFormats.WebLink)) text = (await e.DataView.GetWebLinkAsync()).ToString();
            else if (e.DataView.Contains(StandardDataFormats.Text)) text = await e.DataView.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text)) OpenLinks(text);
        }
        catch (Exception ex)
        {
            Log.Info($"Drop failed: {ex.Message}");
        }
    }

    void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}
