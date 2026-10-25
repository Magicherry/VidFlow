using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Models;
using VidFlow.Services;
using Windows.ApplicationModel.DataTransfer;

namespace VidFlow.Pages;

public sealed partial class DownloadsPage : Page
{
    public DownloadsPage()
    {
        InitializeComponent();
        DownloadList.ItemsSource = App.Downloads.Items;
        App.Downloads.Items.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
    }

    void UpdateEmptyState()
    {
        var empty = App.Downloads.Items.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DownloadList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
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
    void ClearFinished_Click(object sender, RoutedEventArgs e) => App.Downloads.ClearFinished();

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
