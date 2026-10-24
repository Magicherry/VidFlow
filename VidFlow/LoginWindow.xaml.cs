using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;
using Windows.Graphics;

namespace VidFlow;

/// <summary>Embedded browser for signing in to Bilibili / YouTube. Cookies are exported when it closes.</summary>
public sealed partial class LoginWindow : Window
{
    readonly Platform _platform;
    readonly TaskCompletionSource _closed = new();
    bool _exported;

    public LoginWindow(Platform platform)
    {
        _platform = platform;
        InitializeComponent();

        Title = LoginTitleBar.Title = platform == Platform.Bilibili ? L.Get("Login_TitleBili") : L.Get("Login_TitleYt");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(LoginTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RootGrid.RequestedTheme = ((FrameworkElement)App.MainWindow.Content).RequestedTheme;

        var scale = App.MainWindow.Content.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(1100 * scale), (int)(780 * scale)));
        var main = App.MainWindow.AppWindow;
        AppWindow.Move(new PointInt32(
            main.Position.X + (main.Size.Width - AppWindow.Size.Width) / 2,
            main.Position.Y + (main.Size.Height - AppWindow.Size.Height) / 2));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMinimizable = false;
        }

        // Export while the WebView is still alive: by the time Closed fires it is torn down, and touching it
        // there can throw (which used to leave callers waiting forever with their button disabled).
        AppWindow.Closing += async (_, args) =>
        {
            if (_exported) return;
            args.Cancel = true;
            await ExportOnceAsync();
            Close();
        };
        Closed += (_, _) => _closed.TrySetResult();
        _ = InitAsync();
    }

    /// <summary>Shows the window and completes when it is closed and cookies are saved.</summary>
    public Task ShowAsync()
    {
        Activate();
        return _closed.Task;
    }

    async Task InitAsync()
    {
        try
        {
            var env = await AccountService.CreateEnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                LoadingRing.IsActive = false;
                await CheckLoggedInAsync();
            };
            Web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // Keep pop-ups (e.g. third-party login) inside this window.
                e.Handled = true;
                Web.CoreWebView2.Navigate(e.Uri);
            };
            Web.CoreWebView2.Navigate(AccountService.LoginUrl(_platform));
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            Log.Warn($"WebView2 failed: {ex.Message}");
            StatusIcon.Glyph = "";
            StatusText.Text = L.Format("Login_WebViewFailed", ex.Message);
        }
    }

    async Task CheckLoggedInAsync()
    {
        if (await AccountService.HasSessionCookieAsync(Web.CoreWebView2, _platform))
        {
            StatusIcon.Glyph = "";
            StatusText.Text = L.Get("Login_Success");
        }
    }

    async Task ExportOnceAsync()
    {
        if (_exported) return;
        // Set first: the Closing handler relies on it to let the second Close() through, even when the
        // window is closed before WebView2 finished starting.
        _exported = true;
        try
        {
            if (Web.CoreWebView2 is { } core) await AccountService.ExportAsync(core);
        }
        catch (Exception ex)
        {
            // Nothing useful to do if the profile cannot be read; the status simply stays "not signed in".
            Log.Warn($"Cookie export failed: {ex.Message}");
        }
    }

    void Done_Click(object sender, RoutedEventArgs e)
    {
        DoneButton.IsEnabled = false;
        Close(); // Closing exports the cookies.
    }
}
