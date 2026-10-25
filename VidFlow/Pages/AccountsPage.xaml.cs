using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Pages;

/// <summary>Sign-in for each platform, plus the other cookie sources yt-dlp can use.</summary>
public sealed partial class AccountsPage : Page
{
    bool _loading;

    public AccountsPage()
    {
        InitializeComponent();
        SettingsPage.AddItems(BrowserCombo, ("", L.Get("Settings_BrowserNone")), ("edge", "Microsoft Edge"), ("chrome", "Google Chrome"),
            ("firefox", "Firefox"), ("brave", "Brave"), ("opera", "Opera"), ("vivaldi", "Vivaldi"));

        // Follow sign-ins wherever they happen (this page, the home page's prompt, the setup wizard), the moment
        // the cookies are saved rather than when the login window has finished closing.
        Loaded += (_, _) => AccountStore.Changed += OnAccountsChanged;
        Unloaded += (_, _) => AccountStore.Changed -= OnAccountsChanged;
    }

    void OnAccountsChanged() => DispatcherQueue.TryEnqueue(UpdateAccounts);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        SettingsPage.Select(BrowserCombo, AppSettings.Current.CookiesBrowser);
        _loading = false;
        UpdateCookieFile();
        UpdateAccounts();
    }

    void UpdateAccounts()
    {
        var bili = AccountStore.IsLoggedIn(Platform.Bilibili);
        BiliAccountCard.Description = bili ? L.Get("Account_BiliIn") : L.Get("Account_BiliOut");
        BiliAccountButton.Content = bili ? L.Get("Account_Logout") : L.Get("Account_Login");

        var yt = AccountStore.IsLoggedIn(Platform.YouTube);
        YouTubeAccountCard.Description = yt ? L.Get("Account_YtIn") : L.Get("Account_YtOut");
        YouTubeAccountButton.Content = yt ? L.Get("Account_Logout") : L.Get("Account_Login");

        var douyin = AccountStore.IsLoggedIn(Platform.Douyin);
        DouyinAccountCard.Description = douyin ? L.Get("Account_DouyinIn") : L.Get("Account_DouyinOut");
        DouyinAccountButton.Content = douyin ? L.Get("Account_Logout") : L.Get("Account_Login");
    }

    async void BiliAccount_Click(object sender, RoutedEventArgs e) => await ToggleAccountAsync(Platform.Bilibili, BiliAccountButton);
    async void YouTubeAccount_Click(object sender, RoutedEventArgs e) => await ToggleAccountAsync(Platform.YouTube, YouTubeAccountButton);
    async void DouyinAccount_Click(object sender, RoutedEventArgs e) => await ToggleAccountAsync(Platform.Douyin, DouyinAccountButton);

    async Task ToggleAccountAsync(Platform platform, Button button)
    {
        button.IsEnabled = false;
        ErrorBar.IsOpen = false;
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
            ErrorBar.Title = L.Get("Common_Failed");
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            button.IsEnabled = true;
            UpdateAccounts();
        }
    }

    void BrowserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BrowserCombo.SelectedItem is not ComboBoxItem item) return;
        AppSettings.Current.CookiesBrowser = (string)item.Tag;
        AppSettings.Save();
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
}
