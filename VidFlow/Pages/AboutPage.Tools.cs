using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Localization;
using VidFlow.Services;

namespace VidFlow.Pages;

/// <summary>The components section: one expander whose header gives the overall state, last check, "check for
/// updates" and "update all", with one row per component inside saying what it is for, its version, and either "up to date" or the action it needs.
/// The checking and installing itself is <see cref="ComponentUpdater"/>'s, so it carries on when the page is left.</summary>
public sealed partial class AboutPage
{
    /// <summary>One component's row on the page.</summary>
    sealed class Row
    {
        public required Component Component;
        public SettingsCard Card = null!;
        public Button Button = null!;
        public ProgressBar Bar = null!;
        public ProgressRing Ring = null!;
        public StackPanel Status = null!;
        public FontIcon StatusIcon = null!;
        public TextBlock StatusText = null!;
    }

    Row[] _rows = [];

    static ComponentUpdater Tools => App.Tools;

    void BuildToolRows()
    {
        ToolsSummaryCard.Items.Clear();
        _rows = Tools.Components.Select(c =>
        {
            var row = new Row { Component = c };
            row.Bar = new ProgressBar { Width = 120, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            row.Ring = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
            row.StatusIcon = new FontIcon { FontSize = 14 };
            row.StatusText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            row.Status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Status.Children.Add(row.StatusIcon);
            row.Status.Children.Add(row.StatusText);
            row.Button = new Button { MinWidth = 96 };
            row.Button.Click += async (_, _) => await Tools.InstallAsync([c]);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            actions.Children.Add(row.Bar);
            actions.Children.Add(row.Ring);
            actions.Children.Add(row.Status);
            actions.Children.Add(row.Button);
            row.Card = new SettingsCard { Header = c.Name, HeaderIcon = new FontIcon { Glyph = c.Glyph }, Content = actions };
            AutomationProperties.SetName(row.Button, c.Name);
            ToolsSummaryCard.Items.Add(row.Card);
            return row;
        }).ToArray();
        ShowTools();
    }

    async void CheckTools_Click(object sender, RoutedEventArgs e) => await Tools.RefreshAsync(refresh: true);

    async void UpdateAllTools_Click(object sender, RoutedEventArgs e) =>
        await Tools.InstallAsync(Tools.Components.Where(c => c.State is ToolState.UpdateAvailable or ToolState.Missing).ToList());

    void ToolErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Tools.LastResult = null;

    /// <summary>Everything in the section, from the updater's current state.</summary>
    void ShowTools()
    {
        foreach (var row in _rows) ShowTool(row);
        ShowSummary();
        CheckToolsButton.IsEnabled = !Tools.Checking && !Tools.Installing;
        if (Tools.LastResult is { } result)
        {
            ToolErrorBar.Severity = result.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ToolErrorBar.Title = L.Get(result.Succeeded ? "Settings_InstallDone" : "Settings_InstallFailed");
            ToolErrorBar.Message = result.Message;
        }
        ToolErrorBar.IsOpen = Tools.LastResult is not null;
    }

    /// <summary>Description (purpose · version) and the right-hand side: a status, a spinner, a progress bar or the
    /// one action needed.</summary>
    void ShowTool(Row row)
    {
        var c = row.Component;
        ToolTipService.SetToolTip(row.Card, c.Path());
        var purpose = L.Get(c.PurposeKey);
        var version = c.Name == "ffmpeg" ? ComponentUpdater.ShortFfmpeg(c.Version) : c.Version;
        row.Card.Description = c.State switch
        {
            ToolState.Missing => L.Get(c.MissingKey),
            ToolState.SystemCopy => L.Format("Tools_SystemCopy", purpose, version ?? "?"),
            _ when version is not null => L.Format("Tools_Installed", purpose, version),
            _ => purpose,
        };
        var busy = c.State is ToolState.Checking && c.Version is not null;
        row.Ring.IsActive = busy;
        row.Ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        row.Bar.Visibility = c.State == ToolState.Installing ? Visibility.Visible : Visibility.Collapsed;
        row.Bar.IsIndeterminate = c.Progress is null;
        row.Bar.Value = c.Progress ?? 0;

        (string Glyph, string Brush, string Text)? status = c.State switch
        {
            ToolState.UpToDate => ("", "SystemFillColorSuccessBrush", L.Get("Settings_UpToDate")),
            ToolState.CheckFailed => ("", "SystemFillColorCautionBrush", L.Get("Tools_CheckFailed")),
            _ => null,
        };
        row.Status.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        if (status is { } s)
        {
            row.StatusIcon.Glyph = s.Glyph;
            row.StatusIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[s.Brush];
            row.StatusText.Text = s.Text;
        }

        string? action = c.State switch
        {
            ToolState.Missing => L.Get("Settings_Install"),
            ToolState.UpdateAvailable => L.Format("Settings_UpdateTo", c.Latest),
            ToolState.SystemCopy => L.Get("Settings_InstallManaged"),
            _ => null,
        };
        row.Button.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        if (action is not null) row.Button.Content = action;
        row.Button.IsEnabled = !Tools.Installing && !Tools.Checking;
        // The things that need doing stand out; installing a separate copy is optional.
        row.Button.Style = (Style)Application.Current.Resources[
            c.State is ToolState.Missing or ToolState.UpdateAvailable ? "AccentButtonStyle" : "DefaultButtonStyle"];
    }

    /// <summary>Overall state in the header, last check time below, and "Update all" when there is something to do.</summary>
    void ShowSummary()
    {
        var components = Tools.Components;
        var installing = components.FirstOrDefault(c => c.State == ToolState.Installing);
        var updates = components.Count(c => c.State == ToolState.UpdateAvailable);
        var missingRequired = components.Where(c => c.State == ToolState.Missing && c.Required).Select(c => c.Name).ToList();
        var missing = components.Count(c => c.State == ToolState.Missing);
        var failed = components.Any(c => c.State == ToolState.CheckFailed);
        (ToolsSummaryCard.Header, ToolsSummaryIcon.Glyph) = (installing, Tools.Checking, missingRequired.Count, updates + missing, failed) switch
        {
            ({ } c, _, _, _, _) => (L.Format("Wizard_Installing", c.Name), ""),
            (_, true, _, _, _) => (L.Get("Tools_Checking"), ""),
            (_, _, > 0, _, _) => (L.Format("Tools_MissingRequired", string.Join(", ", missingRequired)), ""),
            (_, _, _, > 0, _) => (L.Format("Tools_ActionsNeeded", updates + missing), ""),
            (_, _, _, _, true) => (L.Get("Tools_CheckFailedSummary"), ""),
            _ => (L.Get("Tools_AllUpToDate"), ""),
        };
        ToolsSummaryCard.Description = Tools.LastCheck is { } at
            // 24-hour numbers read the same in either UI language (the system's "4:46 PM" wouldn't match Chinese text).
            ? L.Format("Tools_LastChecked", at.ToString(at.Date == DateTime.Today ? "HH:mm" : "yyyy-MM-dd HH:mm"))
            : L.Get("Tools_ManagedNote");
        var idle = !Tools.Checking && installing is null;
        UpdateAllToolsButton.Visibility = idle && updates + missing > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllToolsButton.IsEnabled = idle;
        // Open by itself when a component needs something done (so its button is in view) or is installing.
        if (installing is not null || (idle && updates + missing > 0)) ToolsSummaryCard.IsExpanded = true;
    }

    void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.AutoUpdateYtDlp = AutoUpdateToggle.IsOn;
        AppSettings.Save();
    }
}
