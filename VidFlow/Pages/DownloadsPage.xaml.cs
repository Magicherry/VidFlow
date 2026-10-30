using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;
using Windows.ApplicationModel.DataTransfer;

namespace VidFlow.Pages;

public sealed partial class DownloadsPage : Page
{
    /// <summary>The downloads that pass the filters, kept in step with the full list.</summary>
    readonly ObservableCollection<DownloadItem> _shown = [];
    DownloadFilter _filter = new();

    public DownloadsPage()
    {
        InitializeComponent();
        SettingsPage.AddItems(PlatformCombo, [("", L.Get("Downloads_AllPlatforms")),
            .. PlatformExtensions.Supported.Select(p => (p.ToString(), p.DisplayName()))]);
        PlatformCombo.SelectedIndex = 0;
        StatusBar.SelectedItem = StatusBar.Items[0];

        DownloadList.ItemsSource = _shown;
        foreach (var item in App.Downloads.Items) item.PropertyChanged += Item_PropertyChanged;
        App.Downloads.Items.CollectionChanged += Items_CollectionChanged;
        Refresh();
    }

    void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DownloadItem item in e.OldItems ?? Array.Empty<DownloadItem>()) item.PropertyChanged -= Item_PropertyChanged;
        foreach (DownloadItem item in e.NewItems ?? Array.Empty<DownloadItem>()) item.PropertyChanged += Item_PropertyChanged;
        Refresh();
    }

    /// <summary>A download that finishes or fails moves between status groups (and its title arrives after it is added).</summary>
    void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItem.State) or nameof(DownloadItem.Title)) Refresh();
    }

    void StatusBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<DownloadStatusFilter>(tag, out var status))
            SetFilter(_filter with { Status = status });
    }

    void PlatformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlatformCombo.SelectedItem is ComboBoxItem { Tag: string tag })
            SetFilter(_filter with { Platform = Enum.TryParse<Platform>(tag, out var p) ? p : null });
    }

    void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        SetFilter(_filter with { Query = sender.Text });

    void SetFilter(DownloadFilter filter)
    {
        if (filter == _filter) return;
        _filter = filter;
        Refresh();
    }

    /// <summary>The list, the counts on the status choices, and the empty state, from the current filters.</summary>
    void Refresh()
    {
        if (StatusBar is null) return;
        var items = App.Downloads.Items;
        DownloadFilter.Sync(_shown, items.Where(i => _filter.Matches(i.Record)).ToList());

        // Counts within the chosen platform and search text, so they say what each choice would show.
        var candidates = items.Where(i => _filter.MatchesExceptStatus(i.Record)).ToList();
        foreach (var choice in StatusBar.Items)
        {
            var status = Enum.Parse<DownloadStatusFilter>((string)choice.Tag);
            var count = status == DownloadStatusFilter.All
                ? candidates.Count
                : candidates.Count(i => DownloadFilter.GroupOf(i.State) == status);
            choice.Text = $"{L.Get($"Downloads_Filter{status}")} {count}";
        }

        var nothing = items.Count == 0;
        FilterBar.Visibility = nothing ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadList.Visibility = _shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyIcon.Glyph = nothing ? "\uE896" : "\uE71C";
        EmptyTitle.Text = L.Get(nothing ? "Downloads_EmptyTitle" : "Downloads_NoMatchTitle");
        EmptyBody.Text = L.Get(nothing ? "Downloads_EmptyBody" : "Downloads_NoMatchBody");
    }

    static DownloadItem ItemOf(object sender) => (DownloadItem)((FrameworkElement)sender).DataContext;

    void Prioritize_Click(object sender, RoutedEventArgs e) => App.Downloads.Prioritize(ItemOf(sender));
    void Pause_Click(object sender, RoutedEventArgs e) => App.Downloads.Pause(ItemOf(sender));
    void Resume_Click(object sender, RoutedEventArgs e) => App.Downloads.Resume(ItemOf(sender));
    void Cancel_Click(object sender, RoutedEventArgs e) => App.Downloads.Cancel(ItemOf(sender));
    void Retry_Click(object sender, RoutedEventArgs e) => App.Downloads.Retry(ItemOf(sender));
    void Remove_Click(object sender, RoutedEventArgs e) => App.Downloads.Remove(ItemOf(sender));
    void PauseAll_Click(object sender, RoutedEventArgs e) => App.Downloads.PauseAll();
    void ResumeAll_Click(object sender, RoutedEventArgs e) => App.Downloads.ResumeAll();

    async void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        var count = App.Downloads.FinishedCount;
        if (count == 0) return;
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Format("Downloads_ClearTitle", count),
            Content = L.Get("Downloads_ClearBody"),
            PrimaryButtonText = L.Get("Downloads_ClearConfirm"),
            CloseButtonText = L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary) App.Downloads.ClearFinished();
    }

    void CopyError_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        var package = new DataPackage();
        package.SetText($"{item.Title}\n{item.Request.Url}\n\n{item.StatusText}\n\n{item.ErrorDetail}");
        Clipboard.SetContent(package);
    }

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item.FilePath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            OpenFolder(item.Request.OutputFolder);
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item.FilePath is { } path && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            OpenFolder(item.Request.OutputFolder);
    }

    void OpenDownloadFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.Current.DownloadFolder);

    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }
}
