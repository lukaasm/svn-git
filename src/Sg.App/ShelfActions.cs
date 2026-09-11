using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// Making a shelf, from wherever the reader is standing: the changes of a checkout, the changes of a
/// worktree, or the files a push refuses to write over. All three ask the same question and report the
/// same way, so shelving is one act with one shape however it is reached.
/// </summary>
public static class ShelfActions
{
    /// <summary>
    /// Asks for a name, takes the named changes out of the working copy, and says what it did. paths null
    /// takes everything that is changed there. Null back means the reader cancelled, or it failed and the
    /// strip has already said why.
    /// </summary>
    public static async Task<ShelfSaveResult?> SaveAsync(object owner, StatusStrip pane, string folder,
        IReadOnlyList<string>? paths, string what, string suggested)
    {
        var name = await AskName(owner, what, suggested);
        if (name == null) return null;
        var root = Session.Require();
        var list = paths?.ToList();
        var result = await Runner.Run(pane, "shelve", () => Shelf.Save(root, folder, list, name));
        if (result == null) return null;
        pane.Append($"shelved {result.Shelf.Count} file(s) as {result.Shelf.Id}. Put them back from Shelved changes.");
        if (result.LeftBehind.Count > 0)
            pane.Append("left where they were, these carry an svn property change a shelf cannot hold: "
                        + string.Join(", ", result.LeftBehind));
        return result;
    }

    /// <summary>
    /// The one question a shelf asks. A name, because a shelf is found again by reading a list of them,
    /// and a list of "shelf, shelf, shelf" is no list at all. Empty is allowed; it becomes "shelf".
    /// </summary>
    static async Task<string?> AskName(object owner, string what, string suggested)
    {
        var box = new TextBox
        {
            Header = "Name",
            Text = suggested,
            PlaceholderText = "what this change is",
            SelectionStart = suggested.Length,
        };
        var panel = new StackPanel { Spacing = 10, Width = 420 };
        panel.Children.Add(new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock
        {
            Text = "They leave the working copy and wait in the store. Put them back from Shelved changes, "
                   + "on the card they came from. A file that changed meanwhile gets the change merged into it.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Dialogs.RootOf(owner),
            Title = "Shelve",
            Content = panel,
            PrimaryButtonText = "Shelve",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var confirmed = false;
        Shortcuts.Add(panel, VirtualKey.Enter, VirtualKeyModifiers.Control, () => { confirmed = true; dialog.Hide(); });
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        var answer = await dialog.ShowAsync();
        if (answer != ContentDialogResult.Primary && !confirmed) return null;
        return box.Text.Trim();
    }

    /// <summary>
    /// How much is on the shelf, written on the button that opens it and on the one the empty state
    /// carries. Both changes pages ask the same question of the same two buttons, and had a copy each
    /// until they started drifting apart. stillWanted drops an answer the page has already moved past.
    /// </summary>
    public static async Task ShowCountAsync(IconButton onPage, IconButton onEmptyState,
        string? checkout, string? branch, Func<bool> stillWanted)
    {
        var root = Session.Root;
        if (root == null || (checkout == null && branch == null)) return;
        var count = await Task.Run(() => Shelf.For(root, checkout, branch).Count);
        if (!stillWanted()) return;
        var text = count == 0 ? "Shelved changes" : $"Shelved changes ({count})";
        onPage.Text = text;
        onEmptyState.Text = text;
        // A clean working copy with something on the shelf is the one place the shelf is the whole story.
        onEmptyState.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A name to start from: one file gives its own, several give the count.</summary>
    public static string Suggest(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? Path.GetFileName(paths[0]) : "";
}
