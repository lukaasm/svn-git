using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// Work that stopped on conflicts, whatever stopped it: a rebase onto a newer snapshot, or an import
/// replaying a branch that came in a file. Both leave the same thing behind, so this is one page with
/// one set of buttons; only the words change, because "keep the SVN version" means nothing during an
/// import and "keep the imported version" means nothing during a rebase.
///
/// Pick a side per file, or edit and mark resolved, then continue. Skip drops the one commit it
/// stopped on and goes on with the rest. Abort puts it back, and says first what that costs.
/// </summary>
public sealed partial class ConflictPage : SgPage
{
    readonly string _worktree;
    readonly ListFilter _filter;

    /// <summary>What the last read found. The buttons word themselves from it.</summary>
    ConflictState _state = new();

    public ConflictPage(string worktree)
    {
        InitializeComponent();
        _worktree = worktree;
        Title = "Resolve conflicts";
        Subtitle = worktree;
        ColumnSplitter.Attach(Splitter);
        Shortcuts.DiffNavigation(this, Diff);
        FileActions.Attach(Files, n => PathUtil.Join(_worktree, n.FullPath));
        _filter = new ListFilter(Filter, Files, FilesHeader, r => ((FileRow)r).Display);
        _filter.Picked += OnPicked;
        Session.Log.Sink = Pane;
        _ = LoadAsync();
    }

    /// <summary>Every action reloads, and only the newest read may draw. Six buttons start their own.</summary>
    int _generation;

    /// <summary>The file whose two versions are in the diff, so a reload can put it back there.</summary>
    string? _shownPath;

    /// <summary>The row it came from, so a change of what to compare can draw the same file again.</summary>
    TreeNode? _shownNode;

    async Task LoadAsync()
    {
        var root = Session.Require();
        var gen = ++_generation;
        // SetItems builds a new list and drops the selection with it, so what was open has to be asked
        // for again: resolving one of ten files used to close the diff and scroll the list back to the top.
        var reopen = _shownPath;
        if (_filter.Count == 0) FilesSkeleton.Show();
        var state = await Runner.Quiet(Pane, () => Conflicts.State(root, _worktree));
        FilesSkeleton.Hide();
        if (state == null || gen != _generation) return;
        _state = state;
        Checkout ??= state.Checkout;
        Branch ??= state.Branch;
        Subtitle = state.Kind == Replay.Import
            ? $"{state.Branch}  -  imported commits   {_worktree}"
            : $"{state.Branch}  onto  svn/{state.Checkout}   {_worktree}";
        NameButtons(state);
        // Three shapes, and only the first is a conflict. A stuck step has no sides to pick between,
        // so what it lists is whatever a forced apply left in the worktree for a hand to finish.
        var rows = state.Conflicted.Count > 0
            ? state.Conflicted.Select(p => new FileRow { Status = 'C', Path = p, Display = "C  " + p }).ToList()
            : state.ByHand.Select(p => new FileRow { Status = Mark(p), Path = p, Display = Mark(p) + "  " + p }).ToList();
        _filter.SetItems(rows, state.Conflicted.Count > 0 ? "Files in conflict" : "Files to finish by hand");
        ShowEmpty(rows.Count == 0);
        ForceButton.Visibility = state.Stuck && state.Kind == Replay.Import ? Visibility.Visible : Visibility.Collapsed;
        EmptyForce.Visibility = ForceButton.Visibility;
        // The resolver settles files at three stages and nothing else. Going on from here it can also
        // continue and skip, so it is offered whenever something is stopped, except an import stuck
        // on a patch that will not go in: forcing what fits of one is a choice for a hand.
        AutoButton.IsEnabled = state.Conflicted.Count > 0;
        AutoAllButton.IsEnabled = state.InProgress && !(state.Stuck && state.Kind == Replay.Import);

        if (!state.InProgress)
        {
            StateBar.Severity = InfoBarSeverity.Success;
            StateBar.Message = "Nothing is stopped here. The branch is in a normal state.";
            NoConflicts.Title = "Nothing is stopped here";
            NoConflicts.Text = "The branch is in a normal state. There is nothing to resolve.";
            ContinueButton.IsEnabled = SkipButton.IsEnabled = AbortButton.IsEnabled = false;
            EmptyContinue.Visibility = EmptyAbort.Visibility = Visibility.Collapsed;
        }
        else if (state.Stuck)
        {
            // Never "every file is resolved" here. Continue can only fail, so it is off, and the two
            // things that do work are the ones on screen.
            var out_ = state.Kind == Replay.Import
                ? "Skip it and the rest of the series still lands. Or force what fits of it into the worktree: "
                  + "the hunks that land are written, the ones that do not are left beside their file as .rej, "
                  + "and you finish them by hand and mark them resolved."
                : "Skip it, and the ones after it still replay.";
            StateBar.Severity = InfoBarSeverity.Warning;
            StateBar.Message = Headline(state) + " " + Conflicts.StuckNote(state.Kind) + " " + out_;
            NoConflicts.Title = state.Kind == Replay.Import ? "This patch would not go in at all" : "This commit changes nothing here";
            NoConflicts.Text = Conflicts.StuckNote(state.Kind) + " " + out_;
            ContinueButton.IsEnabled = false;
            SkipButton.IsEnabled = AbortButton.IsEnabled = true;
            EmptyContinue.Visibility = Visibility.Collapsed;
            EmptyAbort.Visibility = Visibility.Visible;
        }
        else if (rows.Count == 0)
        {
            StateBar.Severity = InfoBarSeverity.Informational;
            StateBar.Message = $"Every file is resolved. Continue the {state.Verb}.";
            NoConflicts.Title = "Every file is resolved";
            NoConflicts.Text = $"Continue the {state.Verb}. The next commit may stop again, and this page comes back if it does.";
            ContinueButton.IsEnabled = SkipButton.IsEnabled = AbortButton.IsEnabled = true;
            EmptyContinue.Visibility = EmptyAbort.Visibility = Visibility.Visible;
        }
        else if (state.Conflicted.Count > 0)
        {
            StateBar.Severity = InfoBarSeverity.Warning;
            StateBar.Message = Headline(state) + $" {state.Conflicted.Count} file(s) are changed on both sides. "
                               + $"Pick a version for each - left is the {state.OursLabel}, right is the {state.TheirsLabel} - "
                               + "or edit the file and mark it resolved. Then continue.";
            ContinueButton.IsEnabled = false;
            SkipButton.IsEnabled = AbortButton.IsEnabled = true;
        }
        else
        {
            StateBar.Severity = InfoBarSeverity.Warning;
            StateBar.Message = Headline(state) + $" {rows.Count} file(s) are waiting for a hand. Every .rej file holds the "
                               + "hunks that would not go in: put them in, delete the .rej, then mark the file resolved and continue.";
            ContinueButton.IsEnabled = SkipButton.IsEnabled = AbortButton.IsEnabled = true;
        }

        if (reopen != null && rows.Any(r => r.Path.Equals(reopen, StringComparison.OrdinalIgnoreCase)))
            _filter.Select(r => r is FileRow f && f.Path.Equals(reopen, StringComparison.OrdinalIgnoreCase));
        if (Files.SelectedItem == null)
        {
            _shownPath = null;
            Diff.ShowText(rows.Count == 0 ? ""
                    : state.Conflicted.Count > 0 ? $"Pick a file to see the {state.OursLabel} on the left and the {state.TheirsLabel} on the right."
                    : "Pick a file to see what the branch holds on the left and what is on disk now on the right.",
                rows.Count == 0 ? "nothing in conflict" : $"{rows.Count} file(s)");
        }
    }

    /// <summary>The letter a by-hand row carries: R for what a forced apply could not put in, M for the rest.</summary>
    static char Mark(string path) => path.EndsWith(".rej", StringComparison.OrdinalIgnoreCase) ? 'R' : 'M';

    /// <summary>The first sentence: what stopped, where it got to, and on which commit.</summary>
    static string Headline(ConflictState s)
    {
        var where = s.Kind == Replay.Import
            ? $"The import of {s.Branch}"
            : $"The rebase of {s.Branch} onto svn/{s.Checkout}";
        var at = s.Of > 0 ? $" at {s.At} of {s.Of}" : "";
        var on = s.Stopped.Length > 0 ? $", on \"{s.Stopped}\"" : "";
        return where + " stopped" + at + on + ".";
    }

    /// <summary>
    /// The two sides have no fixed names: a rebase replays the branch over what SVN said, an import
    /// replays what came in the file over the branch. A button that says the wrong one of those is
    /// worse than a button that says nothing, so the words come from the state on every read.
    /// </summary>
    void NameButtons(ConflictState s)
    {
        var verb = s.InProgress ? " " + s.Verb : "";
        ContinueButton.Text = EmptyContinue.Text = "Continue" + verb;
        AbortButton.Text = EmptyAbort.Text = "Abort" + verb;
        SkipButton.Text = s.Kind == Replay.Import ? "Skip this patch" : "Skip this commit";
        TakeOurs.Text = "Keep " + Article(s.OursLabel);
        TakeTheirs.Text = "Keep " + Article(s.TheirsLabel);
        TakeOurs.SetValue(ToolTipService.ToolTipProperty,
            $"For the ticked files: keep the {s.OursLabel}, the one on the left, and drop the other side of the change.");
        TakeTheirs.SetValue(ToolTipService.ToolTipProperty,
            $"For the ticked files: keep the {s.TheirsLabel}, the one on the right, and drop the other side of the change.");
        SkipButton.SetValue(ToolTipService.ToolTipProperty, s.Kind == Replay.Import
            ? "Drop the patch this stopped on and go on with the ones after it. Asks first."
            : "Drop the commit this stopped on and go on with the ones after it. Asks first.");
        AbortButton.SetValue(ToolTipService.ToolTipProperty, AbortCost(s));
        // The pairs to compare are named from the same labels, and keep their place across reloads.
        // Refilling the box fires its change event with nothing new to show, so that is told apart
        // from a person picking a pair.
        var keep = Compare.SelectedIndex < 0 ? 0 : Compare.SelectedIndex;
        _naming = true;
        try
        {
            Compare.ItemsSource = new[]
            {
                $"{s.OursLabel}  ↔  {s.TheirsLabel}",
                $"base  →  {s.OursLabel}   (what that side changed)",
                $"base  →  {s.TheirsLabel}   (what that side changed)",
            };
            Compare.SelectedIndex = keep;
        }
        finally { _naming = false; }
    }

    bool _naming;

    /// <summary>Which two stages the diff shows: 2 and 3 are the sides, 1 is what both started from.</summary>
    (int Left, int Right) Stages() => Compare.SelectedIndex switch
    {
        1 => (1, 2),
        2 => (1, 3),
        _ => (2, 3),
    };

    /// <summary>The same file again, between the two versions now picked.</summary>
    void Compare_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_naming && _shownNode != null && _state.Conflicted.Count > 0) OnPicked(_shownNode);
    }

    /// <summary>"SVN version" reads as a name on a button; "the SVN version" reads as a sentence in a tooltip.</summary>
    static string Article(string label) => "the " + label;

    /// <summary>
    /// What abort costs, which is not the same for the two. A rebase gives the branch back exactly as
    /// it was. An import is one series to git, so undoing it takes off every commit that went in, not
    /// only the one that stopped - and the file it came from is what still holds them.
    /// </summary>
    static string AbortCost(ConflictState s) => s.Kind == Replay.Import
        ? "Take the whole import back off the branch, the commits that already went in included. "
          + "git undoes a patch series as one thing. The export file still holds every commit, so this can be run again."
        : "Stop the rebase and put the branch back exactly as it was before. Nothing is lost.";

    /// <summary>
    /// The files an action works on: whatever is ticked, or the one line you are reading when
    /// nothing is. Picking a side for the file already open in the diff is the common case, and it
    /// should not cost a tick first.
    /// </summary>
    List<string> Picked()
    {
        var ticked = _filter.Rows<FileRow>().Where(r => r.Checked).Select(r => r.Path).ToList();
        if (ticked.Count > 0) return ticked;
        var one = _filter.Selected<FileRow>();
        return one == null ? new List<string>() : [one.Path];
    }

    async void OnPicked(TreeNode node)
    {
        if (node.Row is not FileRow row)
        {
            // A conflict is two versions of one file; a folder has none to show, only a count to act on.
            _shownPath = null;
            _shownNode = null;
            Diff.ShowText($"{node.FileCount} file(s) in conflict under {node.FullPath}.\n\nTick the folder to pick a side for all of them, or pick one file to see its two versions.", node.FullPath);
            return;
        }
        _shownPath = row.Path;
        _shownNode = node;
        var git = Session.Require().Git;
        // A file at three stages has two versions to compare, and a third both started from. One a
        // forced apply left behind has only itself, so it is read against what the branch holds -
        // which is the change, half put in.
        var conflict = _state.Conflicted.Any(p => p.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
        var (left, right) = Stages();
        var reads = conflict
            ? new DiffView.Reads(() => git.ShowStage(_worktree, left, row.Path), () => git.ShowStage(_worktree, right, row.Path))
            : new DiffView.Reads(() => git.ShowTextIn(_worktree, "HEAD", row.Path), () => OnDisk(row.Path));
        var name = (int stage) => stage == 1 ? "base" : stage == 2 ? _state.OursLabel : _state.TheirsLabel;
        var title = conflict
            ? $"{row.Path}   {name(left)} (left)  →  {name(right)} (right)"
            : $"{row.Path}   branch (left)  →  on disk now (right)";
        await Diff.ShowFileAsync(row.Path, title, reads, () => _filter.IsCurrent(node),
            binaryNote: conflict ? "binary file, pick a version: " : "binary file: ");
    }

    /// <summary>What the file holds right now. A .rej file is not in git at all, so nothing else can read it.</summary>
    string OnDisk(string rel)
    {
        try
        {
            var abs = PathUtil.Join(_worktree, rel);
            return File.Exists(abs) ? File.ReadAllText(abs) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    async Task TakeAsync(bool ours, string label)
    {
        var paths = Picked();
        if (paths.Count == 0) { await Dialogs.Info(this, "Nothing picked", "Tick one or more files first, or select one."); return; }
        var root = Session.Require();
        await Runner.Run(Pane, $"{label} for {paths.Count} file(s)", () => root.Git.TakeSide(_worktree, paths, ours));
        await LoadAsync();
    }

    async void TakeOurs_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => TakeAsync(ours: true, "keep the " + _state.OursLabel));
    async void TakeTheirs_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => TakeAsync(ours: false, "keep the " + _state.TheirsLabel));

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in Picked())
        {
            var abs = PathUtil.Join(_worktree, p);
            try { Process.Start(new ProcessStartInfo(abs) { UseShellExecute = true }); }
            catch (Exception ex) { Pane.Append("cannot open " + abs + ": " + ex.Message); }
        }
    }

    async void MarkResolved_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, async () =>
    {
        var paths = Picked();
        if (paths.Count == 0) { await Dialogs.Info(this, "Nothing picked", "Tick one or more files first, or select one."); return; }
        var root = Session.Require();
        await Runner.Run(Pane, "mark resolved", () => root.Git.MarkResolved(_worktree, paths));
        await LoadAsync();
    });

    /// <summary>
    /// The ticked files, or all of them, to the resolver, once. Nothing is continued: what it settled is
    /// staged and still on the page, with the diff to read it by, and Continue is the next thing.
    /// </summary>
    async void Auto_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, async () =>
    {
        var ticked = _filter.Rows<FileRow>().Where(r => r.Checked).Select(r => r.Path).ToList();
        var root = Session.Require();
        var what = ticked.Count > 0 ? $"{ticked.Count} ticked file(s)" : $"{_state.Conflicted.Count} file(s)";
        var r = await Runner.Run(Pane, "auto-resolve " + what, () => Conflicts.AutoResolve(root, _worktree, ticked));
        if (r != null) ShowStep(r);
        await LoadAsync();
    }, restoreEnabled: false);

    /// <summary>What one run of the resolver did, where the reader is looking.</summary>
    void ShowStep(AutoResolveResult r)
    {
        foreach (var p in r.Resolved) Pane.Append("settled: " + p);
        foreach (var l in r.Left) Pane.Append("left: " + l.Path + "  (" + l.Why + ")");
        StateBar.Severity = r.AllResolved ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
        StateBar.Message = r.AllResolved
            ? $"The resolver settled {r.Resolved.Count} file(s). They are marked resolved: read them in the diff if you like, then continue."
            : $"The resolver settled {r.Resolved.Count} file(s) and left {r.Left.Count}: "
              + string.Join("; ", r.Left.Select(l => l.Path + " - " + l.Why)) + ". Pick a version for those, or edit them and mark them resolved.";
    }

    /// <summary>
    /// From here to the end, or to the first file the resolver cannot settle: settle, continue, settle
    /// the next commit's files. Every stop it went through is a commit on the branch by the time this
    /// returns, so it asks first; the log pane says what it did at each one.
    /// </summary>
    async void AutoAll_Click(object sender, RoutedEventArgs e)
    {
        var s = _state;
        var what = s.Kind == Replay.Import ? "patch" : "commit";
        var left = s.Of > 0 ? $" {s.Of - s.At + 1} {what}(s) are left to replay, this one included." : "";
        if (!await Dialogs.Confirm(this, "Auto-resolve and continue",
                $"Let the resolver settle every stop from here on?{left}\n\n"
                + $"It settles the files, the {s.Verb} continues, and the next {what} that stops is handed over too, until it is through "
                + $"or a file comes back that it could not settle. A {what} left with nothing to commit is skipped. "
                + "Each stop it goes through becomes a commit on the branch; the log below says what it did at every one, "
                + "and the log of the branch shows the result.",
                "Go"))
            return;
        await Busy.During(sender, async () =>
        {
            var root = Session.Require();
            var verb = s.Verb;
            var run = await Runner.Run(Pane, "auto-resolve and continue", () => Conflicts.AutoResolveAll(root, _worktree));
            if (run == null) { await LoadAsync(); return; }
            foreach (var step in run.Steps)
            {
                Pane.Append("stopped" + (step.Of > 0 ? $" at {step.At} of {step.Of}" : "") + (step.Stopped.Length > 0 ? $" on \"{step.Stopped}\"" : "")
                            + $": settled {step.Resolved.Count}, left {step.Left.Count}");
            }
            if (run.Skipped > 0) Pane.Append($"{run.Skipped} {what}(s) changed nothing here any more and were skipped");
            if (run.Ok && run.Finished != null)
            {
                var f = run.Finished;
                f.Output = $"{run.Steps.Count} stop(s), {run.FilesResolved} file(s) settled by the resolver" + (run.Skipped > 0 ? $", {run.Skipped} skipped" : "");
                await AfterStep(f, verb);
                return;
            }
            if (run.Ok) { await AfterStep(new ResolveResult { Ok = true, Kind = s.Kind, Branch = s.Branch, Checkout = s.Checkout }, verb); return; }
            Pane.Append("it stopped short: " + run.Why);
            await LoadAsync();
            var last = run.Steps.LastOrDefault();
            if (last != null && !last.AllResolved) ShowStep(last);
            else
            {
                StateBar.Severity = InfoBarSeverity.Warning;
                StateBar.Message = "The resolver stopped short. " + run.Why;
            }
        }, restoreEnabled: false);
    }

    // sender, not ContinueButton: two buttons run this, and the empty state's one is the only one on
    // screen once every file is resolved. The ring used to spin inside a collapsed panel.
    async void Continue_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, async () =>
    {
        var root = Session.Require();
        var verb = _state.Verb;
        var r = await Runner.Run(Pane, "continue the " + verb, () => Conflicts.Continue(root, _worktree));
        await AfterStep(r, verb);
    }, restoreEnabled: false);

    async void Skip_Click(object sender, RoutedEventArgs e)
    {
        var s = _state;
        var what = s.Kind == Replay.Import ? "patch" : "commit";
        var named = s.Stopped.Length > 0 ? $"\"{s.Stopped}\"" : "the one it stopped on";
        if (!await Dialogs.Confirm(this, $"Skip this {what}",
                $"Drop {named} and go on with the {what}es after it?\n\n"
                + $"What that one changed is not on the branch afterwards. The others still land.",
                "Skip it"))
            return;
        await Busy.During(sender, async () =>
        {
            var root = Session.Require();
            var verb = s.Verb;
            var r = await Runner.Run(Pane, $"skip the {what}", () => Conflicts.Skip(root, _worktree));
            await AfterStep(r, verb);
        }, restoreEnabled: false);
    }

    /// <summary>
    /// The way on from a patch git would not take at all. What still fits is written into the worktree
    /// and what does not is left beside each file as .rej, for a hand to put in. Nothing is staged, so
    /// the page comes back listing exactly what there is to finish.
    /// </summary>
    async void Force_Click(object sender, RoutedEventArgs e)
    {
        var named = _state.Stopped.Length > 0 ? $"\"{_state.Stopped}\"" : "the patch it stopped on";
        if (!await Dialogs.Confirm(this, "Force what fits",
                $"Write as much of {named} into the worktree as still fits?\n\n"
                + "Every hunk that does not fit is written beside its own file as a .rej file, to put in by hand. "
                + "Nothing is committed, and nothing is staged until you mark it resolved.",
                "Force it"))
            return;
        await Busy.During(sender, async () =>
        {
            var root = Session.Require();
            var r = await Runner.Run(Pane, "force what fits", () => Conflicts.ApplyWhatFits(root, _worktree));
            if (r != null)
            {
                StateBar.Severity = r.Rejected.Count == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
                StateBar.Message = r.Rejected.Count == 0
                    ? $"All of it fitted after all: {r.Applied.Count} file(s) are changed in the worktree. Mark them resolved, then continue."
                    : $"{r.Applied.Count} file(s) took the whole patch, {r.Rejected.Count} did not: {string.Join(", ", r.Rejected)}. "
                      + "Each of those has a .rej file beside it holding the hunks that were refused. Put them in, delete the .rej, then mark the file resolved.";
            }
            await LoadAsync();
        });
    }

    /// <summary>
    /// What continue and skip both do with their answer. Through means the page has nothing left to
    /// show; stopped again is normal, and the reload puts the next set of files in the list.
    /// </summary>
    async Task AfterStep(ResolveResult? r, string verb)
    {
        if (r == null) { await LoadAsync(); return; }
        if (r.Ok)
        {
            StateBar.Severity = InfoBarSeverity.Success;
            StateBar.Message = $"The {verb} is through. {r.Branch} is on svn/{r.Checkout}, {r.Ahead} commit(s) ahead.";
            _filter.Clear("Files in conflict");
            NoConflicts.Title = $"The {verb} is through";
            NoConflicts.Text = $"{r.Branch} is on svn/{r.Checkout}, {r.Ahead} commit(s) ahead.";
            EmptyContinue.Visibility = EmptyAbort.Visibility = Visibility.Collapsed;
            ShowEmpty(true);
            Diff.ShowText("", verb + " done");
            return;
        }
        Pane.Append(r.Stopped.Length > 0 ? $"it moved on and stopped again, on \"{r.Stopped}\"" : "it stopped again");
        if (r.Output.Length > 0) Pane.Append(r.Output);
        await LoadAsync();
    }

    async void Abort_Click(object sender, RoutedEventArgs e)
    {
        var s = _state;
        if (!await Dialogs.Confirm(this, "Abort the " + s.Verb, AbortCost(s) + "\n\nContinue?", "Abort " + s.Verb)) return;
        await Busy.During(sender, async () =>
        {
            var root = Session.Require();
            await Runner.Run(Pane, "abort the " + s.Verb, () => Conflicts.Abort(root, _worktree));
            await LoadAsync();
        });
    }

    /// <summary>No file in conflict: the sentence about what stopped is the whole page.</summary>
    void ShowEmpty(bool empty)
    {
        NoConflicts.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => LoadAsync());

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
