using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using VidFlow.Controls;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace VidFlow.Pages;

public sealed partial class HomePage : Page
{
    VideoInfo? _video;
    PlaylistInfo? _playlist;
    List<BatchEntry> _batch = [];
    bool _batchSelectionTouched;  // once the user ticks / unticks, finished parses stop auto-selecting
    bool _batchSelecting;          // selection changes made by code, not the user
    ImageSource? _thumb;
    CancellationTokenSource? _parseCts;
    volatile bool _browserCookiesFailed;
    Platform? _loginPlatform;

    /// <summary>Template the current task started from; the editor works on a copy (per-task overrides).</summary>
    DownloadTemplate _template = TemplateStore.Default;
    bool _fillingTemplates;

    public HomePage()
    {
        InitializeComponent();
        YtDlpService.BrowserCookiesFailed += () => _browserCookiesFailed = true;
        OptionsEditor.Changed += UpdateTemplateState;
        TemplateStore.Changed += () => DispatcherQueue.TryEnqueue(FillTemplates);
        FillTemplates();
        UseTemplate(TemplateStore.Default);
    }

    public static string FormatDuration(double? seconds) => seconds is > 0 ? Format.Duration(seconds.Value) : "";

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ToolsInfoBar.IsOpen = ToolManager.YtDlpPath is null;
        UpdateFolderDescription();

        // Pre-fill a supported link from the clipboard so a copy → switch → click flow needs no paste.
        if (UrlBox.Text.Length == 0)
        {
            var text = await ReadClipboardTextAsync();
            var url = UrlTools.ExtractUrl(text);
            if (UrlTools.IsSupported(url)) UrlBox.Text = url!;
        }
    }

    // ---- Templates ---------------------------------------------------------------------------------------------

    void FillTemplates()
    {
        _fillingTemplates = true;
        TemplateCombo.Items.Clear();
        var defaultId = TemplateStore.Default.Id;
        foreach (var t in TemplateStore.All)
            TemplateCombo.Items.Add(new ComboBoxItem
            {
                Content = t.Id == defaultId ? L.Format("Template_DefaultSuffix", t.DisplayName) : t.DisplayName,
                Tag = t.Id,
            });
        TemplateCombo.SelectedItem = TemplateCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (Guid)i.Tag == _template.Id)
                                     ?? TemplateCombo.Items[0];
        _fillingTemplates = false;
    }

    void TemplateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingTemplates || TemplateCombo.SelectedItem is not ComboBoxItem { Tag: Guid id }) return;
        if (TemplateStore.Find(id) is { } t) UseTemplate(t);
    }

    /// <summary>Starts this task from a template: the editor gets a copy, so edits don't touch the template.</summary>
    void UseTemplate(DownloadTemplate t)
    {
        _template = t;
        OptionsEditor.Load(t.Clone(), _video);
        UpdateTemplateState();
    }

    void UpdateTemplateState()
    {
        var modified = !OptionsEditor.Options.SameOptionsAs(_template);
        ResetTemplateButton.Visibility = modified ? Visibility.Visible : Visibility.Collapsed;
        TemplateCard.Description = modified ? L.Get("Template_ModifiedHint") : DownloadLabels.Summary(_template);
        UpdateFolderDescription();
    }

    void ResetTemplate_Click(object sender, RoutedEventArgs e) => UseTemplate(_template);

    async void SaveAsTemplate_Click(object sender, RoutedEventArgs e)
    {
        var name = await TextPrompt.ShowAsync(XamlRoot, L.Get("Template_SaveAsTitle"), L.Get("Template_Name"),
            TemplateStore.UniqueName(L.Get("Template_NewName")));
        if (name is null) return;
        var copy = OptionsEditor.Options.Clone();
        copy.Id = Guid.NewGuid();
        copy.NameKey = null;
        copy.Name = name;
        TemplateStore.Save(copy);
        _template = copy;
        FillTemplates();
        UseTemplate(copy);
    }

    void UpdateFolderDescription()
    {
        var o = OptionsEditor.Options;
        FolderCard.Description = string.IsNullOrEmpty(o.FolderTemplate)
            ? AppSettings.Current.DownloadFolder
            : Path.Combine(AppSettings.Current.DownloadFolder, o.FolderTemplate.Replace('/', '\\'));
    }

    // ---- Links -------------------------------------------------------------------------------------------------

    /// <summary>Entry point for links coming from drag-and-drop, notifications or the tray.</summary>
    public async void LoadText(string text)
    {
        var urls = UrlTools.ExtractUrls(text);
        if (urls.Count == 0)
        {
            ShowMessage(InfoBarSeverity.Warning, L.Get("Home_NoLink"));
            return;
        }
        UrlBox.Text = string.Join(' ', urls);
        await ParseAsync();
    }

    static async Task<string?> ReadClipboardTextAsync()
    {
        // The clipboard is often locked for a moment by the app that just wrote to it; retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var content = Clipboard.GetContent();
                return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
            }
            catch when (attempt < 4)
            {
                await Task.Delay(60);
            }
            catch
            {
                return null;
            }
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
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ParseAsync();
        }
    }

    /// <summary>A single-line TextBox would keep only the first line of a multi-line paste; join the lines
    /// with spaces instead so pasting several links at once still works.</summary>
    void UrlBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        // Must be decided synchronously: after an await the TextBox has already done its own paste.
        e.Handled = true;
        _ = PasteJoinedAsync(UrlBox.SelectionStart, UrlBox.SelectionLength);
    }

    async Task PasteJoinedAsync(int start, int length)
    {
        var text = await ReadClipboardTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        var joined = string.Join(' ', text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        start = Math.Min(start, UrlBox.Text.Length);
        length = Math.Min(length, UrlBox.Text.Length - start);
        UrlBox.Text = UrlBox.Text.Remove(start, length).Insert(start, joined);
        UrlBox.SelectionStart = start + joined.Length;
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
        var cts = _parseCts = new CancellationTokenSource();
        if (urls.Count > 1)
        {
            await ParseBatchAsync(urls, cts.Token);
            return;
        }

        var url = urls[0];
        UrlBox.Text = url;
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

    /// <summary>Shows the options for what was parsed, keeping the user's current per-task choices.</summary>
    void ShowOptions()
    {
        OptionsEditor.Load(OptionsEditor.Options, _video);
        UpdateTemplateState();
        OptionsPanel.Visibility = Visibility.Visible;
        UpdateDownloadButton();
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
            Platform.Douyin => ColorHelper.FromArgb(255, 22, 24, 35),
            _ => Colors.Gray,
        });
        MetaText.Text = string.Join("  ·  ", new[] { info.Uploader, Format.Date(info.UploadDate) }.Where(s => !string.IsNullOrEmpty(s)));
        DurationBadge.Visibility = info.Duration is > 0 ? Visibility.Visible : Visibility.Collapsed;
        DurationText.Text = FormatDuration(info.Duration);

        PreviewCard.Visibility = Visibility.Visible;
        ShowOptions();

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

        PlaylistCard.Visibility = Visibility.Visible;
        ShowOptions();

        var thumb = await ImageLoader.LoadAsync(info.ThumbnailUrl);
        if (_playlist == info) PlaylistThumbImage.Source = thumb;
    }

    /// <summary>Several links: list them at once, parse them 3 at a time, and tick each one that parses so the
    /// user can untick what they don't want. Failed links stay unticked with the reason shown.</summary>
    async Task ParseBatchAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        _batch = urls.Select(u => new BatchEntry(u)).ToList();
        _batchSelectionTouched = false;
        BatchList.ItemsSource = _batch;
        BatchTitle.Text = L.Format("Batch_Title", urls.Count);
        BatchProgress.Maximum = urls.Count;
        BatchProgress.Value = 0;
        BatchProgress.Visibility = Visibility.Visible;
        BatchCard.Visibility = Visibility.Visible;
        ShowOptions();
        UpdateBatchStatus();
        if (urls.Any(u => !UrlTools.IsSupported(u)))
            ShowMessage(InfoBarSeverity.Informational, L.Get("Batch_UnsupportedHint"));

        using var gate = new SemaphoreSlim(3);
        var work = _batch.Select(async entry =>
        {
            try
            {
                await gate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                var info = await Task.Run(() => YtDlpService.GetInfoAsync(entry.Url, ct));
                if (ct.IsCancellationRequested) return;
                entry.SetResult(info);
                if (!_batchSelectionTouched) SelectBatchEntry(entry);
                entry.Thumbnail = await ImageLoader.LoadAsync(info.ThumbnailUrl);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) entry.SetError(ex.Message);
            }
            finally
            {
                gate.Release();
                if (!ct.IsCancellationRequested)
                {
                    BatchProgress.Value++;
                    UpdateBatchStatus();
                }
            }
        }).ToList();
        await Task.WhenAll(work);
        if (!ct.IsCancellationRequested) BatchProgress.Visibility = Visibility.Collapsed;
    }

    void SelectBatchEntry(BatchEntry entry)
    {
        _batchSelecting = true;
        BatchList.SelectedItems.Add(entry);
        _batchSelecting = false;
    }

    void UpdateBatchStatus()
    {
        var done = _batch.Count(e => !e.IsLoading);
        var failed = _batch.Count(e => e.Failed);
        BatchStatus.Text = failed > 0
            ? L.Format("Batch_StatusFailed", done, _batch.Count, failed)
            : L.Format("Batch_Status", done, _batch.Count);
        UpdateDownloadButton();
    }

    /// <summary>Ticked rows that parsed successfully (the only ones that can be downloaded).</summary>
    List<BatchEntry> SelectedBatchEntries() =>
        BatchList.SelectedItems.Cast<BatchEntry>().Where(e => e.Info is not null).ToList();

    void BatchList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_batchSelecting) _batchSelectionTouched = true;
        UpdateDownloadButton();
    }

    void BatchSelectAll_Click(object sender, RoutedEventArgs e)
    {
        _batchSelectionTouched = true;
        BatchList.SelectAll();
    }

    void BatchSelectNone_Click(object sender, RoutedEventArgs e)
    {
        _batchSelectionTouched = true;
        BatchList.DeselectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(0, (uint)_batch.Count));
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
        else if (platform == Platform.YouTube && ToolManager.DenoPath is null)
        {
            ShowMessage(InfoBarSeverity.Informational, L.Get("Home_NoDeno"), L.Get("Home_NoDenoTitle"));
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
            var selected = SelectedBatchEntries();
            BatchSelectionText.Text = L.Format("Playlist_Selected", BatchList.SelectedItems.Count, _batch.Count);
            DownloadButtonText.Text = L.Format("Download_Selected", selected.Count);
            DownloadButton.IsEnabled = selected.Count > 0;
        }
        else
        {
            DownloadButtonText.Text = L.Get("Download_Start");
            DownloadButton.IsEnabled = _video is not null;
        }
    }

    async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await Pickers.PickFolderAsync();
        if (folder is null) return;
        AppSettings.Current.DownloadFolder = folder;
        AppSettings.Save();
        UpdateFolderDescription();
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        // Guard against queueing a huge playlist / multi-part video with one click.
        var count = _playlist is not null ? EntriesList.SelectedItems.Count : SelectedBatchEntries().Sum(b => b.TaskCount);
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

        // The task's own copy of the options (template + any per-task changes).
        var options = OptionsEditor.Options.Clone();
        var folder = AppSettings.Current.DownloadFolder;

        string added;
        if (_playlist is { } playlist)
        {
            // Each playlist goes into its own subfolder.
            var sub = Path.Combine(folder, SafeFolderName(playlist.Title));
            var selected = EntriesList.SelectedItems.Cast<PlaylistEntry>().OrderBy(x => x.Index).ToList();
            foreach (var entry in selected)
                App.Downloads.Enqueue(options.ToRequest(entry.Url, sub), entry.Title, entry.ThumbnailUrl, entry.Index);
            added = L.Format("Home_AddedMany", selected.Count);
        }
        else if (_batch.Count > 0)
        {
            var total = 0;
            foreach (var entry in SelectedBatchEntries())
            {
                if (entry.Info is PlaylistInfo list)
                {
                    // A playlist among the links: all of its entries, in their own subfolder.
                    var sub = Path.Combine(folder, SafeFolderName(list.Title));
                    foreach (var item in list.Entries)
                        App.Downloads.Enqueue(options.ToRequest(item.Url, sub), item.Title, item.ThumbnailUrl, item.Index);
                    total += list.Entries.Count;
                }
                else if (entry.Info is { } info)
                {
                    App.Downloads.Enqueue(options.ToRequest(info.Url, folder), info.Title, info.ThumbnailUrl, thumbnail: entry.Thumbnail);
                    total++;
                }
            }
            added = L.Format("Home_AddedMany", total);
        }
        else if (_video is { } video)
        {
            App.Downloads.Enqueue(options.ToRequest(video.Url, folder), video.Title, video.ThumbnailUrl, thumbnail: _thumb);
            added = L.Format("Home_AddedOne", video.Title);
        }
        else
        {
            return;
        }

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

    /// <summary>Missing components are installed from Settings &gt; About &amp; updates.</summary>
    void GoToSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("about");
}
