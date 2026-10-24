using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace VidFlow.Pages;

public sealed partial class HomePage : Page
{
    /// <summary>Heights offered when the exact formats are not known (playlists, batches).</summary>
    static readonly int[] GenericHeights = [2160, 1440, 1080, 720, 480, 360];

    VideoInfo? _video;
    PlaylistInfo? _playlist;
    IReadOnlyList<string> _batch = [];
    ImageSource? _thumb;
    CancellationTokenSource? _parseCts;
    volatile bool _browserCookiesFailed;
    Platform? _loginPlatform;

    public HomePage()
    {
        InitializeComponent();
        YtDlpService.BrowserCookiesFailed += () => _browserCookiesFailed = true;
        ModeCombo.Items.Add(new ComboBoxItem { Content = L.Get("Mode_Video"), Tag = "video" });
        ModeCombo.Items.Add(new ComboBoxItem { Content = L.Get("Mode_Mp3"), Tag = "mp3" });
        ModeCombo.Items.Add(new ComboBoxItem { Content = L.Get("Mode_M4a"), Tag = "m4a" });
        ModeCombo.SelectedIndex = AppSettings.Current.DefaultMode switch { "mp3" => 1, "m4a" => 2, _ => 0 };
    }

    public static string FormatDuration(double? seconds) => seconds is > 0 ? Format.Duration(seconds.Value) : "";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ToolsInfoBar.IsOpen = ToolManager.YtDlpPath is null;
        SubsToggle.IsOn = AppSettings.Current.WriteSubtitles;
        FolderCard.Description = AppSettings.Current.DownloadFolder;

        // Pre-fill a supported link from the clipboard so a copy → switch → click flow needs no paste.
        if (UrlBox.Text.Length == 0)
        {
            var text = await ReadClipboardTextAsync();
            var url = UrlTools.ExtractUrl(text);
            if (UrlTools.IsSupported(url)) UrlBox.Text = url!;
        }
    }

    /// <summary>Entry point for links coming from drag-and-drop, notifications or the tray.</summary>
    public async void LoadText(string text)
    {
        var urls = UrlTools.ExtractUrls(text);
        if (urls.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Warning, L.Get("Home_NoLink"));
            return;
        }
        UrlBox.Text = string.Join(Environment.NewLine, urls);
        await ParseAsync();
    }

    static async Task<string?> ReadClipboardTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
        }
        catch
        {
            return null;
        }
    }

    async void Paste_Click(object sender, RoutedEventArgs e)
    {
        var text = await ReadClipboardTextAsync();
        if (UrlTools.ExtractUrls(text).Count == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, L.Get("Home_ClipboardEmpty"));
            return;
        }
        LoadText(text!);
    }

    async void UrlBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter parses; Shift+Enter adds a line for pasting several links.
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && !shift)
        {
            e.Handled = true;
            await ParseAsync();
        }
    }

    async void Parse_Click(object sender, RoutedEventArgs e) => await ParseAsync();

    void CancelParse_Click(object sender, RoutedEventArgs e) => _parseCts?.Cancel();

    async Task ParseAsync()
    {
        var urls = UrlTools.ExtractUrls(UrlBox.Text);
        if (urls.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Warning, L.Get("Home_InvalidLink"));
            return;
        }
        if (ToolManager.YtDlpPath is null)
        {
            ToolsInfoBar.IsOpen = true;
            return;
        }

        _parseCts?.Cancel();
        ResetResults();
        if (urls.Count > 1)
        {
            ShowBatch(urls);
            return;
        }

        var url = urls[0];
        UrlBox.Text = url;
        var cts = _parseCts = new CancellationTokenSource();
        LoadingCard.Visibility = Visibility.Visible;
        ParseButton.IsEnabled = false;
        _browserCookiesFailed = false;
        try
        {
            var info = await Task.Run(() => YtDlpService.GetInfoAsync(url, cts.Token));
            if (cts.IsCancellationRequested) return;
            if (info is PlaylistInfo playlist) ShowPlaylist(playlist);
            else ShowVideo((VideoInfo)info);
            ShowHints(info.Platform);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, ex.Message, L.Get("Home_ParseFailed"));
        }
        finally
        {
            if (_parseCts == cts)
            {
                LoadingCard.Visibility = Visibility.Collapsed;
                ParseButton.IsEnabled = true;
            }
        }
    }

    void ResetResults()
    {
        _video = null;
        _playlist = null;
        _batch = [];
        _thumb = null;
        MessageBar.IsOpen = false;
        PreviewCard.Visibility = PlaylistCard.Visibility = BatchCard.Visibility = OptionsPanel.Visibility = Visibility.Collapsed;
    }

    async void ShowVideo(VideoInfo info)
    {
        _video = info;
        ThumbImage.Source = null;
        TitleText.Text = info.Title;
        PlatformText.Text = info.Platform.DisplayName();
        PlatformTag.Background = new SolidColorBrush(info.Platform switch
        {
            Platform.YouTube => ColorHelper.FromArgb(255, 230, 33, 23),
            Platform.Bilibili => ColorHelper.FromArgb(255, 251, 114, 153),
            _ => Colors.Gray,
        });
        MetaText.Text = string.Join("  ·  ", new[] { info.Uploader, Format.Date(info.UploadDate) }.Where(s => !string.IsNullOrEmpty(s)));
        DurationBadge.Visibility = info.Duration is > 0 ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Text = FormatDuration(info.Duration);

        QualityCombo.Items.Clear();
        var best = info.Qualities.FirstOrDefault();
        QualityCombo.Items.Add(new ComboBoxItem
        {
            Content = best is null ? L.Get("Quality_Best") : $"{L.Get("Quality_Best")}  ·  {Format.Quality(best)}",
            Tag = 0,
        });
        foreach (var q in info.Qualities)
            QualityCombo.Items.Add(new ComboBoxItem { Content = Format.Quality(q), Tag = q.Height });
        SelectPreferredQuality();

        PreviewCard.Visibility = OptionsPanel.Visibility = Visibility.Visible;
        UpdateDownloadButton();

        var thumb = await ImageLoader.LoadAsync(info.ThumbnailUrl);
        if (_video == info) ThumbImage.Source = _thumb = thumb;
    }

    async void ShowPlaylist(PlaylistInfo info)
    {
        _playlist = info;
        PlaylistThumbImage.Source = null;
        var isMultiPart = info.Platform == Platform.Bilibili;
        PlaylistKind.Text = isMultiPart ? L.Get("Playlist_KindParts") : L.Get("Playlist_KindPlaylist");
        PlaylistTitle.Text = info.Title;
        PlaylistMeta.Text = string.Join("  ·  ", new[] { L.Format("Playlist_Count", info.Entries.Count), info.Uploader }
            .Where(s => !string.IsNullOrEmpty(s)));

        EntriesList.ItemsSource = info.Entries;
        EntriesList.SelectAll();
        FillGenericQualities();

        PlaylistCard.Visibility = OptionsPanel.Visibility = Visibility.Visible;
        UpdateDownloadButton();

        var thumb = await ImageLoader.LoadAsync(info.ThumbnailUrl);
        if (_playlist == info) PlaylistThumbImage.Source = thumb;
    }

    void ShowBatch(IReadOnlyList<string> urls)
    {
        _batch = urls;
        BatchTitle.Text = L.Format("Batch_Title", urls.Count);
        BatchList.ItemsSource = urls;
        FillGenericQualities();
        BatchCard.Visibility = OptionsPanel.Visibility = Visibility.Visible;
        UpdateDownloadButton();
        if (urls.Any(u => !UrlTools.IsSupported(u)))
            ShowMessage(InfoBarSeverity.Informational, L.Get("Batch_UnsupportedHint"));
    }

    void FillGenericQualities()
    {
        QualityCombo.Items.Clear();
        QualityCombo.Items.Add(new ComboBoxItem { Content = L.Get("Quality_Best"), Tag = 0 });
        foreach (var h in GenericHeights)
            QualityCombo.Items.Add(new ComboBoxItem { Content = Format.Height(h), Tag = h });
        SelectPreferredQuality();
    }

    void SelectPreferredQuality()
    {
        var preferred = AppSettings.Current.DefaultHeight;
        var items = QualityCombo.Items.Cast<ComboBoxItem>().ToList();
        QualityCombo.SelectedIndex = preferred == 0 ? 0 : Math.Max(0, items.FindIndex(i => (int)i.Tag <= preferred && (int)i.Tag > 0));
    }

    void ShowHints(Platform platform)
    {
        if (_browserCookiesFailed)
        {
            ShowMessage(InfoBarSeverity.Warning, L.Get("Home_BrowserCookiesFailed"), L.Get("Home_BrowserCookiesFailedTitle"),
                loginPlatform: platform == Platform.Other ? null : platform);
        }
        else if (ToolManager.FfmpegPath is null)
        {
            ShowMessage(InfoBarSeverity.Warning, L.Get("Home_NoFfmpeg"), L.Get("Home_NoFfmpegTitle"));
        }
        else if (platform == Platform.Bilibili && !AccountStore.IsLoggedIn(Platform.Bilibili)
                 && AppSettings.Current.CookiesBrowser.Length == 0 && AppSettings.Current.CookiesFile.Length == 0)
        {
            ShowMessage(InfoBarSeverity.Informational, L.Get("Home_BiliLoginHint"), L.Get("Home_BiliLoginHintTitle"),
                loginPlatform: Platform.Bilibili);
        }
    }

    void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDownloadButton();

    void SelectAll_Click(object sender, RoutedEventArgs e) => EntriesList.SelectAll();

    void SelectNone_Click(object sender, RoutedEventArgs e) => EntriesList.DeselectRange(
        new Microsoft.UI.Xaml.Data.ItemIndexRange(0, (uint)(_playlist?.Entries.Count ?? 0)));

    void UpdateDownloadButton()
    {
        if (_playlist is not null)
        {
            var n = EntriesList.SelectedItems.Count;
            SelectionText.Text = L.Format("Playlist_Selected", n, _playlist.Entries.Count);
            DownloadButtonText.Text = L.Format("Download_Selected", n);
            DownloadButton.IsEnabled = n > 0;
        }
        else if (_batch.Count > 0)
        {
            DownloadButtonText.Text = L.Format("Download_Batch", _batch.Count);
            DownloadButton.IsEnabled = true;
        }
        else
        {
            DownloadButtonText.Text = L.Get("Download_Start");
            DownloadButton.IsEnabled = _video is not null;
        }
    }

    void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        QualityCard.IsEnabled = SelectedMode == "video";

    string SelectedMode => (ModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "video";
    int SelectedHeight => SelectedMode == "video" ? (QualityCombo.SelectedItem as ComboBoxItem)?.Tag as int? ?? 0 : 0;

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;
        AppSettings.Current.DownloadFolder = folder;
        AppSettings.Save();
        FolderCard.Description = folder;
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        // Guard against queueing a huge playlist / multi-part video with one click.
        var count = _playlist is not null ? EntriesList.SelectedItems.Count : _batch.Count;
        if (count > 50)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("Home_ConfirmManyTitle"),
                Content = L.Format("Home_ConfirmManyBody", count),
                PrimaryButtonText = L.Format("Download_Selected", count),
                CloseButtonText = L.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        var mode = SelectedMode;
        var height = SelectedHeight;
        var folder = AppSettings.Current.DownloadFolder;

        string added;
        if (_playlist is { } playlist)
        {
            // Each playlist goes into its own subfolder.
            var sub = Path.Combine(folder, SafeFolderName(playlist.Title));
            var selected = EntriesList.SelectedItems.Cast<PlaylistEntry>().OrderBy(x => x.Index).ToList();
            foreach (var entry in selected)
                App.Downloads.Enqueue(new DownloadRequest(entry.Url, mode, height, SubsToggle.IsOn, sub), entry.Title,
                    entry.ThumbnailUrl, entry.Index);
            added = L.Format("Home_AddedMany", selected.Count);
        }
        else if (_batch.Count > 0)
        {
            foreach (var url in _batch)
                App.Downloads.Enqueue(new DownloadRequest(url, mode, height, SubsToggle.IsOn, folder), url, null);
            added = L.Format("Home_AddedMany", _batch.Count);
        }
        else if (_video is { } video)
        {
            App.Downloads.Enqueue(new DownloadRequest(video.Url, mode, height, SubsToggle.IsOn, folder), video.Title,
                video.ThumbnailUrl, thumbnail: _thumb);
            added = L.Format("Home_AddedOne", video.Title);
        }
        else
        {
            return;
        }

        // Remember the choices as the defaults for next time.
        AppSettings.Current.DefaultMode = mode;
        if (mode == "video") AppSettings.Current.DefaultHeight = height;
        AppSettings.Current.WriteSubtitles = SubsToggle.IsOn;
        AppSettings.Save();

        ShowMessage(InfoBarSeverity.Success, added, L.Get("Home_Added"), showViewButton: true);
        UrlBox.Text = "";
    }

    static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length switch { 0 => "Playlist", > 80 => clean[..80], _ => clean };
    }

    void ShowMessage(InfoBarSeverity severity, string message, string? title = null,
        bool showViewButton = false, Platform? loginPlatform = null)
    {
        MessageBar.Severity = severity;
        MessageBar.Title = title ?? "";
        MessageBar.Message = message;
        _loginPlatform = loginPlatform;
        MessageActionButton.Content = loginPlatform is not null ? L.Get("Home_LoginNow") : L.Get("Home_ViewDownloads");
        MessageActionButton.Visibility = showViewButton || loginPlatform is not null ? Visibility.Visible : Visibility.Collapsed;
        MessageBar.IsOpen = true;
    }

    async void MessageAction_Click(object sender, RoutedEventArgs e)
    {
        if (_loginPlatform is not { } platform)
        {
            App.MainWindow.NavigateTo("downloads");
            return;
        }
        MessageBar.IsOpen = false;
        var url = _video?.Url ?? _playlist?.Url;
        await new LoginWindow(platform).ShowAsync();
        // Re-parse so the quality list reflects what the signed-in account can access.
        if (AccountStore.IsLoggedIn(platform) && url is not null)
        {
            UrlBox.Text = url;
            await ParseAsync();
        }
    }

    void GoToSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings");
}
