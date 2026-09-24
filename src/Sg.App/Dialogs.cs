using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

public sealed record NewBranchInput(string Name, string Checkout, bool Minimal, System.Collections.Immutable.ImmutableArray<string> Without, SharedMode Shared);
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

    public static async Task<NewBranchInput?> NewBranch(object owner, SgRoot root, CheckoutConfig? preselect, NewBranchInput? draft = null, Action<TaskFollowUp>? navigate = null, Action<CheckoutConfig>? transfer = null)
    {
        var name = new TextBox { Header = "Branch name", PlaceholderText = "feature-x", Text = draft?.Name ?? "" };
        var from = new ComboBox { Header = "From checkout", ItemsSource = root.Config.Checkouts.Select(c => c.Name).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var first = root.Config.Checkouts.FirstOrDefault(c => c.Name == draft?.Checkout) ?? preselect ?? root.Config.Checkouts.FirstOrDefault();
        from.SelectedItem = first?.Name;
        var minimal = new CheckBox { IsChecked = draft?.Minimal ?? false };
        var without = new TextBox { Header = "Also leave out (folders, one per line)", AcceptsReturn = true, Height = 70, Text = draft == null ? "" : string.Join("\n", draft.Without) };
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
            if (co != null) _ = shared.DetectAsync(co.Path, worktreeRoot);
        }
        Follow(first);
        if (draft != null) shared.Mode = draft.Shared;
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
            Title = draft == null ? "New worktree" : "Retry worktree creation",
            Content = panel,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var validation = new BranchTargetValidation();
        CheckoutConfig? transferFrom = null;
        if (transfer != null)
        {
            var link = new HyperlinkButton { Content = "Copy or move checkout edits into a worktree…" };
            link.Click += (_, _) => { if (from.SelectedItem is string n) { transferFrom = root.Checkout(n); d.Hide(); } };
            panel.Children.Add(link);
        }
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        AutomationProperties.SetAutomationId(summary, "NewWorktreeSummary");
        panel.Children.Add(summary);
        TaskFollowUp? destination = null;
        var existing = new DestinationAction { Requested = target => { destination = target; d.Hide(); } };
        panel.Children.Add(existing);
        void Explain(string message)
        {
            summary.Text = message;
            AutomationProperties.SetHelpText(name, message);
        }
        async void SyncCreate()
        {
            existing.Update(null, null);
            d.IsPrimaryButtonEnabled = false;
            var branch = name.Text.Trim();
            if (branch.Length > 0) Explain("Checking branch name and destination…");
            var check = await validation.CheckAsync(root, branch);
            if (check == null) return;
            existing.Update(root, check.Existing);
            var checkout = from.SelectedItem as string;
            d.IsPrimaryButtonEnabled = branch.Length > 0 && checkout != null && !check.Taken && check.Error == null;
            Explain(branch.Length == 0 ? "Give the branch a name."
                : checkout == null ? "Pick the checkout to build it on."
                : check.Error ?? (check.Taken ? $"{branch} is already a branch here. Give it another name."
                    : $"{branch} will be made on {checkout}, and its worktree with it."));
        }
        name.TextChanged += (_, _) => SyncCreate();
        from.SelectionChanged += (_, _) => SyncCreate();
        SyncCreate();
        ContentDialogResult result;
        try { result = await d.ShowAsync(); }
        finally { validation.Invalidate(); }
        if (transferFrom != null) { if (ReferenceEquals(root, Session.Root)) transfer?.Invoke(transferFrom); return null; }
        if (destination != null)
        {
            if (ReferenceEquals(root, Session.Root)) navigate?.Invoke(destination);
            return null;
        }
        if (result != ContentDialogResult.Primary) return null;
        if (name.Text.Trim().Length == 0 || from.SelectedItem is not string coName) return null;
        var list = without.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return new NewBranchInput(name.Text.Trim(), coName, minimal.IsChecked == true, [.. list], shared.Mode);
    }

    public static async Task<ServerCheckoutInput?> ServerCheckout(object owner, SgRoot root, CheckoutConfig? preselect)
    {
        var target = new TextBox { Header = "Server branch name, or full URL", PlaceholderText = "stable, or for git https://host/repo.git#stable" };
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
