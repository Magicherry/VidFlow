using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VidFlow.Localization;
using VidFlow.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace VidFlow.Pages;

public sealed partial class SubtitlesPage : Page
{
    static LocalSubtitleJob Job => App.Subtitles;
    string? _folder;
    string? _subtitleFile;
    LocalSubtitleInfo? _shownInfo;
    string? _thumbnailSource;
    CancellationTokenSource? _thumbnailCts;
    bool _loading = true;
    const string ChooseFolder = "\0choose";
    bool _choosingFolder;

    public SubtitlesPage()
    {
        InitializeComponent();
        foreach (var action in new[] { LocalSubtitleOperation.Generate, LocalSubtitleOperation.Embed,
                     LocalSubtitleOperation.Extract, LocalSubtitleOperation.Remove })
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(new FontIcon { Glyph = GlyphFor(action), FontSize = 14 });
            label.Children.Add(new TextBlock { Text = L.Get($"LocalSub_Action_{action}") });
            var item = new ComboBoxItem { Content = label, Tag = action };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, L.Get($"LocalSub_Action_{action}"));
            ActionCombo.Items.Add(item);
        }
        ActionCombo.SelectedIndex = 0;
        foreach (var language in SpeechRecognizer.Languages)
            LanguageCombo.Items.Add(new ComboBoxItem { Content = L.Get($"Asr_Lang_{language}"), Tag = language });
        LanguageCombo.SelectedIndex = 0;
        foreach (var format in SubtitleFiles.Formats)
            FormatCombo.Items.Add(new ComboBoxItem { Content = SubtitleFiles.DisplayName(format), Tag = format });
        FormatCombo.SelectedIndex = 0;
        FillFolders();
        _loading = false;
        Job.Changed += Refresh;
        Refresh();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();
    LocalSubtitleOperation SelectedAction => ActionCombo.SelectedItem is ComboBoxItem { Tag: LocalSubtitleOperation action } ? action : LocalSubtitleOperation.Generate;
    LocalSubtitleOperation Operation => LocalSubtitles.ResolveOperation(SelectedAction, EmbedToggle.IsOn);
    void Action_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) Refresh(); }
    void Track_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) Refresh(); }
    void Embed_Toggled(object sender, RoutedEventArgs e) { if (!_loading) Refresh(); }

    static string GlyphFor(LocalSubtitleOperation action) => action switch
    {
        LocalSubtitleOperation.Generate => "\uE720",
        LocalSubtitleOperation.GenerateAndEmbed or LocalSubtitleOperation.Embed => "\uE7F0",
        LocalSubtitleOperation.Extract => "\uE896",
        _ => "\uE894",
    };

    void Refresh()
    {
        var info = Job.Info;
        var empty = Job.Source is null;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        OptionsPanel.Visibility = StartBar.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(_shownInfo, info))
        {
            _loading = true;
            TrackCombo.Items.Clear();
            foreach (var track in info?.Tracks ?? [])
                TrackCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{track.Index} · {SubtitleFiles.LanguageName(track.Language)} · {track.Codec}" + (track.Title is { Length: > 0 } ? $" · {track.Title}" : ""),
                    Tag = track.Index, IsEnabled = track.CanExport,
                });
            TrackCombo.SelectedItem = TrackCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.IsEnabled);
            _shownInfo = info;
            _loading = false;
        }
        var speech = LocalSubtitles.NeedsSpeech(Operation);
        ActionCard.IsEnabled = LanguageCard.IsEnabled = TrackCard.IsEnabled = FormatCard.IsEnabled = FolderCard.IsEnabled
            = EmbedCard.IsEnabled = SubtitleFileCard.IsEnabled = SourceCard.IsEnabled = ChooseVideoButton.IsEnabled = !Job.Busy;
        SourceName.Text = Job.Source is { } source ? Path.GetFileName(source) : L.Get("LocalSub_Video");
        SourcePath.Text = Job.Source ?? L.Get("LocalSub_DropNote");
        ToolTipService.SetToolTip(SourceCard, Job.Source);
        if (_thumbnailSource != Job.Source)
        {
            _thumbnailSource = Job.Source;
            _thumbnailCts?.Cancel();
            SourceThumbnail.Source = null;
            SourcePlaceholder.Visibility = Visibility.Visible;
            if (Job.Source is { } video) _ = LoadThumbnailAsync(video);
        }
        ActionIcon.Glyph = GlyphFor(Operation);
        ActionCard.Description = L.Get($"LocalSub_Description_{Operation}");
        EmbedCard.Visibility = SelectedAction == LocalSubtitleOperation.Generate ? Visibility.Visible : Visibility.Collapsed;
        SubtitleFileCard.Visibility = Operation == LocalSubtitleOperation.Embed ? Visibility.Visible : Visibility.Collapsed;
        SubtitleFileCard.Description = _subtitleFile ?? L.Get("LocalSub_ChooseSubtitleNote");
        LanguageCard.Visibility = speech ? Visibility.Visible : Visibility.Collapsed;
        TrackCard.Visibility = Operation == LocalSubtitleOperation.Extract ? Visibility.Visible : Visibility.Collapsed;
        FormatCard.Visibility = Operation is LocalSubtitleOperation.Generate or LocalSubtitleOperation.Extract ? Visibility.Visible : Visibility.Collapsed;
        FolderCard.Description = _folder ?? L.Get("LocalSub_OutputNote");
        var ffmpeg = ToolManager.FfmpegPath;
        var toolsReady = ffmpeg is not null && File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"));
        SpeechBar.IsOpen = speech && !SpeechRecognizer.CanRecognize;
        SpeechBar.Visibility = SpeechBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        ToolsBar.IsOpen = !toolsReady;
        ToolsBar.Visibility = ToolsBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        DetectionBar.IsOpen = info is not null;
        DetectionBar.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        DetectionBar.Title = info is null ? "" : L.Format("LocalSub_Detected", info.Tracks.Count);
        DetectionBar.Message = info is null ? "" : !info.HasAudio ? L.Get("Error_ConvertNoAudio") : L.Get("LocalSub_DetectionNote");
        var trackReady = Operation != LocalSubtitleOperation.Extract || TrackCombo.SelectedItem is ComboBoxItem { IsEnabled: true };
        var removalReady = Operation != LocalSubtitleOperation.Remove || info?.Tracks.Count > 0;
        var subtitleReady = Operation != LocalSubtitleOperation.Embed || _subtitleFile is not null && File.Exists(_subtitleFile);
        StartButton.IsEnabled = !Job.Busy && info is not null && toolsReady && trackReady && removalReady
            && subtitleReady && (!speech || SpeechRecognizer.CanRecognize && info.HasAudio);
        CancelButton.Visibility = Job.Busy ? Visibility.Visible : Visibility.Collapsed;
        JobProgress.Visibility = Job.Busy ? Visibility.Visible : Visibility.Collapsed;
        JobProgress.IsIndeterminate = Job.Progress is null;
        JobProgress.Value = Job.Progress ?? 0;
        StatusText.Text = Job.Status;
        StatusCard.Visibility = Job.Busy || Job.Failed ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Glyph = Job.Failed ? "\uE7BA" : "\uE895";
        StatusCard.Header = L.Get(Job.Failed ? "LocalSub_Failed" : "LocalSub_Progress");
        SummaryText.Text = Job.Busy ? Job.Status : info is null ? ""
            : L.Format("LocalSub_Detected", info.Tracks.Count);
        ToolTipService.SetToolTip(SummaryText, SummaryText.Text);
        ResultBar.IsOpen = Job.OutputPath is not null;
        ResultBar.Visibility = ResultBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        ResultBar.Title = L.Get("LocalSub_Saved");
        ResultBar.Message = Job.OutputPath ?? "";
        CopyErrorButton.Visibility = Job.Failed ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task LoadThumbnailAsync(string source)
    {
        using var cts = new CancellationTokenSource();
        _thumbnailCts = cts;
        try
        {
            var thumbnail = await ImageLoader.LoadVideoAsync(source, cts.Token);
            if (cts.IsCancellationRequested || Job.Source != source) return;
            SourceThumbnail.Source = thumbnail;
            SourcePlaceholder.Visibility = thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { if (ReferenceEquals(_thumbnailCts, cts)) _thumbnailCts = null; }
    }

    async void ChooseVideo_Click(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.PickFileAsync(".mp4", ".mkv", ".mov", ".webm", ".avi", ".flv", ".m4v", ".ts", ".wmv", ".3gp");
        if (path is not null) await Job.InspectAsync(path);
    }

    async void ChooseSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFileAsync(".srt", ".ass", ".ssa", ".vtt", ".lrc") is { } path) _subtitleFile = path;
        Refresh();
    }

    void FillFolders()
    {
        _choosingFolder = true;
        FolderCombo.Items.Clear();
        FolderCombo.Items.Add(new ComboBoxItem { Content = L.Get("Convert_SameFolder"), Tag = "" });
        if (_folder is not null) FolderCombo.Items.Add(new ComboBoxItem { Content = _folder, Tag = _folder });
        FolderCombo.Items.Add(new ComboBoxItem { Content = L.Get("Convert_ChooseFolder"), Tag = ChooseFolder });
        FolderCombo.SelectedIndex = _folder is null ? 0 : 1;
        _choosingFolder = false;
    }

    async void FolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _choosingFolder || FolderCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (tag == ChooseFolder)
        {
            _choosingFolder = true;
            try { if (await Pickers.PickFolderAsync() is { } folder) _folder = folder; }
            finally { _choosingFolder = false; }
        }
        else _folder = tag.Length > 0 ? tag : null;
        FillFolders();
        Refresh();
    }
    void SpeechSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("settings/addons");
    void ToolsSettings_Click(object sender, RoutedEventArgs e) => App.MainWindow.NavigateTo("about");
    void Cancel_Click(object sender, RoutedEventArgs e) => Job.Cancel();

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Job.Source is not { } source || !StartButton.IsEnabled) return;
        await Job.StartAsync(new(source, Operation, (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto",
            (FormatCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "srt",
            (TrackCombo.SelectedItem as ComboBoxItem)?.Tag as int? ?? -1, _folder, _subtitleFile));
    }

    void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        e.AcceptedOperation = Job.Busy ? DataPackageOperation.None : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = L.Get("LocalSub_DropNote");
    }

    async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        if (Job.Busy) return;
        var deferral = e.GetDeferral();
        try
        {
            var files = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().ToList();
            if (files.Count == 1 && MediaConverter.KindOf(files[0].Path) == MediaKind.Video)
                await Job.InspectAsync(files[0].Path);
            else
                await new ContentDialog { XamlRoot = XamlRoot, Title = L.Get("Nav_Subtitles"), Content = L.Get("LocalSub_OneVideo"),
                    CloseButtonText = L.Get("Common_Close"), Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"] }.ShowAsync();
        }
        finally { deferral.Complete(); }
    }

    void ShowResult_Click(object sender, RoutedEventArgs e)
    {
        if (Job.OutputPath is { } path && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
    void CopyError_Click(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText($"{Job.Source}\n{Job.Status}\n{Job.ErrorDetail}");
        Clipboard.SetContent(data);
    }
}
