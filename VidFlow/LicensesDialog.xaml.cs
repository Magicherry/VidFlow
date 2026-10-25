using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;

namespace VidFlow;

/// <summary>Third-party components and their licenses (names and links; the full texts live at the links).</summary>
public sealed partial class LicensesDialog : ContentDialog
{
    static readonly (string Name, string License, string Url, string? NoteKey)[] Components =
    [
        ("yt-dlp", "Unlicense", "https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE", "Licenses_YtDlpNote"),
        ("FFmpeg", "GPL-3.0 (yt-dlp/FFmpeg-Builds)", "https://github.com/yt-dlp/FFmpeg-Builds", "Licenses_FfmpegNote"),
        ("Deno", "MIT", "https://github.com/denoland/deno/blob/main/LICENSE.md", "Licenses_YtDlpNote"),
        ("aria2", "GPL-2.0", "https://github.com/aria2/aria2/blob/master/COPYING", "Licenses_YtDlpNote"),
        (".NET", "MIT", "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT", null),
        ("Windows App SDK / WinUI 3", "MIT", "https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE", null),
        ("Windows Community Toolkit", "MIT", "https://github.com/CommunityToolkit/Windows/blob/main/License.md", null),
        ("H.NotifyIcon", "MIT", "https://github.com/HavenDV/H.NotifyIcon/blob/master/LICENSE.md", null),
        ("Microsoft Edge WebView2", "Microsoft Software License Terms", "https://developer.microsoft.com/microsoft-edge/webview2/", null),
    ];

    public LicensesDialog()
    {
        InitializeComponent();
        foreach (var (name, license, url, note) in Components)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            panel.Children.Add(new HyperlinkButton { Content = license, NavigateUri = new Uri(url), Padding = new Thickness(0) });
            if (note is not null)
                panel.Children.Add(new TextBlock
                {
                    Text = L.Get(note),
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                });
            ListPanel.Children.Add(panel);
        }
    }
}
