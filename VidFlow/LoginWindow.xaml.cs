using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace VidFlow;

/// <summary>
/// Sign-in for Bilibili / YouTube. Bilibili opens on a QR code to scan with its app (no web page needed), with a
/// link to the web page for password / SMS sign-in; YouTube goes straight to Google's page. Either way the cookies
/// end up in the WebView2 profile and are exported for yt-dlp when the window closes, which it does by itself once
/// the sign-in is detected.
/// </summary>
public sealed partial class LoginWindow : Window
{
    readonly Platform _platform;
    readonly TaskCompletionSource _closed = new();
    readonly Task _webReady;
    CancellationTokenSource? _qr;
    bool _exported;
    bool _closing;

    public LoginWindow(Platform platform)
    {
        _platform = platform;
        InitializeComponent();

        Title = LoginTitleBar.Title = platform switch
        {
            Platform.Bilibili => L.Get("Login_TitleBili"),
            Platform.Douyin => L.Get("Login_TitleDouyin"),
            _ => L.Get("Login_TitleYt"),
        };
        // Douyin's home page has no sign-in form of its own: say where its sign-in button is.
        if (platform == Platform.Douyin) StatusText.Text = L.Get("Login_InstructionsDouyin");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(LoginTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RootGrid.RequestedTheme = ((FrameworkElement)App.MainWindow.Content).RequestedTheme;
        if (AppWindow.Presenter is OverlappedPresenter p) p.IsMinimizable = false;

        // Export while the WebView is still alive: by the time Closed fires it is torn down, and touching it
        // there can throw (which used to leave callers waiting forever with their button disabled).
        AppWindow.Closing += async (_, args) =>
        {
            _qr?.Cancel();
            if (_exported) return;
            args.Cancel = true;
            await ExportOnceAsync();
            Close();
        };
        Closed += (_, _) => _closed.TrySetResult();

        _webReady = InitWebAsync();
        if (platform == Platform.Bilibili) ShowQr();
        else ShowWeb();
    }

    /// <summary>Shows the window and completes when it is closed and cookies are saved.</summary>
    public Task ShowAsync()
    {
        Activate();
        return _closed.Task;
    }

    /// <summary>Sizes the window (in view pixels) and centres it over the main window.</summary>
    void Place(int width, int height)
    {
        var scale = App.MainWindow.Content.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
        var main = App.MainWindow.AppWindow;
        AppWindow.Move(new PointInt32(
            main.Position.X + (main.Size.Width - AppWindow.Size.Width) / 2,
            main.Position.Y + (main.Size.Height - AppWindow.Size.Height) / 2));
    }

    // ---- Web sign-in -------------------------------------------------------------------------------------------

    async Task InitWebAsync()
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
        }
        catch (Exception ex)
        {
            LoadingRing.IsActive = false;
            Log.Warn($"WebView2 failed: {ex.Message}");
            StatusIcon.Glyph = "";
            StatusText.Text = L.Format("Login_WebViewFailed", ex.Message);
            throw;
        }
    }

    async void ShowWeb()
    {
        _qr?.Cancel();
        QrPanel.Visibility = Visibility.Collapsed;
        WebStatusBar.Visibility = WebHost.Visibility = Visibility.Visible;
        Place(1100, 780);
        try
        {
            await _webReady;
            Web.CoreWebView2.Navigate(AccountService.LoginUrl(_platform));
        }
        catch
        {
            // Reported in the status line by InitWebAsync.
        }
    }

    void UseWebLogin_Click(object sender, RoutedEventArgs e) => ShowWeb();

    async Task CheckLoggedInAsync()
    {
        if (_closing || !await AccountService.HasSessionCookieAsync(Web.CoreWebView2, _platform)) return;
        StatusIcon.Glyph = "";
        StatusText.Text = L.Get("Login_SuccessClosing");
        DoneButton.IsEnabled = false;
        // Let the site finish setting its cookies, then close (which saves them).
        await CloseSoonAsync(TimeSpan.FromSeconds(1.5));
    }

    async Task CloseSoonAsync(TimeSpan delay)
    {
        if (_closing) return;
        _closing = true;
        await Task.Delay(delay);
        Close();
    }

    async Task ExportOnceAsync()
    {
        if (_exported) return;
        // Set first: the Closing handler relies on it to let the second Close() through, even when the
        // window is closed before WebView2 finished starting.
        _exported = true;
        try
        {
            await _webReady;
            await AccountService.ExportAsync(Web.CoreWebView2);
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

    // ---- Bilibili QR sign-in -----------------------------------------------------------------------------------

    void ShowQr()
    {
        QrPanel.Visibility = Visibility.Visible;
        Place(460, 620);
        _ = RunQrAsync();
    }

    void QrRefresh_Click(object sender, RoutedEventArgs e) => _ = RunQrAsync();

    /// <summary>Gets a code, shows it, and follows it until it is confirmed, expires or the window moves on.</summary>
    async Task RunQrAsync()
    {
        _qr?.Cancel();
        var cts = _qr = new CancellationTokenSource();
        var ct = cts.Token;
        ShowQrState(loading: true);
        try
        {
            var code = await BilibiliQrLogin.GenerateAsync(ct);
            QrImage.Source = await RenderQrAsync(code.Url);
            ShowQrState();

            var failures = 0;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                QrPollResult result;
                try
                {
                    result = await BilibiliQrLogin.PollAsync(code.Key, ct);
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ++failures < 5)
                {
                    continue; // A dropped request or two: keep asking.
                }

                switch (result.State)
                {
                    case QrLoginState.Scanned:
                        ShowQrState("", L.Get("Login_QrScanned"), "AccentTextFillColorPrimaryBrush");
                        break;
                    case QrLoginState.Expired:
                        ShowQrState("", L.Get("Login_QrExpired"), "TextFillColorSecondaryBrush", refresh: L.Get("Login_QrRefresh"));
                        return;
                    case QrLoginState.Success:
                        await _webReady;
                        AccountService.AddCookies(Web.CoreWebView2, result.Cookies);
                        ShowQrState("", L.Get("Login_QrSuccess"), "SystemFillColorSuccessBrush");
                        // Save straight away so the app shows "signed in" while this window says so too.
                        await ExportOnceAsync();
                        await CloseSoonAsync(TimeSpan.FromSeconds(0.8));
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Bilibili QR login failed: {ex.Message}");
            if (!ct.IsCancellationRequested)
                ShowQrState("", L.Get("Login_QrFailed"), "SystemFillColorCautionBrush", refresh: L.Get("Login_QrRetry"));
        }
    }

    /// <summary>Loading spinner, the plain code, or the code covered by a status (with an optional refresh button).</summary>
    void ShowQrState(string? glyph = null, string? text = null, string brush = "", string? refresh = null, bool loading = false)
    {
        QrRing.IsActive = loading;
        QrRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        QrImage.Opacity = loading ? 0 : 1;
        QrOverlay.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
        if (glyph is null) return;
        QrOverlayIcon.Glyph = glyph;
        QrOverlayIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush];
        QrOverlayText.Text = text;
        QrRefreshButton.Visibility = refresh is null ? Visibility.Collapsed : Visibility.Visible;
        if (refresh is not null) QrRefreshButton.Content = refresh;
    }

    static async Task<BitmapImage> RenderQrAsync(string text)
    {
        using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10, drawQuietZones: false);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }
}
