using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace VidFlow.Pages;

/// <summary>Converting files the user has (video, audio, image, subtitles) into another format. The list is
/// <see cref="ConvertQueue"/>'s, so it carries on while other pages are shown.</summary>
public sealed partial class ConvertPage : Page
{
    static ConvertQueue Queue => App.Converts;

    const string SameFolder = "";
    const string ChooseFolder = "\0choose";

    public ConvertPage()
    {
        InitializeComponent();
        FileList.ItemsSource = Queue.Items;
        Queue.Items.CollectionChanged += Items_CollectionChanged;
        foreach (var item in Queue.Items) item.PropertyChanged += Item_PropertyChanged;
        FillFolders();
        Refresh();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // ffmpeg may have been installed meanwhile.
        FfmpegBar.IsOpen = ToolManager.FfmpegPath is null;
        Refresh();
    }

    void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (ConvertItem item in e.OldItems ?? Array.Empty<ConvertItem>()) item.PropertyChanged -= Item_PropertyChanged;
        foreach (ConvertItem item in e.NewItems ?? Array.Empty<ConvertItem>()) item.PropertyChanged += Item_PropertyChanged;
        Refresh();
    }

    void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConvertItem.State)) Refresh();
    }

    /// <summary>Empty state or list, the counts, and whether there is anything to start.</summary>
    void Refresh()
    {
        var items = Queue.Items;
        var empty = items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        FileList.Visibility = StartBar.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        var waiting = items.Count(i => i.IsWaiting);
        var done = items.Count(i => i.IsDone);
        SummaryText.Text = L.Format("Convert_Summary", items.Count, waiting, done);
        StartButton.IsEnabled = waiting > 0 && !Queue.Running && ToolManager.FfmpegPath is not null;
    }

    // ---- Output folder --------------------------------------------------------------------------------------------

    void FillFolders()
    {
        FolderCombo.Items.Clear();
        FolderCombo.Items.Add(new ComboBoxItem { Content = L.Get("Convert_SameFolder"), Tag = SameFolder });
        if (AppSettings.Current.ConvertFolder is { Length: > 0 } folder)
            FolderCombo.Items.Add(new ComboBoxItem { Content = folder, Tag = folder });
        FolderCombo.Items.Add(new ComboBoxItem { Content = L.Get("Convert_ChooseFolder"), Tag = ChooseFolder });
        _choosing = true;
        FolderCombo.SelectedIndex = AppSettings.Current.ConvertFolder.Length > 0 ? 1 : 0;
        _choosing = false;
        FolderCard.Description = AppSettings.Current.ConvertFolder.Length > 0 ? AppSettings.Current.ConvertFolder : L.Get("Convert_SameFolderDescription");
    }

    bool _choosing;

    async void FolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_choosing || FolderCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (tag == ChooseFolder)
        {
            _choosing = true;
            var picked = await Pickers.PickFolderAsync();
            _choosing = false;
            if (picked is not null) AppSettings.Current.ConvertFolder = picked;
        }
        else AppSettings.Current.ConvertFolder = tag;
        AppSettings.Save();
        FillFolders();
    }

    // ---- Adding -----------------------------------------------------------------------------------------------------

    async void AddFiles_Click(object sender, RoutedEventArgs e) => Add(await Pickers.PickFilesAsync());

    void Add(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var skipped = Queue.Add(paths);
        if (skipped > 0)
            _ = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Format("Convert_SkippedTitle", skipped),
                Content = L.Get("Convert_SkippedBody"),
                CloseButtonText = L.Get("Common_Close"),
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            }.ShowAsync();
    }

    void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = L.Get("Convert_DropCaption");
        // The window would take it for a link otherwise.
        e.Handled = true;
    }

    async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        var paths = new List<string>();
        foreach (var item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFile file) paths.Add(file.Path);
            // A dropped folder brings the files directly in it.
            else if (item is StorageFolder folder && Directory.Exists(folder.Path)) paths.AddRange(Directory.GetFiles(folder.Path));
        }
        Add(paths);
    }

    // ---- Rows -------------------------------------------------------------------------------------------------------

    static ConvertItem ItemOf(object sender) => (ConvertItem)((FrameworkElement)sender).DataContext;

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        var running = Queue.StartAsync();
        Refresh();
        await running;
        Refresh();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Queue.Cancel(ItemOf(sender));

    async void Retry_Click(object sender, RoutedEventArgs e)
    {
        Queue.Retry(ItemOf(sender));
        Refresh();
        // Picked up by the run under way, or a new one.
        if (!Queue.Running) await Queue.StartAsync();
        Refresh();
    }

    void Remove_Click(object sender, RoutedEventArgs e) => Queue.Remove(ItemOf(sender));

    void ClearFinished_Click(object sender, RoutedEventArgs e) => Queue.ClearFinished();

    void CopyError_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        var package = new DataPackage();
        package.SetText($"{item.Source} → {item.Target}\n\n{item.StatusText}\n\n{item.ErrorDetail}");
        Clipboard.SetContent(package);
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender).OutputPath is { } path && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
