using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;
    public static DownloadManager Downloads { get; private set; } = null!;
    public static DispatcherQueue UiQueue { get; private set; } = null!;

    public App()
    {
        // Settings and language must be in place before any XAML (and its x:Bind strings) is created.
        AppSettings.Load();
        L.ApplyLanguage(AppSettings.Current.Language);

        InitializeComponent();
        UnhandledException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Info($"VidFlow {typeof(App).Assembly.GetName().Version} starting");
        UiQueue = DispatcherQueue.GetForCurrentThread();

        NotificationService.Initialize();
        Downloads = new DownloadManager(UiQueue);
        MainWindow = new MainWindow();
        TrayIcon.Initialize();
        ClipboardWatcher.Initialize();

        MainWindow.Activate();
        Downloads.LoadPersisted();

        // Launched by clicking a notification while the app was not running.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs n)
            NotificationService.HandleActivation(n);

        _ = Task.Run(() => ToolManager.AutoUpdateYtDlpAsync());
    }

    /// <summary>Another launch (second instance or notification click) was redirected here. Called off the UI thread.</summary>
    public static void OnRedirectedActivation(AppActivationArguments e)
    {
        UiQueue?.TryEnqueue(() =>
        {
            MainWindow?.BringToFront();
            if (e.Kind == ExtendedActivationKind.AppNotification && e.Data is AppNotificationActivatedEventArgs n)
                NotificationService.HandleActivation(n);
        });
    }

    /// <summary>Real exit (tray "Exit" or close with minimize-to-tray off). Unfinished downloads resume next launch.</summary>
    public static void Quit()
    {
        Downloads.Shutdown();
        TrayIcon.Dispose();
        MainWindow.CloseForReal();
        Current.Exit();
    }

    /// <summary>Restarts the app (used after changing the language).</summary>
    public static void Restart()
    {
        Downloads.Shutdown();
        TrayIcon.Dispose();
        // Release the single-instance key first, or the new process would redirect back to this one.
        AppInstance.GetCurrent().UnregisterKey();
        if (Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        MainWindow.CloseForReal();
        Current.Exit();
    }
}
