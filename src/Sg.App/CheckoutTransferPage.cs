using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>The same preview handles new and existing worktrees; changing any option invalidates it.</summary>
public sealed class CheckoutTransferPage : WorkflowPage
{
    readonly CheckoutConfig _co;
    readonly string[]? _paths;
    readonly ComboBox _kind = new() { Header = "Destination", ItemsSource = new[] { "New worktree", "Existing worktree" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _name = new() { Header = "Branch name", PlaceholderText = "feature-x" };
    readonly ComboBox _existing = new() { Header = "Worktree", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    readonly CheckBox _move = new() { Content = "Move changes out of the checkout" };
    readonly TextBlock _explain = new() { TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _preview = new() { Spacing = 10 };
    readonly IconButton _check, _apply, _cancel;
    readonly ReportCard _report = new();
    CheckoutTransferPlan? _plan;
    bool _running;
    bool _previewing;
    int _generation;
    string? _wanted;
    sealed record ViewState(int Kind, string Name, string? Existing, bool Move);
    internal override object? CaptureViewState() => new ViewState(_kind.SelectedIndex, _name.Text, _existing.SelectedItem as string ?? _wanted, _move.IsChecked == true);
    internal override void RestoreViewState(object? state)
    {
        if (state is not ViewState view) return;
        _kind.SelectedIndex = view.Kind; _name.Text = view.Name; _wanted = view.Existing; _move.IsChecked = view.Move;
    }
    public CheckoutTransferPage(CheckoutConfig co, string[]? paths = null) : base("Transfer checkout changes", keepContentInteractive: true)
    {
        _co = co; _paths = paths; Checkout = co.Name; Subtitle = co.Path;
        Text("Copy checkout edits into a worktree, or move them there to continue on a branch.");
        if (paths != null) Text($"Using {paths.Length} selected path(s) from Changes in the checkout.");
        Body.Children.Add(_kind); Body.Children.Add(_name); Body.Children.Add(_existing);
        Body.Children.Add(_move); Body.Children.Add(_explain);
        _check = (IconButton)Action("Preview transfer", Preview, glyph: "\uE8A5");
        _apply = (IconButton)Action("Copy changes", Apply, primary: true, enabled: false, mutates: true, glyph: "\uE8C8");
        _cancel = (IconButton)Action("Cancel preview", () => { Invalidate(); return Task.CompletedTask; }, glyph: "\uE711");
        _cancel.Visibility = Visibility.Collapsed;
        WrapActions(Body.Children.Count - 3);
        Body.Children.Add(_preview); Body.Children.Add(_report);
        AutomationProperties.SetAutomationId(_kind, "TransferDestinationKind");
        AutomationProperties.SetAutomationId(_name, "TransferBranchName");
        AutomationProperties.SetAutomationId(_existing, "TransferExisting");
        AutomationProperties.SetAutomationId(_move, "TransferMove");
        AutomationProperties.SetAutomationId(_check, "TransferPreview");
        AutomationProperties.SetAutomationId(_apply, "TransferApply");
        AutomationProperties.SetAutomationId(_cancel, "TransferCancelPreview");
        _kind.SelectionChanged += (_, _) => Invalidate(); _existing.SelectionChanged += (_, _) => Invalidate();
        _name.TextChanged += (_, _) => Invalidate(); _move.Checked += (_, _) => Invalidate(); _move.Unchecked += (_, _) => Invalidate();
        Invalidate();
    }
    void Invalidate()
    {
        ++_generation;
        if (_previewing) { _previewing = false; CancelRead(); }
        _cancel.Visibility = Visibility.Collapsed;
        _plan = null; _apply.IsEnabled = false; _preview.Children.Clear(); _report.Hide();
        _name.Visibility = _kind.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        _existing.Visibility = _kind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _apply.Text = _move.IsChecked == true ? "Move changes" : "Copy changes";
        _apply.Glyph = _move.IsChecked == true ? "\uE8DE" : "\uE8C8";
        _explain.Text = _move.IsChecked == true
            ? "The worktree receives the files first. Then transferred edits are removed from the checkout. Recovery shelves keep the previous versions."
            : "The checkout keeps its edits. Existing worktree edits are merged where possible; overlapping changes block the transfer.";
        TaskGate.SetHelp(_apply, "Preview the current destination and options first.");
        _check.IsEnabled = !_running && (_kind.SelectedIndex == 0 ? _name.Text.Trim().Length > 0 : _existing.SelectedItem != null);
        ActionHint.SetHelp(_check, _check.IsEnabled ? "Check files and conflicts without changing either folder." : "Choose a destination first.");
    }
    protected override async Task Reload()
    {
        if (_running) return;
        Invalidate();
        using var read = BeginRead(); var root = Session.Require();
        var names = await read.Run(Pane, () =>
        {
            var bases = root.Git.BranchBases();
            return root.Git.WorktreeList().Where(w => !w.Bare && w.Branch != null && bases.GetValueOrDefault(w.Branch) == _co.Name)
                .Select(w => w.Branch!).Order().ToArray();
        });
        if (!Current(read) || names == null) return;
        var selected = _existing.SelectedItem as string ?? _wanted;
        _existing.ItemsSource = names; _existing.SelectedItem = names.Contains(selected) ? selected : names.FirstOrDefault();
        Invalidate();
    }
    async Task Preview()
    {
        if (_running) return;
        Invalidate(); var generation = _generation; using var read = BeginRead(); var root = Session.Require();
        var target = _kind.SelectedIndex == 0 ? _name.Text.Trim() : _existing.SelectedItem as string;
        if (string.IsNullOrEmpty(target)) return;
        var create = _kind.SelectedIndex == 0; var move = _move.IsChecked == true;
        _previewing = true; _cancel.Visibility = Visibility.Visible; _check.IsEnabled = false;
        Reading.Show("Checking checkout changes…", placeholders: false);
        var plan = await read.Run(Pane, () => CheckoutTransfer.Preview(root, _co, target, create, move, _paths,
            progress: (done, total) => DispatcherQueue.TryEnqueue(() =>
            {
                if (Current(read) && generation == _generation) Reading.Show($"Checking transfer · {done} of {total} files", placeholders: false);
            })));
        if (generation == _generation)
        {
            _previewing = false; _cancel.Visibility = Visibility.Collapsed; _check.IsEnabled = true;
        }
        if (!Current(read) || generation != _generation || plan == null) return;
        _plan = plan;
        _preview.Children.Add(new StatusChip { Text = $"{plan.Files.Count} files · {plan.Conflicts.Count} conflicts · {plan.LeftBehind.Count} left behind",
            Severity = plan.Conflicts.Count > 0 ? ChipSeverity.Caution : ChipSeverity.Success, Glyph = "\uE8A5" });
        if (plan.Files.Count > 0)
        {
            var header = new TextBlock(); var filter = new TextBox { PlaceholderText = "Filter files" };
            AutomationProperties.SetAutomationId(filter, "TransferFileFilter");
            var tree = new RowTreeView { Height = 280, ItemTemplate = (DataTemplate)Application.Current.Resources["FileTreeTemplate"] };
            AutomationProperties.SetAutomationId(tree, "TransferFiles");
            var list = new ListFilter(filter, tree, header, row => ((FileRow)row).Display);
            list.SetItems(plan.Files.Select(f => new FileRow { Path = f.Path, Status = f.Action == "Delete" ? 'D' : f.Action == "Add" ? 'A' : 'M', Display = f.Path + " · " + f.Action }).ToList(), "Files to transfer");
            _preview.Children.Add(header); _preview.Children.Add(filter); _preview.Children.Add(tree);
        }
        foreach (var notice in plan.Conflicts) Notice(notice, InfoBarSeverity.Warning);
        if (plan.LeftBehind.Count > 0)
        {
            var details = new Expander { Header = Label($"Left in the checkout ({plan.LeftBehind.Count})", "\uE946"), HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = string.Join("\n", plan.LeftBehind.Select(n => n.Path + ": " + n.Reason)), TextWrapping = TextWrapping.Wrap } };
            _preview.Children.Add(details);
        }
        if (plan.Files.Count == 0 && plan.Conflicts.Count == 0) Notice(new("No transferable changes", "The selected paths are clean or are listed under Left in the checkout."), InfoBarSeverity.Informational);
        _apply.IsEnabled = plan.CanApply;
        TaskGate.SetHelp(_apply, plan.CanApply ? "Transfer the previewed files. Both folders are checked again before applying." : "Choose transferable files and a destination without conflicts.");
    }
    void Notice(TransferNotice notice, InfoBarSeverity severity) => _preview.Children.Add(new InfoBar
        { IsOpen = true, IsClosable = false, Severity = severity, Title = notice.Path, Message = notice.Reason });
    async Task Apply()
    {
        if (_running || _plan is not { CanApply: true } plan) return;
        if (plan.Move && !await Dialogs.Confirm(this, "Move checkout changes?",
            $"Transfer {plan.Files.Count} file(s) to {plan.Target}, then remove only those edits from {_co.Name}.\n\nRecovery shelves retain both previous versions. {plan.LeftBehind.Count} path(s) stay in the checkout.\n\n" +
            string.Join("\n", plan.Files.Take(12).Select(f => f.Action + ": " + f.Path)) + (plan.Files.Count > 12 ? "\n… See the preview for the full list." : ""), "Move changes")) return;
        _running = true; _check.IsEnabled = _apply.IsEnabled = false; Scroll.IsEnabled = false;
        var root = Session.Require();
        try
        {
            var result = await Reports.Run(_report, Pane, (plan.Move ? "move" : "copy") + " checkout changes", () => CheckoutTransfer.Apply(root, plan),
                (card, value) => card.Show(ChipSeverity.Success, "\uE73E", $"{value.Files} file(s) {(value.Moved ? "moved" : "copied")}", "Recovery shelves retained for both folders."),
                plan.NewWorktree ? new(_co.Name, plan.Target, root.WorktreePathFor(plan.Target)) : null);
            _plan = null;
            if (result != null && Host?.Current == this)
            {
                _preview.Children.Clear();
                var open = new IconButton { Text = "Open worktree changes", Glyph = "\uE8A5" };
                AutomationProperties.SetAutomationId(open, "TransferOpenWorktree");
                open.Click += (_, _) => Go(() => new CommitPage(result.Path) { Checkout = _co.Name }, "commit:" + result.Path);
                _preview.Children.Add(open);
                var shelves = new HyperlinkButton { Content = "Checkout recovery shelves" };
                shelves.Click += (_, _) => Go(() => new ShelfPage(_co), "shelf:" + _co.Name);
                _preview.Children.Add(shelves);
            }
        }
        finally { _running = false; Scroll.IsEnabled = true; _check.IsEnabled = true; }
    }
}
