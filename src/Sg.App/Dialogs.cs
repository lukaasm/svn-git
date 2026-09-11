using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

public sealed record NewBranchInput(string Name, CheckoutConfig Checkout, bool Minimal, List<string> Without, SharedMode Shared);
public sealed record ServerCheckoutInput(string Target, CheckoutConfig Near, string? Name);

/// <summary>Small modal dialogs built in code, so every window can use them.</summary>
public static class Dialogs
{
    /// <summary>Where a dialog goes: a window's content, or the page or element that asked for it.</summary>
    public static XamlRoot RootOf(object owner) => owner switch
    {
        Window w => w.Content.XamlRoot,
        UIElement e => e.XamlRoot,
        _ => throw new ArgumentException("a dialog needs a window or an element to sit on", nameof(owner)),
    };

    public static async Task<bool> Confirm(object owner, string title, string text, string okText = "OK")
    {
        var d = new ContentDialog
        {
            XamlRoot = RootOf(owner),
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = okText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>One thing a destructive action takes with it, worn as the badge the card already used for it.</summary>
    public sealed record Loss(ChipSeverity Severity, string Glyph, string Text);

    /// <summary>
    /// A confirmation for something that cannot be undone. It lists what goes in the same badges the
    /// card showed, so the user is not asked to remember what the chips said, and the button stays off
    /// until they tick the acknowledgement. With nothing to lose there is no tick box: asking for one
    /// on a harmless action is how people learn to tick without reading.
    /// </summary>
    public static async Task<bool> ConfirmLoss(object owner, string title, string what,
        IReadOnlyList<Loss> losses, string? kept, string okText)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = what, TextWrapping = TextWrapping.Wrap });

        var d = new ContentDialog
        {
            XamlRoot = RootOf(owner),
            Title = title,
            PrimaryButtonText = okText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (losses.Count > 0)
        {
            var list = new StackPanel { Spacing = 6 };
            foreach (var loss in losses)
                list.Children.Add(new StatusChip
                {
                    Severity = loss.Severity,
                    Glyph = loss.Glyph,
                    Text = loss.Text,
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
            panel.Children.Add(list);
        }

        if (kept != null)
            panel.Children.Add(new TextBlock
            {
                Text = kept,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            });

        if (losses.Count > 0)
        {
            var ack = new CheckBox { Content = "Delete this permanently" };
            ack.Checked += (_, _) => d.IsPrimaryButtonEnabled = true;
            ack.Unchecked += (_, _) => d.IsPrimaryButtonEnabled = false;
            panel.Children.Add(ack);
            d.IsPrimaryButtonEnabled = false;
        }

        d.Content = panel;
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task Info(object owner, string title, string text)
    {
        var d = new ContentDialog
        {
            XamlRoot = RootOf(owner),
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            CloseButtonText = "Close",
        };
        await d.ShowAsync();
    }

    public static async Task<NewBranchInput?> NewBranch(object owner, SgRoot root, CheckoutConfig? preselect)
    {
        var name = new TextBox { Header = "Branch name", PlaceholderText = "feature-x" };
        var from = new ComboBox { Header = "From checkout", ItemsSource = root.Config.Checkouts.Select(c => c.Name).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var first = preselect ?? root.Config.Checkouts.FirstOrDefault();
        from.SelectedItem = first?.Name;
        var minimal = new CheckBox();
        var without = new TextBox { Header = "Also leave out (folders, one per line)", AcceptsReturn = true, Height = 70 };
        var shared = new SharedModeBox();
        var worktreeRoot = root.Config.WorktreeRoot ?? root.RootPath;
        // The optional and the shared folders belong to the checkout in the box, so they follow it.
        void Follow(CheckoutConfig? co)
        {
            minimal.Content = "Minimal: leave out the optional folders (" + string.Join(", ", co?.Optional ?? []) + ")";
            var folders = co?.Junctions ?? [];
            shared.Visibility = folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            shared.Header = "Shared folders (" + string.Join(", ", folders) + ") come as";
            shared.Mode = co?.Shared ?? SharedMode.Junction;
            if (co != null) shared.Detect(co.Path, worktreeRoot);
        }
        Follow(first);
        from.SelectionChanged += (_, _) => Follow(from.SelectedItem is string n ? root.Checkout(n) : null);
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(name);
        panel.Children.Add(from);
        panel.Children.Add(minimal);
        panel.Children.Add(without);
        panel.Children.Add(shared);
        var d = new ContentDialog
        {
            XamlRoot = RootOf(owner),
            Title = "New worktree",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        // Create used to be live with the name box empty: pressing it closed the dialog and made nothing,
        // with no branch, no error and no way to tell that anything had gone wrong.
        void SyncCreate() => d.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0 && from.SelectedItem is string;
        name.TextChanged += (_, _) => SyncCreate();
        from.SelectionChanged += (_, _) => SyncCreate();
        SyncCreate();
        if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
        if (name.Text.Trim().Length == 0 || from.SelectedItem is not string coName) return null;
        var list = without.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return new NewBranchInput(name.Text.Trim(), root.Checkout(coName), minimal.IsChecked == true, list, shared.Mode);
    }

    public static async Task<ServerCheckoutInput?> ServerCheckout(object owner, SgRoot root, CheckoutConfig? preselect)
    {
        var target = new TextBox { Header = "Server branch name, or full URL", PlaceholderText = "stable" };
        var near = new ComboBox { Header = "Copy from the nearest checkout", ItemsSource = root.Config.Checkouts.Select(c => c.Name).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        near.SelectedItem = (preselect ?? root.Config.Checkouts.FirstOrDefault())?.Name;
        var name = new TextBox { Header = "Folder name (optional)", PlaceholderText = "same as the branch name" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(target);
        panel.Children.Add(near);
        panel.Children.Add(name);
        var d = new ContentDialog
        {
            XamlRoot = RootOf(owner),
            Title = "New server checkout",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
        if (target.Text.Trim().Length == 0 || near.SelectedItem is not string nearName) return null;
        return new ServerCheckoutInput(target.Text.Trim(), root.Checkout(nearName), name.Text.Trim().Length > 0 ? name.Text.Trim() : null);
    }
}
