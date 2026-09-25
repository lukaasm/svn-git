using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>Shared layout and request lifetime for the smaller workflow screens.</summary>
public abstract class WorkflowPage : SgPage
{
    protected readonly StackPanel Body = new() { Spacing = 12, Padding = new Thickness(16), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };
    protected readonly StatusStrip Pane = new();
    readonly ScrollViewer _scroll = new();
    protected ScrollViewer Scroll => _scroll;
    bool _busy;
    readonly PageReads _reads;
    protected readonly ReadFeedback Reading = new() { Margin = new Thickness(16), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
    protected WorkflowPage(string title)
    {
        Title = title;
        Reading.StateId = "WorkflowLoading";
        _reads = new(active =>
        {
            if (active) Reading.Show("Loading " + Title.ToLowerInvariant() + "…", Body.Children.Count == 0);
            else Reading.Hide();
            _scroll.IsEnabled = !active;
        });
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _scroll.Content = Body; _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        grid.Children.Add(Reading);
        Grid.SetRow(_scroll, 1); grid.Children.Add(_scroll);
        Grid.SetRow(Pane, 2); grid.Children.Add(Pane); Content = grid;
        Pane.ShowReadFeedback = false; // This layout places the shared feedback above its content.
        Shortcuts.Add(this, VirtualKey.F5, () => _ = Reload());
        Unloaded += (_, _) => OnHidden(); // Standalone window closure also ends disposable reads.
    }
    public override void OnShown(bool returning) => _ = Reload();
    public override void OnHidden() => _reads.Cancel();
    protected abstract Task Reload();
    private protected PageReads.Request BeginRead() => _reads.Begin();
    private protected static bool Current(PageReads.Request read) => read.Current;
    protected void Text(string text, bool heading = false) => Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = heading ? 20 : 14 });
    protected Button Action(string label, Func<Task> action, bool primary = false, bool enabled = true, bool mutates = false, string glyph = "")
    {
        var button = new IconButton { Text = label, Glyph = glyph, IsEnabled = enabled };
        if (primary) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) => { if (!_busy) await action(); };
        Body.Children.Add(mutates ? new TaskGate { Content = button } : button);
        return button;
    }
    protected Task Execute(string title, Action work) => Execute(title, () => { work(); return (object)"ok"; });
    protected async Task Execute<T>(string title, Func<T> work, PendingWorktree? worktree = null) where T : class
    {
        if (_busy) return;
        _busy = true; _scroll.IsEnabled = false;
        try { await Runner.Run(Pane, title, work, worktree: worktree); }
        finally { _busy = false; _scroll.IsEnabled = true; if (Host?.Current == this) await Reload(); }
    }
    protected Task Navigate(Func<SgPage> page, string key) { Go(page, key); return Task.CompletedTask; }
    protected static StackPanel Label(string text, string glyph)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        label.Children.Add(new TextBlock { Text = text });
        return label;
    }
    protected void Link(string text, string glyph, Func<SgPage> page, string key)
    {
        var link = new HyperlinkButton { Content = Label(text, glyph), Padding = new Thickness(0, 4, 0, 4) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(link, text);
        // Finish the link's invocation before replacing its visual tree with a page containing WebView.
        link.Click += (_, _) => DispatcherQueue.TryEnqueue(() => Go(page, key));
        Body.Children.Add(link);
    }
    protected void Status(string text, ChipSeverity severity, string glyph) =>
        Body.Children.Add(new StatusChip { Text = text, Severity = severity, Glyph = glyph });
    protected InfoBar ReadNotice(string message, string id)
    {
        var notice = new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = message };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(notice, id);
        Body.Children.Add(notice);
        return notice;
    }
    protected void ReadFailed(InfoBar notice, string message)
    {
        notice.Severity = InfoBarSeverity.Error;
        notice.Message = message;
        Action("Retry", Reload, glyph: "\uE72C");
        Action("View error log", () => { OutputWindow.Show(); return Task.CompletedTask; }, glyph: "\uE8A5");
    }
    protected void Details(string title, IEnumerable<string> lines)
    {
        var expander = new Expander { Header = Label(title, "\uE8A5"), HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, title);
        Body.Children.Add(expander);
    }
    protected void WrapActions(int start)
    {
        var row = new WrapRow { Spacing = 8 };
        while (Body.Children.Count > start)
        {
            var child = Body.Children[start]; Body.Children.RemoveAt(start); row.Children.Add(child);
        }
        Body.Children.Add(row);
    }
    protected Expander CollapseActions(int start, string title, string glyph = "\uE713")
    {
        var content = new StackPanel { Spacing = 12 };
        while (Body.Children.Count > start)
        {
            var child = Body.Children[start];
            Body.Children.RemoveAt(start);
            content.Children.Add(child);
        }
        var expander = new Expander { Header = Label(title, glyph), Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expander, title);
        Body.Children.Add(expander);
        return expander;
    }
    // Keep the persisted operation kind stable for older installations and recovery records.
    protected static string OperationTitle(OperationRecord record) => record.Kind == "Update from SVN" ? "Pull from SVN" : record.Kind;
}

public sealed class UpdateBranchPage : WorkflowPage
{
    readonly string _path;
    Func<Task>? _submit;
    OperationRecord? _lastResult;
    sealed record ViewState(bool Revisions, bool Advanced, double Offset);
    ViewState _view = new(false, false, 0);
    Expander? _revisions, _advanced;
    internal override object? CaptureViewState() => new ViewState(_revisions?.IsExpanded ?? _view.Revisions,
        _advanced?.IsExpanded ?? _view.Advanced, _revisions == null && _advanced == null ? _view.Offset : Scroll.VerticalOffset);
    internal override void RestoreViewState(object? state) { if (state is ViewState view) _view = view; }
    public UpdateBranchPage(string path) : base("Pull from SVN")
    {
        _path = path; Subtitle = path; Pane.StopAtBoundary(true);
        Shortcuts.Add(this, VirtualKey.Enter, VirtualKeyModifiers.Control, () => { if (_submit != null) _ = _submit(); });
    }
    protected override async Task Reload()
    {
        _submit = null;
        _view = (ViewState)CaptureViewState()!;
        _revisions = _advanced = null;
        Body.Children.Clear();
        using var generation = BeginRead(); var root = Session.Require();
        var completed = false;
        IProgress<BranchUpdateProgress> progress = new Progress<BranchUpdateProgress>(preview =>
        {
            if (completed || !Current(generation)) return;
            Reading.Show(preview.Stage + "…", preview.Branch.Length == 0);
            if (preview.Branch.Length == 0) return;
            Branch = preview.Branch; Checkout = preview.Checkout;
            Body.Children.Clear(); Scroll.IsEnabled = true;
            Text($"{preview.Branch} · {preview.Commits} local commits", true);
            Status("Preparing pull plan", ChipSeverity.Attention, "\uE895");
            Link("Review local commits", "\uE81C", () => new LogPage(_path), "log:" + _path);
            if (preview.BranchEdits != null) Edits("Branch edits", preview.BranchEdits, _path);
            else Body.Children.Add(new Skeleton { RowCount = 2 });
            if (preview.CheckoutEdits != null) Edits("Checkout edits", preview.CheckoutEdits, root.Checkout(preview.Checkout).Path);
            else Body.Children.Add(new Skeleton { RowCount = 2 });
            Text("SVN revisions", true);
            Body.Children.Add(new Skeleton { RowCount = 3 });
        });
        var state = await generation.Run(Pane, () => (object?)Operations.Pending(root, _path) ?? (_lastResult != null ? (object)Operations.Read(root, _lastResult.Id) : Operations.Plan(root, _path, progress: progress.Report)), _ => { });
        completed = true;
        if (Current(generation) && state == null)
        {
            Body.Children.Clear();
            Link("Review local commits", "\uE81C", () => new LogPage(_path), "log:" + _path);
            var notice = ReadNotice("Could not prepare this pull plan.", "PullPlanError");
            ReadFailed(notice, "Could not prepare the pull plan. Retry, or open the error log for details.");
        }
        if (state == null || !Current(generation)) return;
        Body.Children.Clear(); _submit = null;
        if (state is OperationRecord record)
        {
            Title = OperationTitle(record); Branch = record.Branch; Checkout = record.Checkout;
            Text(record.PhaseLabel, true); Text(record.Detail ?? (record.Terminal ? "The recorded operation is complete. Its checkpoint and recovery shelves remain available." : "The operation can resume from its recorded step."));
            var actions = Body.Children.Count;
            if (record.Phase == OperationPhase.Replaying)
                Action("Review replay", () => Navigate(() => new ConflictPage(_path), "resolve:" + _path), glyph: "\uE8A5");
            if (!record.Terminal && record.Phase != OperationPhase.NeedsReview)
            {
                _submit = () => Execute("Resume pull", () => _lastResult = Operations.Resume(root, record.Id));
                Action(record.Kind == "Update from SVN" ? "Resume pull" : "Refresh operation state", _submit, true, mutates: true, glyph: "\uE768");
            }
            if (record.Terminal) Action("Back to branch", () => { Close(); return Task.CompletedTask; }, true, glyph: "\uE72B");
            Action("Review saved edits", () => Navigate(() => new ShelfPage(root.Checkout(record.Checkout)), "shelves:" + record.Checkout), glyph: "\uE7B8");
            RefreshPlan();
            WrapActions(actions);
            var advanced = Body.Children.Count;
            Details("Steps and checkpoint", record.Steps.Select(step => "✓ " + step).Append("Branch checkpoint: " + record.Before
                + ". Restoring it does not undo SVN updates or published commits."));
            Link("Activity and checkpoints", "\uE81C", () => new ActivityPage(), "activity");
            if (!record.Terminal)
            {
                Text("Close operation keeps current files and every shelf. Any edits not restored yet remain on their shelves.");
                Action("Keep current files and close operation", () => Execute("Close operation", () => _lastResult = Operations.FinishReview(root, record.Id)), mutates: true, glyph: "\uE73E");
            }
            _advanced = CollapseActions(advanced, "Advanced");
        }
        else if (state is BranchUpdatePlan plan)
        {
            Branch = plan.Branch; Checkout = plan.Checkout;
            Text($"{plan.Branch} · {plan.Commits} local commits", true);
            Body.Children.Add(new StatusChip { Text = plan.Ready ? "Ready to pull" : "Needs attention", Severity = plan.Ready ? ChipSeverity.Success : ChipSeverity.Critical, Glyph = plan.Ready ? "\uE73E" : "\uE7BA" });
            Text("Save edits → sync SVN → replay commits → restore edits");
            Edits("Branch edits", plan.BranchEdits, _path);
            Edits("Checkout edits", plan.CheckoutEdits, root.Checkout(plan.Checkout).Path);
            foreach (var blocker in plan.Blockers) Body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = blocker });
            if (plan.Ready) _submit = () => Execute("Pull from SVN", () => _lastResult = Operations.Run(root, plan));
            var actions = Body.Children.Count;
            var updateLabel = plan.BranchEdits.Count + plan.CheckoutEdits.Count > 0 ? "Save edits and pull" : "Pull from SVN";
            var update = Action(updateLabel, () => Execute("Pull from SVN (stop requests wait for the current step)", () => _lastResult = Operations.Run(root, plan)), true, plan.Ready, mutates: true, glyph: "\uE896");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(update, "PullFromSvnButton");
            TaskGate.SetHelp(update, plan.Ready ? "Save local edits, pull SVN changes, replay this branch, then restore the saved edits."
                : string.Join("\n", plan.Blockers));
            RefreshPlan();
            Link("Review local commits", "\uE81C", () => new LogPage(_path), "log:" + _path);
            WrapActions(actions);
            var revisions = Body.Children.Count;
            foreach (var revision in plan.Revisions)
            {
                var changed = revision.From != revision.To;
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new StatusChip { Text = changed ? "Changed" : "Matching", Severity = changed ? ChipSeverity.Caution : ChipSeverity.Success, Glyph = changed ? "\uE895" : "\uE73E" });
                var description = new TextBlock { Text = $"{revision.WorkingCopy} · r{revision.From} → r{revision.To}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(description, 1);
                row.Children.Add(description);
                Body.Children.Add(row);
            }
            Text("Checked now. Sync may fetch newer work.");
            Link("View SVN log", "\uE81C", () => new SvnLogPage(root.Checkout(plan.Checkout)), "svnlog:" + plan.Checkout);
            var changedCount = plan.Revisions.Count(r => r.From != r.To);
            _revisions = CollapseActions(revisions, $"SVN revisions · {changedCount} changed · {plan.Revisions.Count - changedCount} matching", "\uE895");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_revisions, "PullRevisions");
            var advanced = Body.Children.Count;
            Text("Ignored files stay in place and are outside shelf coverage. Shared links follow the checkout; private shared-folder copies are not refreshed by this pull.");
            Link("Activity and checkpoints", "\uE81C", () => new ActivityPage(), "activity");
            _advanced = CollapseActions(advanced, "Advanced");
        }
        if (_revisions != null) _revisions.IsExpanded = _view.Revisions;
        if (_advanced != null)
        {
            _advanced.IsExpanded = _view.Advanced;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_advanced, "PullAdvanced");
        }
        BrowseScroll.Restore(Scroll, _view.Offset);
    }

    void RefreshPlan() => Action("Refresh pull plan", () => { _lastResult = null; return Reload(); }, glyph: "\uE72C");

    void Edits(string title, IReadOnlyCollection<string> files, string path)
    {
        Body.Children.Add(new StatusChip { Text = files.Count == 0 ? title + ": clean" : $"{title}: {files.Count} to preserve", Glyph = files.Count == 0 ? "\uE73E" : "\uE70F", Severity = files.Count == 0 ? ChipSeverity.Success : ChipSeverity.Attention });
        if (files.Count == 0) return;
        Body.Children.Add(new Expander { Header = $"Show {files.Count} changed files", HorizontalAlignment = HorizontalAlignment.Stretch, Content = new TextBlock { Text = string.Join("\n", files), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } });
        Link("Review " + title.ToLowerInvariant(), "\uE70F", () => new CommitPage(path), "commit:" + path);
    }
}

public sealed class ReviewPage : WorkflowPage
{
    readonly string _path;
    List<ReviewCheckConfig>? _checkDraft;
    public ReviewPage(string path) : base("Review readiness") { _path = path; Subtitle = path; }
    protected override async Task Reload()
    {
        using var generation = BeginRead(); var root = Session.Require();
        string? error = null;
        var status = await generation.Run(Pane, () => new[] { Review.Status(root, _path) }, message => error = message);
        if (!Current(generation)) return;
        if (status == null) { Body.Children.Clear(); ReadFailed(ReadNotice("", "ReviewReadError"), "Could not read review status. " + error); return; }
        var records = await generation.Run(Pane, () => new[] { Review.Read(root, _path) }, message => error = message);
        var record = records?.FirstOrDefault();
        if (!Current(generation)) return;
        Body.Children.Clear();
        if (records == null) { ReadFailed(ReadNotice("", "ReviewReadError"), "Could not read review results. " + error); return; }
        var severity = status[0] switch
        {
            "Ready for this version" => ChipSeverity.Success,
            "Checks failed" => ChipSeverity.Critical,
            "Not reviewed" => ChipSeverity.Neutral,
            _ => ChipSeverity.Caution
        };
        Status(status[0], severity, severity == ChipSeverity.Success ? "\uE73E" : severity == ChipSeverity.Neutral ? "\uE8A5" : "\uE7BA");
        Text("Review this branch version, then mark it ready. Publishing to SVN is a separate action.");
        Link("Review branch diff", "\uE8A5", () => new PushPage(_path), "push:" + _path);
        Link("Review uncommitted edits", "\uE70F", () => new CommitPage(_path), "commit:" + _path);
        if (record != null)
        {
            Text($"Last checked {record.Checked.LocalDateTime:g}");
            if (status[0] == "Changed since review") Text("These results describe an older version. Run checks again for the current files and configuration.");
            Details($"Reviewed version · {record.Files.Count} files", new[] { "HEAD: " + record.Head, "Snapshot: " + record.Snapshot }.Concat(record.Files));
            Body.Children.Add(new ReviewCheckResults(record, index => Go(
                () => new ReviewCheckOutputPage(record, index), $"review-output:{_path}:{record.Checked.UtcTicks}:{index}")));
        }
        if (root.Config.ReviewChecks.Count == 0) Text("No local checks configured. Run local checks records this version for manual review.");
        Action("Run local checks", () => Execute("Review checks", () => Review.RunChecks(root, _path)), true, mutates: true, glyph: "\uE768");
        Action("Mark this version ready", () => Execute("Mark reviewed", () => Review.MarkReady(root, _path)), enabled: status[0] == "Checks complete; review required", mutates: true, glyph: "\uE73E");
        var advancedStart = Body.Children.Count;
        var config = new ReviewChecksEditor(_checkDraft ?? root.Config.ReviewChecks);
        Body.Children.Add(config);
        var save = Action("Save local check configuration", () =>
        {
            var checks = config.Snapshot();
            return Execute("Save check configuration", () =>
            {
                var previous = root.Config.ReviewChecks;
                root.Config.ReviewChecks = checks;
                try { root.Save(); _checkDraft = null; }
                catch { root.Config.ReviewChecks = previous; throw; }
            });
        }, enabled: config.IsValid, mutates: true, glyph: "\uE74E");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(save, "SaveReviewChecks");
        config.Changed += () => { save.IsEnabled = config.IsValid; _checkDraft = config.Draft(); };
        CollapseActions(advancedStart, "Configure local checks");
    }
}

public sealed class CoveragePage : WorkflowPage
{
    readonly string _path;
    HandoffReceipt? _receipt;
    public CoveragePage(string path) : base("Backup coverage and handoff") { _path = path; Subtitle = path; }
    protected override async Task Reload()
    {
        using var generation = BeginRead(); Body.Children.Clear(); var root = Session.Require();
        Text("Check what the backup holds and whether restoration has been tested.");
        Action("Check current coverage", () => Execute("Check backup refs", () => _receipt = Backup.Coverage(root, _path)), true, mutates: true, glyph: "\uE72C");
        Link("View branch history", "\uE81C", () => new LogPage(_path), "log:" + _path);
        Link("Review readiness", "\uE73E", () => new ReviewPage(_path), "review:" + _path);
        if (_receipt != null)
        {
            var receipt = _receipt;
            Text($"{receipt.Branch} · checked {receipt.Checked.LocalDateTime:g}", true);
            var local = await generation.Run(Pane, () => new[] { Backup.LocalReceiptStatus(root, _path, receipt) });
            if (!Current(generation)) return;
            if (local != null) Body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = local[0] });
            if (receipt.Coverage.Count == 0) Status("No covered items in this receipt", ChipSeverity.Caution, "\uE7BA");
            foreach (var item in receipt.Coverage)
            {
                Text($"{item.Kind} · {item.Name}", true);
                var verified = item.State == "Remote refs checked";
                var failed = item.State is "failed" or "rejected";
                Status(string.IsNullOrWhiteSpace(item.State) ? "Not verified" : item.State,
                    verified ? ChipSeverity.Success : failed ? ChipSeverity.Critical : ChipSeverity.Caution,
                    verified ? "\uE73E" : "\uE7BA");
                Text($"{item.Commits} commits · Last upload: {item.Uploaded?.LocalDateTime.ToString("g") ?? "not recorded"}");
                if (item.Files.Count > 0) Details($"Included files ({item.Files.Count})", item.Files);
                if (item.Excluded.Count > 0) Details($"Outside coverage ({item.Excluded.Count})", item.Excluded);
            }
            Status(receipt.RestoreTested == null ? "Restore not verified" : $"Restore tested {receipt.RestoreTested.Value.LocalDateTime:g}",
                receipt.RestoreTested == null ? ChipSeverity.Caution : ChipSeverity.Success, receipt.RestoreTested == null ? "\uE7BA" : "\uE73E");
            var advancedStart = Body.Children.Count;
            if (receipt.RestorePath != null) Text("Rehearsal retained at " + receipt.RestorePath);
            if (receipt.TestedSnapshot != null) Text("Tested snapshot: " + receipt.TestedSnapshot);
            Action("Test restore in a separate branch", () =>
            {
                var name = receipt.Branch + "-restore-test-" + Guid.NewGuid().ToString("N")[..8];
                return Execute("Test backup restore", () => _receipt = Backup.TestRestore(root, receipt, name),
                    new(receipt.Checkout, name, root.WorktreePathFor(name)));
            }, mutates: true, glyph: "\uE8A7");
            var note = new TextBox { Header = "Handoff note (text only)", Text = receipt.Note, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            Body.Children.Add(note);
            Action("Save handoff receipt", async () =>
            {
                var file = await WindowHelper.PickSaveFile(this, receipt.Branch + ".handoff.json", "Handoff receipt", ".json");
                if (file != null)
                {
                    receipt.Note = note.Text;
                    await Execute("Save handoff", () => Backup.WriteReceipt(receipt, file));
                }
            }, mutates: true, glyph: "\uE74E");
            CollapseActions(advancedStart, "Restore testing and handoff");
        }
        Action("Preview handoff receipt", async () =>
        {
            var file = await WindowHelper.PickOpenFile(this, ".json");
            if (file == null) return;
            await Execute("Read handoff", () => _receipt = Backup.ReadReceipt(file));
            if (_receipt != null) await Execute("Check handoff refs", () => { foreach (var issue in Backup.ValidateReceipt(root, _receipt)) root.Log.Warn(issue); });
        }, mutates: true, glyph: "\uE8E5");
        Details("Coverage limits", ["Upload history, remote-ref checks, and restore tests are separate results.", "Ignored and shared files are outside backup coverage. A receipt records a point in time; check again after making changes."]);
        Link("Open backup restore preview", "\uE74E", () => new BackupPage(), "backup");
        Link("Activity and checkpoints", "\uE81C", () => new ActivityPage(), "activity");
    }
}

public sealed class StoragePage : WorkflowPage
{
    public StoragePage() : base("Storage and archive") { }
    protected override async Task Reload()
    {
        using var generation = BeginRead(); var root = Session.Require();
        Body.Children.Clear();
        Text("Review worktree sizes, archive eligibility, and retained temporary data.");
        Link("View retained shelves", "\uE7B8", () => new ShelfPage(), "shelves");
        Link("View recovery checkpoints", "\uE81C", () => new ActivityPage(), "activity");
        var loading = ReadNotice("Scanning worktrees and measuring files. Large folders or another repository operation can take time. You can keep browsing.", "StorageLoading");
        var plans = await generation.Run(Pane, () => Storage.List(root));
        if (!Current(generation)) return;
        if (plans == null) { ReadFailed(loading, "Could not read storage information. Retry the scan or open the error log for details."); return; }
        Body.Children.Clear();
        var summary = new TextBlock { Text = $"{plans.Count} worktrees · {plans.Count(p => p.Ready)} can be archived" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(summary, "StorageSummary");
        Body.Children.Add(summary);
        Details("How storage is measured", ["Logical sizes exclude links. Physical space recovered is unknown because shared blocks may remain in use.", "Archive retains a local commit checkpoint; it is not an off-machine backup."]);
        Link("View retained shelves", "\uE7B8", () => new ShelfPage(), "shelves");
        Link("View recovery checkpoints", "\uE81C", () => new ActivityPage(), "activity");
        if (plans.Count == 0) Status("No worktrees to archive", ChipSeverity.Neutral, "\uEDA2");
        foreach (var plan in plans)
        {
            Text(plan.Branch, true);
            Status(plan.Ready ? "Can be archived" : $"Protected · {plan.Blockers.Count} reasons", plan.Ready ? ChipSeverity.Neutral : ChipSeverity.Caution, plan.Ready ? "\uE7B8" : "\uE7BA");
            Text($"Logical size: {plan.LogicalBytes:N0} bytes · Space recovered: unknown");
            Link("View branch history", "\uE81C", () => new LogPage(plan.Path), "log:" + plan.Path);
            var archiveStart = Body.Children.Count;
            Text("Remove exactly " + plan.Path + " and preserve commit " + plan.Head + " as an Activity checkpoint.");
            foreach (var blocker in plan.Blockers) Text(blocker);
            Action("Archive and remove this worktree", async () => {
                if (await Dialogs.Confirm(this, "Archive this worktree?", $"This will:\n• Preserve commit {plan.Head} as an Activity checkpoint.\n• Remove branch {plan.Branch} and its worktree at {plan.Path}.\n• Recheck eligibility before removal.\n\nThe checkpoint stays on this machine; no backup is published.", "Archive worktree"))
                    await Execute("Archive branch", () => Storage.Archive(root, plan));
            }, enabled: plan.Ready, mutates: true, glyph: "\uE74D");
            CollapseActions(archiveStart, "Archive options · " + plan.Branch);
        }
        var temporaryNotice = ReadNotice("Checking retained temporary data…", "StorageTemporaryLoading");
        var temporary = await generation.Run(Pane, () => Storage.TemporaryData(root));
        if (!Current(generation)) return;
        if (temporary == null) { ReadFailed(temporaryNotice, "Worktrees were loaded, but temporary data could not be read."); return; }
        Body.Children.Remove(temporaryNotice);
        if (temporary.Count == 0) Text("No retained temporary directories.");
        if (temporary != null)
            foreach (var item in temporary)
            {
                Text("Temporary data · " + Path.GetFileName(item.Path), true);
                Status(item.Blockers.Count == 0 ? "Can be cleaned" : "Retained for safety", item.Blockers.Count == 0 ? ChipSeverity.Neutral : ChipSeverity.Caution, item.Blockers.Count == 0 ? "\uEDA2" : "\uE7BA");
                Text($"{item.LogicalBytes:N0} logical bytes · Space recovered: unknown");
                var cleanupStart = Body.Children.Count;
                Text(item.Path);
                foreach (var blocker in item.Blockers) Text(blocker);
                Action("Remove this temporary directory", async () => {
                    if (await Dialogs.Confirm(this, "Remove temporary data?", $"This will:\n• Recheck that these files are unchanged and eligible for cleanup.\n• Delete {item.Path}.\n• Record the cleanup in Activity.\n\nLogical size: {item.LogicalBytes:N0} bytes. Physical space recovered is unknown.", "Remove directory"))
                        await Execute("Remove temporary data", () => Storage.CleanTemporaryData(root, item));
                }, enabled: item.Blockers.Count == 0, mutates: true, glyph: "\uE74D");
                CollapseActions(cleanupStart, "Cleanup options · " + Path.GetFileName(item.Path));
            }
        Action("Refresh", Reload, glyph: "\uE72C");
    }
}
