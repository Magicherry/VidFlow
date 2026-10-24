using System.Runtime.InteropServices;

namespace VidFlow.Services;

/// <summary>Shows overall download progress on the taskbar button (ITaskbarList3).</summary>
public static class TaskbarProgress
{
    public enum State { None = 0, Indeterminate = 0x1, Normal = 0x2, Error = 0x4, Paused = 0x8 }

    static ITaskbarList3? _taskbar;
    static bool _failed;

    public static void Set(IntPtr hwnd, State state, double percent = 0)
    {
        if (_failed) return;
        try
        {
            if (_taskbar is null)
            {
                _taskbar = (ITaskbarList3)new TaskbarInstance();
                _taskbar.HrInit();
            }
            _taskbar.SetProgressState(hwnd, (int)state);
            if (state is State.Normal or State.Paused or State.Error)
                _taskbar.SetProgressValue(hwnd, (ulong)Math.Clamp(percent * 10, 0, 1000), 1000);
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Warn($"Taskbar progress unavailable: {ex.Message}");
        }
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    class TaskbarInstance;

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
    }
}
