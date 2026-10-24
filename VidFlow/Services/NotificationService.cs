using System.Diagnostics;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>Windows toast notifications: download finished / failed, and "video link copied" prompts.</summary>
public static class NotificationService
{
    static bool _registered;

    public static void Initialize()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
                App.UiQueue.TryEnqueue(() => HandleActivation(e));
            AppNotificationManager.Default.Register("VidFlow",
                new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")));
            _registered = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Notifications unavailable: {ex.Message}");
        }
    }

    public static void DownloadCompleted(string title, string? filePath, string folder)
    {
        var b = new AppNotificationBuilder()
            .AddArgument("action", "show")
            .AddText(L.Get("Notify_Completed"))
            .AddText(title);
        if (filePath is not null)
            b.AddButton(new AppNotificationButton(L.Get("Action_Play")).AddArgument("action", "play").AddArgument("path", filePath));
        b.AddButton(new AppNotificationButton(L.Get("Action_OpenFolder")).AddArgument("action", "folder").AddArgument("path", filePath ?? folder));
        Show(b);
    }

    public static void BatchCompleted(int count, string folder) =>
        Show(new AppNotificationBuilder()
            .AddArgument("action", "show")
            .AddText(L.Format("Notify_BatchCompleted", count))
            .AddButton(new AppNotificationButton(L.Get("Action_OpenFolder")).AddArgument("action", "folder").AddArgument("path", folder)));

    public static void DownloadFailed(string title, string reason) =>
        Show(new AppNotificationBuilder()
            .AddArgument("action", "downloads")
            .AddText(L.Get("Notify_Failed"))
            .AddText(title)
            .AddText(reason.Split('\n')[0]));

    public static void LinkDetected(string url) =>
        Show(new AppNotificationBuilder()
            .AddArgument("action", "parse").AddArgument("url", url)
            .AddText(L.Get("Notify_LinkDetected"))
            .AddText(url)
            .AddButton(new AppNotificationButton(L.Get("Action_Download")).AddArgument("action", "parse").AddArgument("url", url)));

    public static void StillRunningInTray() =>
        Show(new AppNotificationBuilder()
            .AddArgument("action", "show")
            .AddText(L.Get("Notify_InTray"))
            .AddText(L.Get("Notify_InTrayBody")));

    static void Show(AppNotificationBuilder builder)
    {
        if (!_registered) return;
        try
        {
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Log.Warn($"Showing notification failed: {ex.Message}");
        }
    }

    /// <summary>Runs on the UI thread.</summary>
    public static void HandleActivation(AppNotificationActivatedEventArgs e)
    {
        var args = e.Arguments;
        args.TryGetValue("action", out var action);
        args.TryGetValue("path", out var path);
        try
        {
            switch (action)
            {
                case "play" when path is not null && File.Exists(path):
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    break;
                case "folder" when path is not null:
                    if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
                    else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    break;
                case "parse" when args.TryGetValue("url", out var url):
                    App.MainWindow.BringToFront();
                    App.MainWindow.OpenLinks(url);
                    break;
                case "downloads":
                    App.MainWindow.BringToFront();
                    App.MainWindow.NavigateTo("downloads");
                    break;
                default:
                    App.MainWindow.BringToFront();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Notification action '{action}' failed: {ex.Message}");
        }
    }
}
