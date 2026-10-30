using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Controls;

/// <summary>Creates or edits a user template. A built-in preset opens read-only, with "Duplicate" as the way to change it.</summary>
public sealed partial class TemplateEditorDialog : ContentDialog
{
    readonly DownloadTemplate _working;

    /// <param name="template">The template to edit, or null for a new one.</param>
    public TemplateEditorDialog(DownloadTemplate? template)
    {
        InitializeComponent();
        if (template is null)
        {
            _working = TemplateStore.Default.Clone();
            _working.Id = Guid.NewGuid();
            _working.NameKey = null;
            _working.Name = TemplateStore.UniqueName(L.Get("Template_NewName"));
            Title = L.Get("Template_NewTitle");
        }
        else
        {
            _working = template.Clone();
            Title = L.Get("Template_EditTitle");
        }
        NameBox.Text = _working.DisplayName;
        Editor.Load(_working);
        PrimaryButtonText = L.Get(_working.IsBuiltIn ? "Template_DuplicateToEdit" : "Common_Save");
        CloseButtonText = L.Get(_working.IsBuiltIn ? "Common_Close" : "Common_Cancel");
        if (_working.IsBuiltIn)
        {
            Title = _working.DisplayName;
            NameBox.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            Editor.MakeReadOnly();
            DefaultButton = ContentDialogButton.Close;
        }
    }

    /// <summary>The saved template, or null if the dialog was cancelled.</summary>
    public DownloadTemplate? Result { get; private set; }

    void NameBox_TextChanged(object sender, TextChangedEventArgs e) => IsPrimaryButtonEnabled = NameBox.Text.Trim().Length > 0;

    void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_working.IsBuiltIn) return; // The caller duplicates it.
        var options = Editor.Options;
        options.Name = NameBox.Text.Trim();
        Result = TemplateStore.Save(options);
    }
}
