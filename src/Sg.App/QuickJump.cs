using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Sg.App;

/// <summary>
/// Ctrl+K: one box that reaches any checkout, any worktree and its pages, and any page of the app,
/// by typing part of its name. The pane reaches a checkout in one press and a worktree's page in
/// three; this reaches all of them without the pointer, which is what a keyboard shortcut is for.
/// The list is what the window knows right now, handed in when the box opens, so nothing here
/// reads the root.
/// </summary>
public static class QuickJump
{
    /// <summary>One place to go. Words is what the filter matches against, on top of the title.</summary>
    public sealed record Entry(string Title, string Detail, string Glyph, Action Run, string Words = "");

    static bool _open;

    public static async Task ShowAsync(object owner, IReadOnlyList<Entry> entries)
    {
        if (_open) return;
        _open = true;
        try { await RunAsync(owner, entries); }
        finally { _open = false; }
    }

    static async Task RunAsync(object owner, IReadOnlyList<Entry> entries)
    {
        var box = new TextBox { PlaceholderText = "A checkout, a branch, a page", Width = 560 };
        var list = new ListView { MaxHeight = 380, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single };
        var count = new TextBlock { Style = Res<Style>("CaptionTextBlockStyle"), Foreground = Res<Brush>("TextFillColorTertiaryBrush") };
        var panel = new StackPanel { Spacing = 8, Width = 560 };
        panel.Children.Add(box);
        panel.Children.Add(list);
        panel.Children.Add(count);

        var dialog = new ContentDialog
        {
            XamlRoot = Dialogs.RootOf(owner),
            Title = "Go to",
            Content = panel,
            PrimaryButtonText = "Go",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = Res<Style>("AccentButtonStyle"),
        };
        dialog.Resources["ContentDialogMaxWidth"] = 700.0;

        var shown = new List<Entry>();
        void Fill()
        {
            var words = box.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            shown = entries.Where(e => words.All(w =>
                    e.Title.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || e.Detail.Contains(w, StringComparison.OrdinalIgnoreCase)
                    || e.Words.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            list.Items.Clear();
            foreach (var e in shown) list.Items.Add(Row(e));
            if (shown.Count > 0) list.SelectedIndex = 0;
            count.Text = shown.Count == entries.Count ? $"{entries.Count} places" : $"showing {shown.Count} of {entries.Count}";
            dialog.IsPrimaryButtonEnabled = shown.Count > 0;
        }

        Entry? picked = null;
        void Pick(int index)
        {
            if (index < 0 || index >= shown.Count) return;
            picked = shown[index];
            dialog.Hide();
        }

        box.TextChanged += (_, _) => Fill();
        // Down from the box walks the list; Enter anywhere goes to the line that is lit.
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Down && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Up && list.SelectedIndex > 0)
            {
                list.SelectedIndex--;
                e.Handled = true;
            }
        };
        list.ItemClick += (_, e) => Pick(list.Items.IndexOf(e.ClickedItem));
        list.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Pick(list.SelectedIndex); e.Handled = true; } };
        dialog.PrimaryButtonClick += (_, _) => picked = list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count ? shown[list.SelectedIndex] : null;
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        Fill();
        await dialog.ShowAsync();
        picked?.Run();
    }

    /// <summary>A line of the list: the glyph, the name in the strong weight, and where it is in the quiet one.</summary>
    static UIElement Row(Entry e)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 4, 4, 4) };
        // What a screen reader announces for the line; a panel of its own has no name to read out.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, e.Title + ", " + e.Detail);
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = e.Glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = e.Title, Style = Res<Style>("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock
        {
            Text = e.Detail, Style = Res<Style>("CaptionTextBlockStyle"), Foreground = Res<Brush>("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(detail, 2);
        row.Children.Add(icon);
        row.Children.Add(title);
        row.Children.Add(detail);
        return row;
    }

    static T Res<T>(string key) => (T)Application.Current.Resources[key];
}
