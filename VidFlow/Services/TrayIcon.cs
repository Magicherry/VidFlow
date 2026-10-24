using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>Notification-area icon: lets downloads continue after the window is closed (minimize to tray).</summary>
public static class TrayIcon
{
    static TaskbarIcon? _icon;

    public static void Initialize()
    {
        try
        {
            var menu = new MenuFlyout();
            menu.Items.Add(Item(L.Get("Tray_Show"), () => App.MainWindow.BringToFront()));
            menu.Items.Add(Item(L.Get("Tray_PasteLink"), () => { App.MainWindow.BringToFront(); App.MainWindow.PasteFromClipboard(); }));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(L.Get("Downloads_PauseAll"), () => App.Downloads.PauseAll()));
            menu.Items.Add(Item(L.Get("Downloads_ResumeAll"), () => App.Downloads.ResumeAll()));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item(L.Get("Tray_Exit"), App.Quit));

            _icon = new TaskbarIcon
            {
                ToolTipText = "VidFlow",
                IconSource = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"))),
                ContextMenuMode = ContextMenuMode.PopupMenu,
                ContextFlyout = menu,
                NoLeftClickDelay = true,
                LeftClickCommand = new Command(() => App.MainWindow.BringToFront()),
            };
            // Efficiency mode would lower our priority while hidden; downloads should keep full speed.
            _icon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon unavailable: {ex.Message}");
            _icon = null;
        }
    }

    public static bool IsAvailable => _icon is not null;

    public static void SetToolTip(string text)
    {
        if (_icon is not null) _icon.ToolTipText = text;
    }

    public static void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    static MenuFlyoutItem Item(string text, Action action) => new() { Text = text, Command = new Command(action) };

    sealed class Command(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
