using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Controls;
using VidFlow.Localization;
using VidFlow.Models;
using VidFlow.Services;

namespace VidFlow.Pages;

/// <summary>Download templates: the user's own (edit / duplicate / rename / delete) and the built-in presets
/// (duplicate only), each of which can be made the default.</summary>
public sealed partial class TemplatesPage : Page
{
    public TemplatesPage()
    {
        InitializeComponent();
        // The page is recreated on each visit: subscribe while shown, unsubscribe when it goes away.
        Loaded += (_, _) =>
        {
            TemplateStore.Changed += BuildLists;
            BuildLists();
        };
        Unloaded += (_, _) => TemplateStore.Changed -= BuildLists;
    }

    void BuildLists()
    {
        CustomList.Children.Clear();
        BuiltInList.Children.Clear();
        var defaultId = TemplateStore.Default.Id;
        foreach (var t in TemplateStore.All)
            (t.IsBuiltIn ? BuiltInList : CustomList).Children.Add(BuildCard(t, t.Id == defaultId));
        EmptyCard.Visibility = CustomList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Summary, "set as default", and a menu with the actions the template allows.</summary>
    SettingsCard BuildCard(DownloadTemplate t, bool isDefault)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (isDefault)
            actions.Children.Add(new TextBlock
            {
                Text = L.Get("Template_IsDefault"),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            });
        else
        {
            var setDefault = new Button { Content = L.Get("Template_SetDefault") };
            setDefault.Click += (_, _) => TemplateStore.SetDefault(t);
            actions.Children.Add(setDefault);
        }

        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        if (!t.IsBuiltIn) menu.Items.Add(MenuItem(L.Get("Template_Edit"), "", async () => await EditTemplateAsync(t)));
        menu.Items.Add(MenuItem(L.Get("Template_Duplicate"), "", async () => await DuplicateTemplateAsync(t)));
        if (!t.IsBuiltIn)
        {
            menu.Items.Add(MenuItem(L.Get("Template_Rename"), "", async () => await RenameTemplateAsync(t)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem(L.Get("Template_Delete"), "", async () => await DeleteTemplateAsync(t)));
        }
        var more = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Width = 40,
            Height = 32,
            Padding = new Thickness(0),
            Flyout = menu,
        };
        ToolTipService.SetToolTip(more, L.Get("Downloads_More"));
        AutomationProperties.SetName(more, $"{t.DisplayName} {L.Get("Downloads_More")}");
        actions.Children.Add(more);

        var card = new SettingsCard
        {
            Header = t.DisplayName,
            Description = DownloadLabels.Summary(t),
            HeaderIcon = new FontIcon { Glyph = isDefault ? "" : t.IsBuiltIn ? "" : "" },
            Content = actions,
        };
        // Custom templates open the editor on click, like a file in a list.
        if (!t.IsBuiltIn)
        {
            card.IsClickEnabled = true;
            card.ActionIcon = new FontIcon { Glyph = "" };
            card.Click += async (_, _) => await EditTemplateAsync(t);
        }
        return card;
    }

    static MenuFlyoutItem MenuItem(string text, string glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += async (_, _) => await action();
        return item;
    }

    async void NewTemplate_Click(object sender, RoutedEventArgs e) => await EditTemplateAsync(null);

    async Task EditTemplateAsync(DownloadTemplate? t)
    {
        await new TemplateEditorDialog(t) { XamlRoot = XamlRoot }.ShowAsync();
        BuildLists();
    }

    async Task DuplicateTemplateAsync(DownloadTemplate t)
    {
        var copy = TemplateStore.Duplicate(t, TemplateStore.UniqueName(L.Format("Template_CopyName", t.DisplayName)));
        await EditTemplateAsync(copy);
    }

    async Task RenameTemplateAsync(DownloadTemplate t)
    {
        var name = await TextPrompt.ShowAsync(XamlRoot, L.Get("Template_Rename"), L.Get("Template_Name"), t.DisplayName);
        if (name is not null) TemplateStore.Rename(t, name);
    }

    async Task DeleteTemplateAsync(DownloadTemplate t)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Format("Template_DeleteTitle", t.DisplayName),
            Content = L.Get("Template_DeleteBody"),
            PrimaryButtonText = L.Get("Template_Delete"),
            CloseButtonText = L.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary) TemplateStore.Delete(t);
    }
}
