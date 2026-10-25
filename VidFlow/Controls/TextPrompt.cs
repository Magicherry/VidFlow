using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;

namespace VidFlow.Controls;

/// <summary>A small "enter a name" dialog. Returns the trimmed text, or null when cancelled or empty.</summary>
public static class TextPrompt
{
    public static async Task<string?> ShowAsync(XamlRoot root, string title, string label, string initial)
    {
        var box = new TextBox { Header = label, Text = initial, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = L.Get("Common_Save"),
            CloseButtonText = L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
