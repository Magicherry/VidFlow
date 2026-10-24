using Windows.ApplicationModel.DataTransfer;

namespace VidFlow.Services;

/// <summary>
/// Optional: when a YouTube / Bilibili link is copied while VidFlow is in the background, offer to download it
/// with a notification. Links copied while VidFlow is in front are left alone (the user is pasting themselves).
/// </summary>
public static class ClipboardWatcher
{
    static string? _lastUrl;

    public static void Initialize()
    {
        try
        {
            Clipboard.ContentChanged += async (_, _) =>
            {
                if (!AppSettings.Current.WatchClipboard || App.MainWindow.IsForeground) return;
                try
                {
                    var content = Clipboard.GetContent();
                    if (!content.Contains(StandardDataFormats.Text)) return;
                    var url = UrlTools.ExtractUrl(await content.GetTextAsync());
                    if (!UrlTools.IsSupported(url) || url == _lastUrl) return;
                    _lastUrl = url;
                    NotificationService.LinkDetected(url!);
                }
                catch (Exception ex)
                {
                    // The clipboard is often briefly locked by the app that just wrote to it.
                    Log.Info($"Clipboard read skipped: {ex.Message}");
                }
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Clipboard watching unavailable: {ex.Message}");
        }
    }
}
