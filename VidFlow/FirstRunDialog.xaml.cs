using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow;

/// <summary>First-run setup in three steps: install components, pick the download folder, optionally sign in.</summary>
public sealed partial class FirstRunDialog : ContentDialog
{
    int _step;
    bool _installing;

    public FirstRunDialog()
    {
        InitializeComponent();
        Title = "VidFlow";
        FolderText.Text = AppSettings.Current.DownloadFolder;
        UpdateComponents();
        UpdateAccounts();
        ShowStep(0);
    }

    void ShowStep(int step)
    {
        _step = step;
        Pips.SelectedPageIndex = step;
        Step1.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButtonText = step == 2 ? L.Get("Wizard_Finish") : L.Get("Wizard_Next");
        SecondaryButtonText = step > 0 ? L.Get("Wizard_Back") : "";
    }

    void UpdateComponents()
    {
        var hasYt = ToolManager.YtDlpPath is not null;
        var hasFf = ToolManager.FfmpegPath is not null;
        YtDlpIcon.Glyph = hasYt ? "" : "";
        FfmpegIcon.Glyph = hasFf ? "" : "";
        YtDlpStatus.Text = hasYt ? L.Get("Wizard_YtDlpReady") : L.Get("Wizard_YtDlpMissing");
        FfmpegStatus.Text = hasFf ? L.Get("Wizard_FfmpegReady") : L.Get("Wizard_FfmpegMissing");
        InstallButton.Visibility = hasYt && hasFf && ToolManager.IsYtDlpManaged && ToolManager.DenoPath is not null
            ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateAccounts()
    {
        BiliLoginButton.Content = AccountStore.IsLoggedIn(Platform.Bilibili) ? L.Get("Wizard_BiliDone") : L.Get("Wizard_BiliLogin");
        YtLoginButton.Content = AccountStore.IsLoggedIn(Platform.YouTube) ? L.Get("Wizard_YtDone") : L.Get("Wizard_YtLogin");
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        InstallButton.IsEnabled = false;
        IsPrimaryButtonEnabled = false;
        InstallError.Visibility = Visibility.Collapsed;
        InstallProgress.Visibility = Visibility.Visible;
        InstallProgress.IsIndeterminate = true;
        try
        {
            var progress = new Progress<double>(p =>
            {
                InstallProgress.IsIndeterminate = false;
                InstallProgress.Value = p;
            });
            if (!ToolManager.IsYtDlpManaged) await Task.Run(() => ToolManager.InstallYtDlpAsync(progress, CancellationToken.None));
            UpdateComponents();
            if (ToolManager.FfmpegPath is null) await Task.Run(() => ToolManager.InstallFfmpegAsync(progress, CancellationToken.None));
            // JavaScript runtime yt-dlp needs for YouTube (without it some formats are missing).
            if (ToolManager.DenoPath is null) await Task.Run(() => ToolManager.InstallDenoAsync(progress, CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Warn($"Wizard install failed: {ex.Message}");
            InstallError.Text = L.Format("Settings_InstallFailedMessage", ex.Message);
            InstallError.Visibility = Visibility.Visible;
        }
        finally
        {
            _installing = false;
            InstallButton.IsEnabled = true;
            IsPrimaryButtonEnabled = true;
            InstallProgress.Visibility = Visibility.Collapsed;
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

    async void BiliLogin_Click(object sender, RoutedEventArgs e) => await LoginAsync(Platform.Bilibili);
    async void YtLogin_Click(object sender, RoutedEventArgs e) => await LoginAsync(Platform.YouTube);

    async Task LoginAsync(Platform platform)
    {
        await new LoginWindow(platform).ShowAsync();
        UpdateAccounts();
    }

    void Next_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_step < 2)
        {
            args.Cancel = true; // stay open
            ShowStep(_step + 1);
            return;
        }
        Complete();
    }

    void Back_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_step > 0) ShowStep(_step - 1);
    }

    void Skip_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args) => Complete();

    static void Complete()
    {
        AppSettings.Current.FirstRunCompleted = true;
        AppSettings.Save();
    }
}
