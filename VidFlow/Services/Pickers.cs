using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VidFlow.Services;

/// <summary>Unpackaged WinUI apps must bind pickers to the window handle before showing them.</summary>
public static class Pickers
{
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
