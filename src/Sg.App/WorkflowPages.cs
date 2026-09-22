using System.Text.Json;
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
    bool _busy;
    int _generation;
    protected WorkflowPage(string title)
    {
        Title = title;
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _scroll.Content = Body; _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        grid.Children.Add(_scroll);
        Grid.SetRow(Pane, 1); grid.Children.Add(Pane); Content = grid;
        Shortcuts.Add(this, VirtualKey.F5, () => _ = Reload());
    }
    public override void OnShown(bool returning) => _ = Reload();
    public override void OnHidden() => ++_generation;
    protected abstract Task Reload();
    protected int BeginRead() => ++_generation;
    protected bool Current(int generation) => generation == _generation;
    protected void Text(string text, bool heading = false) => Body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = heading ? 20 : 14 });
    protected Button Action(string label, Func<Task> action, bool primary = false, bool enabled = true, bool mutates = false)
    {
        var button = new Button { Content = label, IsEnabled = enabled };
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
}

public sealed class UpdateBranchPage : WorkflowPage
{
    readonly string _path;
    Func<Task>? _submit;
    OperationRecord? _lastResult;
    public UpdateBranchPage(string path) : base("Update from SVN")
    {
        _path = path; Subtitle = path; Pane.StopAtBoundary(true);
        Shortcuts.Add(this, VirtualKey.Enter, VirtualKeyModifiers.Control, () => { if (_submit != null) _ = _submit(); });
    }
    protected override async Task Reload()
    {
        var generation = BeginRead(); var root = Session.Require();
        var state = await Runner.Quiet(Pane, () => (object?)Operations.Pending(root, _path) ?? (_lastResult != null ? (object)Operations.Read(root, _lastResult.Id) : Operations.Plan(root, _path)));
        if (state == null || !Current(generation)) return;
        Body.Children.Clear(); _submit = null;
        if (state is OperationRecord record)
        {
            Title = record.Kind; Branch = record.Branch; Checkout = record.Checkout;
            Text(record.PhaseLabel, true); Text(record.Detail ?? (record.Terminal ? "The recorded operation is complete. Its checkpoint and recovery shelves remain available." : "The operation can resume from its recorded step."));
            foreach (var step in record.Steps) Text("✓ " + step);
            Text("Branch checkpoint: " + record.Before + ". Restoring it does not undo SVN updates or published commits.");
            if (record.Phase == OperationPhase.Replaying)
                Action("Review replay", () => Navigate(() => new ConflictPage(_path), "resolve:" + _path));
            if (!record.Terminal && record.Phase != OperationPhase.NeedsReview)
            {
                _submit = () => Execute("Resume update", () => _lastResult = Operations.Resume(root, record.Id));
                Action(record.Kind == "Update from SVN" ? "Resume update" : "Refresh operation state", _submit, true, mutates: true);
            }
            Action("Review saved edits", () => Navigate(() => new ShelfPage(root.Checkout(record.Checkout)), "shelves:" + record.Checkout));
            if (!record.Terminal)
            {
                Text("Close operation keeps current files and every shelf. Any edits not restored yet remain on their shelves.");
                Action("Keep current files and close operation", () => Execute("Close operation", () => _lastResult = Operations.FinishReview(root, record.Id)), mutates: true);
            }
            else Action("Back to branch", () => { Close(); return Task.CompletedTask; }, true);
        }
        else if (state is BranchUpdatePlan plan)
        {
            Branch = plan.Branch; Checkout = plan.Checkout;
            Text($"{plan.Branch} · {plan.Commits} local commits", true);
            Text("Save edits → sync SVN → replay commits → recover checkout edits → recover branch edits");
            foreach (var revision in plan.Revisions) Text($"{revision.WorkingCopy}: snapshot r{revision.From} → server r{revision.To} (checked now; sync may fetch newer work)");
            Text("Ignored files stay in place and are outside shelf coverage. Shared links follow the checkout; private shared-folder copies are not refreshed by this update.");
            Text($"Branch edits to preserve ({plan.BranchEdits.Count})\n" + string.Join("\n", plan.BranchEdits));
            Text($"Checkout edits to preserve ({plan.CheckoutEdits.Count})\n" + string.Join("\n", plan.CheckoutEdits));
            foreach (var blocker in plan.Blockers) Text(blocker);
            if (plan.Ready) _submit = () => Execute("Update from SVN", () => _lastResult = Operations.Run(root, plan));
            Action(plan.BranchEdits.Count + plan.CheckoutEdits.Count > 0 ? "Save edits and update" : "Update branch", () => Execute("Update from SVN (stop requests wait for the current step)", () => _lastResult = Operations.Run(root, plan)), true, plan.Ready, mutates: true);
        }
        Action("Activity and checkpoints", () => Navigate(() => new ActivityPage(), "activity"));
        Action("Plan another update", () => { _lastResult = null; return Reload(); });
    }
}

public sealed class ActivityPage : WorkflowPage
{
    public ActivityPage() : base("Activity and recovery") { }
    protected override async Task Reload()
    {
        var generation = BeginRead(); var root = Session.Require();
        var data = await Runner.Quiet(Pane, () =>
        {
            var records = Operations.List(root);
            var replays = root.Git.WorktreeList().Where(w => !w.Bare && Directory.Exists(w.Path) && Conflicts.HasPending(root.Git, w.Path) && records.All(r => r.Terminal || r.Path != w.Path)).ToList();
            return new { Records = records, Replays = replays };
        });
        if (data == null || !Current(generation)) return;
        var records = data.Records.OrderBy(r => r.Terminal).ThenByDescending(r => r.Updated).ToList();
        Body.Children.Clear(); Text("Operation history for " + root.Config.Root);
        foreach (var replay in data.Replays)
        {
            Text("Existing replay · " + replay.Branch, true);
            Text(replay.Path + " · Paused before operation history was recorded");
            Action("Review replay", () => Navigate(() => new ConflictPage(replay.Path), "resolve:" + replay.Path));
        }
        if (records.Count == 0 && data.Replays.Count == 0) Text("No recorded operations yet.");
        foreach (var record in records)
        {
            Text(record.Kind + " · " + record.Branch, true);
            Text($"{record.Updated.LocalDateTime:g} · {record.PhaseLabel}\n{record.Detail}\n" + string.Join("\n", record.Steps));
            if (!record.Terminal) Action(record.Action, () => Navigate(() => new UpdateBranchPage(record.Path), "update-branch:" + record.Path));
            if (record.Before.Length > 0) Action("Restore commits to a separate branch", () =>
            {
                var name = record.Branch + "-recovered-" + Guid.NewGuid().ToString("N")[..8];
                return Execute("Restore checkpoint", () => Operations.RestoreCheckpoint(root, record.Id, name),
                    new(record.Checkout, name, root.WorktreePathFor(name)));
            }, mutates: true);
        }
        Action("Refresh", Reload);
    }
}

public sealed class ReviewPage : WorkflowPage
{
    readonly string _path;
    public ReviewPage(string path) : base("Review readiness") { _path = path; Subtitle = path; }
    protected override async Task Reload()
    {
        var generation = BeginRead(); var root = Session.Require();
        var status = await Runner.Quiet(Pane, () => new[] { Review.Status(root, _path) });
        if (status == null || !Current(generation)) return;
        var records = await Runner.Quiet(Pane, () => new[] { Review.Read(root, _path) });
        var record = records?.FirstOrDefault();
        if (!Current(generation)) return;
        Body.Children.Clear(); Text(status[0], true);
        Text("Readiness covers the complete branch through its recorded HEAD. Publishing to SVN is a separate action. Checks execute only when you click Run local checks.");
        if (record != null)
        {
            Text($"HEAD {record.Head}\nSnapshot {record.Snapshot}\nFiles in the reviewed commit range:\n" + string.Join("\n", record.Files));
            foreach (var check in record.Checks) Text($"{check.Name}: exit {check.ExitCode}, {check.Seconds:F1}s\n{check.Command}\n{check.Output}");
        }
        Action("Review branch diff", () => Navigate(() => new PushPage(_path), "push:" + _path));
        Action("Review uncommitted edits", () => Navigate(() => new CommitPage(_path), "commit:" + _path));
        Action("Run local checks", () => Execute("Review checks", () => Review.RunChecks(root, _path)), true, mutates: true);
        Action("Mark this version ready", () => Execute("Mark reviewed", () => Review.MarkReady(root, _path)), enabled: status[0] == "Checks complete; review required", mutates: true);
        Text("Local check configuration (JSON array: name, executable, arguments). Commands run in this branch's folder.");
        var config = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinWidth = 500, MinHeight = 100, Text = JsonSerializer.Serialize(root.Config.ReviewChecks, SgConfig.JsonOptions) };
        Body.Children.Add(config);
        Action("Save local check configuration", () =>
        {
            var text = config.Text;
            return Execute("Save check configuration", () =>
            {
                var checks = JsonSerializer.Deserialize<List<ReviewCheckConfig>>(text, SgConfig.JsonOptions) ?? throw new SgException("Enter an array of checks.");
                if (checks.Any(x => string.IsNullOrWhiteSpace(x.Executable))) throw new SgException("Every check needs an executable.");
                root.Config.ReviewChecks = checks; root.Save();
            });
        }, mutates: true);
    }
}

public sealed class CoveragePage : WorkflowPage
{
    readonly string _path;
    HandoffReceipt? _receipt;
    public CoveragePage(string path) : base("Backup coverage and handoff") { _path = path; Subtitle = path; }
    protected override async Task Reload()
    {
        var generation = BeginRead(); Body.Children.Clear(); var root = Session.Require();
        Text("Uploaded, remote refs checked, and restore tested are separate results. Ignored/shared files are outside coverage. A receipt is a point-in-time record.");
        Action("Check current coverage", () => Execute("Check backup refs", () => _receipt = Backup.Coverage(root, _path)), true, mutates: true);
        if (_receipt != null)
        {
            var receipt = _receipt;
            Text($"{receipt.Branch} · checked {receipt.Checked.LocalDateTime:g}", true);
            var local = await Runner.Quiet(Pane, () => new[] { Backup.LocalReceiptStatus(root, _path, receipt) });
            if (!Current(generation)) return;
            if (local != null) Text(local[0]);
            foreach (var item in receipt.Coverage) Text($"{item.Kind}/{item.Name}: {item.State} · {item.Commits} commits · last upload {item.Uploaded?.LocalDateTime.ToString("g") ?? "not recorded"}\nPaths in this category (exclusions below):\n" + string.Join("\n", item.Files) + "\n" + string.Join("\n", item.Excluded.Select(x => "Excluded: " + x)));
            Text(receipt.RestoreTested == null ? "Restore not verified" : $"Restore tested {receipt.RestoreTested.Value.LocalDateTime:g}, base {receipt.TestedSnapshot}");
            if (receipt.RestorePath != null) Text("Rehearsal retained at " + receipt.RestorePath);
            Action("Test restore in a separate branch", () =>
            {
                var name = receipt.Branch + "-restore-test-" + Guid.NewGuid().ToString("N")[..8];
                return Execute("Test backup restore", () => _receipt = Backup.TestRestore(root, receipt, name),
                    new(receipt.Checkout, name, root.WorktreePathFor(name)));
            }, mutates: true);
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
            }, mutates: true);
        }
        Action("Preview handoff receipt", async () =>
        {
            var file = await WindowHelper.PickOpenFile(this, ".json");
            if (file == null) return;
            await Execute("Read handoff", () => _receipt = Backup.ReadReceipt(file));
            if (_receipt != null) await Execute("Check handoff refs", () => { foreach (var issue in Backup.ValidateReceipt(root, _receipt)) root.Log.Warn(issue); });
        }, mutates: true);
        Action("Open backup restore preview", () => Navigate(() => new BackupPage(), "backup"));
    }
}

public sealed class StoragePage : WorkflowPage
{
    public StoragePage() : base("Storage and archive") { }
    protected override async Task Reload()
    {
        var generation = BeginRead(); var root = Session.Require();
        var plans = await Runner.Quiet(Pane, () => Storage.List(root));
        if (plans == null || !Current(generation)) return;
        Body.Children.Clear();
        Text("Logical sizes exclude links. Reclaimable space is unknown because shared blocks may remain in use. Archive retains a local commit checkpoint; it is not an off-machine backup.");
        foreach (var plan in plans)
        {
            Text(plan.Branch, true); Text($"{plan.Path}\nLogical size: {plan.LogicalBytes:N0} bytes · Reclaimable: unknown");
            foreach (var blocker in plan.Blockers) Text(blocker);
            Action("Preview archive removal", () =>
            {
                Text("Remove exactly " + plan.Path + " and preserve commit " + plan.Head + " as an Activity checkpoint.", true);
                Action("Archive and remove this worktree", () => Execute("Archive branch", () => Storage.Archive(root, plan)), true, plan.Ready, mutates: true);
                return Task.CompletedTask;
            }, enabled: plan.Ready);
        }
        var temporary = await Runner.Quiet(Pane, () => Storage.TemporaryData(root));
        if (!Current(generation)) return;
        if (temporary != null)
            foreach (var item in temporary)
            {
                Text($"Temporary data: {item.Path}\n{item.LogicalBytes:N0} logical bytes; reclaimable unknown");
                foreach (var blocker in item.Blockers) Text(blocker);
                Action("Remove this temporary directory", () => Execute("Remove temporary data", () => Storage.CleanTemporaryData(root, item)), enabled: item.Blockers.Count == 0, mutates: true);
            }
        Action("View retained shelves", () => Navigate(() => new ShelfPage(), "shelves"));
        Action("View recovery checkpoints", () => Navigate(() => new ActivityPage(), "activity"));
        Action("Refresh", Reload);
    }
}
