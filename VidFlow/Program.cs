using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace VidFlow;

/// <summary>
/// Entry point. Enforces a single instance: a second launch (or a notification click) is redirected to the
/// running instance, which brings its window to the front.
/// </summary>
public static partial class Program
{
    const string InstanceKey = "VidFlow.Main";

    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Run by the uninstaller: remove the notification registration this unpackaged app created.
        if (args.Contains("--uninstall-cleanup"))
        {
            try { Microsoft.Windows.AppNotifications.AppNotificationManager.Default.UnregisterAll(); } catch { }
            return 0;
        }

        if (RedirectToRunningInstance()) return 0;

        Application.Start(init =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Returns true when another instance owns the key and has been handed this activation.</summary>
    static bool RedirectToRunningInstance()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }

        // Let the running instance take the foreground, then hand over and exit.
        AllowSetForegroundWindow(main.ProcessId);
        Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait();
        return true;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
