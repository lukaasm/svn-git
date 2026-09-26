using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// Work that stopped on conflicts, whatever stopped it: a pull onto a newer snapshot, an import
/// replaying a branch that came in a file, or a pull or restore from the backup. All of them leave the
/// same thing behind, so this is one page with one set of buttons, in the words VS Code and the git
/// tools use: accept current or incoming for each file, or edit it and mark it as resolved, then
/// Continue - or Skip the commit it stopped on, or Abort and put it all back.
///
/// It says each fact once. The banner says what stopped, where and on which commit; each file's row
/// says how it conflicts; the diff shows what differs. Everything only ever looked at - the commit
/// being replayed, what is staged for it so far - waits in More.
/// </summary>
public sealed partial class ConflictPage : SgPage
{
    readonly string _worktree;
    readonly ListFilter _filter;
    readonly ReviewLayout _layout;

    /// <summary>What the last read found. The buttons word themselves from it.</summary>
    ConflictState _state = new();

    public ConflictPage(string worktree)
    {
        InitializeComponent();
        _worktree = worktree;
        Title = "Resolve conflicts";
        Subtitle = worktree;
        _layout = new ReviewLayout(this, Body, FilesPane, DiffPane, Splitter, CompactViews);
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
        Title = Operation(state);
        NameButtons(state);
        ShowPreview(null);

        // Three shapes, and only the first is a conflict. A stuck step has no sides to pick between,
        // so what it lists is whatever a forced apply left in the worktree for a hand to finish.
        var rows = state.Conflicted.Count > 0
            ? state.Conflicted.Select(p => new FileRow
            {
                Status = 'C', Path = p, Display = "C  " + p,
                Note = Conflicts.Describe(state.Codes.GetValueOrDefault(p, "")),
            }).ToList()
            : state.ByHand.Select(p => new FileRow { Status = Mark(p), Path = p, Display = Mark(p) + "  " + p }).ToList();
        _filter.SetItems(rows, state.Conflicted.Count > 0 ? "Conflicts" : "Files to finish by hand");
        ShowEmpty(rows.Count == 0);

        var staged = state.ResolutionReviewFiles.Count;
        ReviewResultButton.Visibility = staged > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReviewResultButton.Text = $"Review staged changes ({staged})";
        InspectButton.Visibility = state.InProgress && !state.Finalizing ? Visibility.Visible : Visibility.Collapsed;
        ContinueButton.Visibility = state.InProgress && !state.Stuck ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = AbortButton.Visibility = state.InProgress && !state.Finalizing ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Style = state.Stuck ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
        ForceButton.Visibility = state.Stuck && state.Kind == Replay.Import ? Visibility.Visible : Visibility.Collapsed;
        // The resolver settles files at three stages and nothing else. Going on from here it can also
        // continue and skip, so it is offered whenever something is stopped, except an import stuck
        // on a patch that will not go in: forcing what fits of one is a choice for a hand.
        AutoButton.IsEnabled = state.Conflicted.Count > 0;
        AutoAllButton.Visibility = state.Conflicted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoAllButton.IsEnabled = state.InProgress && !(state.Stuck && state.Kind == Replay.Import);

        var at = At(state);
        if (!state.InProgress)
        {
            Say(InfoBarSeverity.Success, "Nothing is paused here", "The branch is in a normal state. There is nothing to resolve.");
            NoConflicts.Title = "Nothing is paused here";
            NoConflicts.Text = "The branch is in a normal state. There is nothing to resolve.";
            ContinueButton.IsEnabled = SkipButton.IsEnabled = AbortButton.IsEnabled = false;
        }
        else if (state.Finalizing)
        {
            Say(InfoBarSeverity.Informational, "The commits are applied", "Recover the saved local edits to finish.");
            NoConflicts.Title = "Recover local edits";
            NoConflicts.Text = "The commits are applied. Recover the saved local edits to finish.";
            ContinueButton.Text = "Recover local edits";
            ContinueButton.IsEnabled = true;
        }
        else if (state.Stuck)
        {
            // Never "every file is resolved" here. Continue can only fail, so it is not offered, and the
            // two things that do work are the ones on screen.
            var out_ = state.Kind == Replay.Import
                ? "Skip it and the rest of the series still lands. Or, in More, apply what fits of it by hand: "
                  + "the hunks that land are written, the ones that do not are left beside their file as .rej, "
                  + "and you finish them and mark them as resolved."
                : "Skip it, and the ones after it still replay.";
            Say(InfoBarSeverity.Warning, Headline(state), Stopped(state) + Conflicts.StuckNote(state.Kind) + " " + out_);
            NoConflicts.Title = state.Kind == Replay.Import ? "Patch needs attention" : "Empty commit";
            NoConflicts.Text = Conflicts.StuckNote(state.Kind) + " " + out_;
            SkipButton.IsEnabled = AbortButton.IsEnabled = true;
        }
        else if (rows.Count == 0)
        {
            Say(InfoBarSeverity.Informational, "All conflicts resolved", Stopped(state) + $"Continue to commit it{at}. The next commit may stop again.");
            NoConflicts.Title = "All conflicts resolved";
            NoConflicts.Text = $"Continue to commit it{at}. The next commit may stop again.";
            ContinueButton.IsEnabled = SkipButton.IsEnabled = AbortButton.IsEnabled = true;
        }
        else if (state.Conflicted.Count > 0)
        {
            // The buttons over the list say how; the banner only says what is left before Continue.
            Say(InfoBarSeverity.Warning, Headline(state), Stopped(state)
                + (state.Conflicted.Count == 1 ? "Resolve the file in conflict, then Continue." : $"Resolve the {state.Conflicted.Count} files in conflict, then Continue."));
            ContinueButton.IsEnabled = false;
            SkipButton.IsEnabled = AbortButton.IsEnabled = true;
        }
        else
        {
            Say(InfoBarSeverity.Warning, Headline(state), Stopped(state) + $"{rows.Count} file(s) need finishing by hand. Every .rej file holds the "
                + "hunks that would not go in: put them in, delete the .rej, then mark the file as resolved and Continue.");
            ContinueButton.IsEnabled = SkipButton.IsEnabled = AbortButton.IsEnabled = true;
        }
        TaskGate.SetHelp(ContinueButton, ContinueButton.IsEnabled
            ? $"Commit this step and go on with the rest of the {Verb(state)}. The next commit may stop again. The resolutions are yours to read first: Review staged changes, in More."
            : state.Conflicted.Count > 0 ? $"Resolve every file first: {state.Conflicted.Count} left." : "Nothing is paused here.");

        // Opening the page is opening the first conflict: with one file, there is nothing else to pick.
        var reopened = reopen != null && _filter.Select(r => r is FileRow f && f.Path.Equals(reopen, StringComparison.OrdinalIgnoreCase));
        if (!reopened && rows.Count > 0) _filter.SelectFirstFile();
        if (Files.SelectedItem == null)
        {
            _shownPath = null;
            Diff.ShowText("", rows.Count == 0 ? "nothing in conflict" : "");
        }
    }

    void Say(InfoBarSeverity severity, string title, string message)
    {
        StateBar.Severity = severity;
        StateBar.Title = title;
        StateBar.Message = message;
    }

    /// <summary>The letter a by-hand row carries: R for what a forced apply could not put in, M for the rest.</summary>
    static char Mark(string path) => path.EndsWith(".rej", StringComparison.OrdinalIgnoreCase) ? 'R' : 'M';

    /// <summary>
    /// What the stopped work is called: the same name as the button that started it. A pull onto a new
    /// snapshot is the worktree card's Pull, a pull or restore from the backup is the backup's, and an
    /// import is an import.
    /// </summary>
    static string Operation(ConflictState s) => s.BackupName != null ? Backup.ReplayTitle(s.BackupPull)
        : s.Kind == Replay.Import ? "Import branch"
        : "Pull from " + s.Server;

    static string Verb(ConflictState s) => s.BackupName != null ? (s.BackupPull ? "pull" : "restore") : s.Kind == Replay.Import ? "import" : "pull";

    /// <summary>The banner's title: what stopped, and where it got to.</summary>
    static string Headline(ConflictState s)
    {
        var what = s.Kind == Replay.Import && s.BackupName == null ? "patch" : "commit";
        return Operation(s) + " stopped" + (s.Of > 0 ? $" at {what} {s.At} of {s.Of}" : "");
    }

    /// <summary>The commit it stopped on, as the banner's message starts. Empty when git does not say.</summary>
    static string Stopped(ConflictState s) => s.Stopped.Length > 0 ? $"\"{s.Stopped}\". " : "";

    static string At(ConflictState s) => s.Of > 0 ? $" ({s.At} of {s.Of})" : "";

    /// <summary>
    /// The two sides are called current and incoming everywhere, as VS Code calls them for a merge and
    /// a rebase alike. What each one is depends on what stopped, and the tooltips say that.
    /// </summary>
    void NameButtons(ConflictState s)
    {
        var what = s.Kind == Replay.Import && s.BackupName == null ? "patch" : "commit";
        ContinueButton.Text = "Continue";
        SkipButton.Text = s.Stuck ? $"Skip empty {what}" : $"Skip {what}";
        InspectButton.Text = $"View {what}";
        ToolTipService.SetToolTip(TakeOurs, $"Keep the current version - the {s.OursLabel}, on the left in the diff - for the ticked files, or the one selected. "
                                            + "Where the current side deleted the file, accepting it deletes the file.");
        ToolTipService.SetToolTip(TakeTheirs, $"Keep the incoming version - the {s.TheirsLabel}, on the right in the diff - for the ticked files, or the one selected. "
                                              + "Where the incoming side deleted the file, accepting it deletes the file.");
        ToolTipService.SetToolTip(SkipButton, $"Drop the {what} this stopped on and go on with the ones after it. Asks first.");
        ToolTipService.SetToolTip(AbortButton, AbortCost(s));
        ToolTipService.SetToolTip(LeaveButton, s.InProgress
            ? $"The {Verb(s)} stays paused, with every resolution made so far. The worktree's card offers Resolve to come back."
            : "Back to where you came from.");
        // The pairs to compare keep their place across reloads. Refilling the box fires its change event
        // with nothing new to show, so that is told apart from a person picking a pair.
        var keep = Compare.SelectedIndex < 0 ? 0 : Compare.SelectedIndex;
        _naming = true;
        try
        {
            Compare.ItemsSource = new[]
            {
                "Current  ↔  Incoming",
                "Base  →  Current   (what the current side changed)",
                "Base  →  Incoming   (what the incoming side changed)",
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

    /// <summary>
    /// What abort costs, which is not the same for each. A pull gives the branch back exactly as it
    /// was. An import is one series to git, so undoing it takes off every commit that went in, not
    /// only the one that stopped - and the file it came from is what still holds them.
    /// </summary>
    static string AbortCost(ConflictState s) => s.BackupName != null
        ? $"Stop the {Verb(s)} and undo every commit it applied. The backup stays as it is, with its uncommitted edits. Asks first."
        : s.Kind == Replay.Import
        ? "Take the whole import back off the branch, the commits that already went in included. "
          + "git undoes a patch series as one thing. The export file still holds every commit, so this can be run again. Asks first."
        : "Put the branch back where this pull began. Resolutions made during it are dropped. Asks first.";

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
            Diff.ShowText($"{node.FileCount} file(s) in conflict under {node.FullPath}.\n\nTick the folder to accept one side for all of them, or select one file to see its two versions.", node.FullPath);
            return;
        }
        _shownPath = row.Path;
        _shownNode = node;
        _layout.ShowDetails();
        var git = Session.Require().Git;
        // A file at three stages has two versions to compare, and a third both started from. One a
        // forced apply left behind has only itself, so it is read against what the branch holds -
        // which is the change, half put in.
        var conflict = _state.Conflicted.Any(p => p.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
        var (left, right) = Stages();
        var reads = conflict
            ? new DiffView.Reads(() => git.ShowStage(_worktree, left, row.Path), () => git.ShowStage(_worktree, right, row.Path))
            : new DiffView.Reads(() => git.ShowTextIn(_worktree, "HEAD", row.Path), () => OnDisk(row.Path));
        var name = (int stage) => stage == 1 ? "base" : stage == 2 ? "current" : "incoming";
        // Short, because the header shares its line with the diff's own toggles: the order of the two
        // names is the order of the two sides, and the row's note says the rest.
        var note = conflict && row.Note.Length > 0 ? " · " + row.Note : "";
        var title = conflict
            ? $"{row.Path}   {name(left)} → {name(right)}{note}"
            : $"{row.Path}   branch → on disk now";
        await Diff.ShowFileAsync(row.Path, title, reads, () => _filter.IsCurrent(node),
            binaryNote: conflict ? "binary file, accept one side: " : "binary file: ");
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

    async Task TakeAsync(bool ours)
    {
        var paths = Picked();
        if (paths.Count == 0) { await Dialogs.Info(this, "Nothing selected", "Select a file, or tick several, first."); return; }
        var root = Session.Require();
        await Runner.Run(Pane, $"accept {(ours ? "current" : "incoming")} for {paths.Count} file(s)", () => root.Git.TakeSide(_worktree, paths, ours));
        await LoadAsync();
    }

    async void TakeOurs_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => TakeAsync(ours: true));
    async void TakeTheirs_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => TakeAsync(ours: false));

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
        if (paths.Count == 0) { await Dialogs.Info(this, "Nothing selected", "Select a file, or tick several, first."); return; }
        var root = Session.Require();
        await Runner.Run(Pane, "mark as resolved", () => root.Git.MarkResolved(_worktree, paths));
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
        await LoadAsync();
        if (r != null) ShowStep(r);
    }, restoreEnabled: false);

    /// <summary>What one run of the resolver did, where the reader is looking.</summary>
    void ShowStep(AutoResolveResult r)
    {
        foreach (var p in r.Resolved) Pane.Append("settled: " + p);
        foreach (var l in r.Left) Pane.Append("left: " + l.Path + "  (" + l.Why + ")");
        if (r.AllResolved)
            Say(InfoBarSeverity.Informational, $"The resolver settled {r.Resolved.Count} file(s)",
                "They are marked as resolved. Read them in the diff, then Continue.");
        else
            Say(InfoBarSeverity.Warning, $"The resolver settled {r.Resolved.Count} file(s) and left {r.Left.Count}",
                string.Join("; ", r.Left.Select(l => l.Path + " - " + l.Why)) + ". Accept a side for those, or edit them and mark them as resolved.");
    }

    /// <summary>
    /// From here to the end, or to the first file the resolver cannot settle: settle, continue, settle
    /// the next commit's files. Every stop it went through is a commit on the branch by the time this
    /// returns, so it asks first; the log pane says what it did at each one.
    /// </summary>
    async void AutoAll_Click(object sender, RoutedEventArgs e)
    {
        var s = _state;
        var what = s.Kind == Replay.Import && s.BackupName == null ? "patch" : "commit";
        var left = s.Of > 0 ? $" {s.Of - s.At + 1} {what}(s) are left to replay, this one included." : "";
        if (!await Dialogs.Confirm(this, "Resolve remaining commits with AI",
                $"Let the resolver settle every stop from here on?{left}\n\n"
                + $"It settles the files, the {Verb(s)} continues, and the next {what} that stops is handed over too, until it is through "
                + $"or a file comes back that it could not settle. A {what} left with nothing to commit is skipped. "
                + "Each stop it goes through becomes a commit on the branch; the log below says what it did at every one, "
                + "and the log of the branch shows the result.",
                "Go"))
            return;
        await Busy.During(MoreButton, async () =>
        {
            var root = Session.Require();
            var verb = Verb(s);
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
            else Say(InfoBarSeverity.Warning, "The resolver stopped short", run.Why);
        }, restoreEnabled: false);
    }

    // sender, not ContinueButton: the empty state has no button of its own, but the ring must spin on
    // whichever was pressed.
    async void Continue_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, async () =>
    {
        var root = Session.Require();
        var verb = Verb(_state);
        var r = await Runner.Run(Pane, "continue the " + verb, () => Conflicts.Continue(root, _worktree));
        await AfterStep(r, verb);
    }, restoreEnabled: false);

    /// <summary>What is staged for the commit so far, resolutions included: the thing to read before Continue.</summary>
    async void ReviewResult_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Require();
        var generation = _generation;
        var diff = await Runner.Quiet(Pane, () => root.Git.Out(_worktree, "diff", "--cached", "--no-ext-diff"));
        if (diff == null || generation != _generation) return;
        ShowPreview(diff, "staged changes, as Continue would commit them");
    }

    /// <summary>The commit, or the patch, this stopped on, as it came.</summary>
    async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        var generation = _generation;
        var patch = await Runner.Quiet(Pane, () => _state.Kind == Replay.Import
            ? Session.Require().Git.Ok(_worktree, "am", "--show-current-patch=diff").StdOut
            : Session.Require().Git.Ok(_worktree, "show", "--format=fuller", "--no-ext-diff", "REBASE_HEAD").StdOut);
        if (patch == null || generation != _generation) return;
        ShowPreview(patch, InspectButton.Text.ToLowerInvariant());
    }

    /// <summary>A read-only patch over the files and the diff, or back to them when text is null.</summary>
    void ShowPreview(string? text, string title = "")
    {
        var showing = text != null;
        PatchPreview.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        BackToConflicts.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        if (showing)
        {
            NoConflicts.Visibility = Filled.Visibility = Visibility.Collapsed;
            PatchPreview.ShowText(text!, title);
        }
        else ShowEmpty(_filter.Count == 0);
    }

    void BackToConflicts_Click(object sender, RoutedEventArgs e) => ShowPreview(null);

    async void Skip_Click(object sender, RoutedEventArgs e)
    {
        var s = _state;
        var what = s.Kind == Replay.Import && s.BackupName == null ? "patch" : "commit";
        var named = s.Stopped.Length > 0 ? $"\"{s.Stopped}\"" : "the one it stopped on";
        if (!await Dialogs.Confirm(this, $"Skip this {what}",
                $"Drop {named} and go on with the remaining commits?\n\n"
                + "This drops the current step and any resolution made for it. The remaining commits will still be replayed.",
                $"Skip {what}"))
            return;
        await Busy.During(sender, async () =>
        {
            var root = Session.Require();
            var verb = Verb(s);
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
        if (!await Dialogs.Confirm(this, "Apply patch manually",
                $"Write as much of {named} into the worktree as still fits?\n\n"
                + "Every hunk that does not fit is written beside its own file as a .rej file, to put in by hand. "
                + "Nothing is committed, and nothing is staged until you mark it as resolved.",
                "Apply what fits"))
            return;
        await Busy.During(MoreButton, async () =>
        {
            var root = Session.Require();
            var r = await Runner.Run(Pane, "force what fits", () => Conflicts.ApplyWhatFits(root, _worktree));
            await LoadAsync();
            if (r != null)
                Say(r.Rejected.Count == 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Warning,
                    r.Rejected.Count == 0 ? "All of it fitted" : $"{r.Rejected.Count} file(s) did not take the whole patch",
                    r.Rejected.Count == 0
                        ? $"{r.Applied.Count} file(s) are changed in the worktree. Mark them as resolved, then Continue."
                        : $"{r.Applied.Count} file(s) took it; {string.Join(", ", r.Rejected)} did not. "
                          + "Each of those has a .rej file beside it holding the refused hunks. Put them in, delete the .rej, then mark the file as resolved.");
        });
    }

    /// <summary>
    /// What continue and skip both do with their answer. Through means the page has nothing left to
    /// show; stopped again is normal, and the reload puts the next set of files in the list.
    /// </summary>
    async Task AfterStep(ResolveResult? r, string verb)
    {
        if (r == null) { await LoadAsync(); return; }
        if (r.Operation is { Terminal: false })
        {
            Go(() => new UpdateBranchPage(_worktree), "update-branch:" + _worktree);
            return;
        }
        if (r.Ok)
        {
            var title = $"The {verb} is done";
            var detail = $"{r.Branch} is on svn/{r.Checkout}, {r.Ahead} commit(s) ahead.";
            var severity = InfoBarSeverity.Success;
            if (r.Backup != null)
            {
                var outcome = TaskResults.Describe(r.Backup);
                severity = outcome.State == TaskState.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                detail = outcome.Detail;
                if (r.Backup.WipShelf != null && !r.Backup.WipWritten)
                {
                    title = $"The {verb} is done; local edits need attention";
                    ReviewEditsButton.Visibility = Visibility.Visible;
                }
            }
            Say(severity, title, detail);
            _filter.Clear("Conflicts");
            NoConflicts.Title = title;
            NoConflicts.Text = detail;
            ContinueButton.Visibility = SkipButton.Visibility = AbortButton.Visibility = MoreButton.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(LeaveButton, "Back to where you came from.");
            PatchPreview.Visibility = BackToConflicts.Visibility = Visibility.Collapsed;
            ShowEmpty(true);
            Diff.ShowText("", verb + " done");
            return;
        }
        Pane.Append(r.Stopped.Length > 0 ? $"it moved on and stopped again, on \"{r.Stopped}\"" : "it stopped again");
        if (r.Output.Length > 0) Pane.Append(r.Output);
        await LoadAsync();
    }

    void ReviewEdits_Click(object sender, RoutedEventArgs e) =>
        Go(() => new ShelfPage(worktree: _worktree, branch: Branch) { Checkout = Checkout, Branch = Branch }, "shelves:" + _worktree);

    async void Abort_Click(object sender, RoutedEventArgs e)
    {
        var s = _state;
        if (!await Dialogs.Confirm(this, $"Abort the {Verb(s)}", AbortCost(s).Replace(" Asks first.", ""), "Abort")) return;
        await Busy.During(sender, async () =>
        {
            var root = Session.Require();
            await Runner.Run(Pane, "abort the " + Verb(s), () => Conflicts.Abort(root, _worktree));
            await LoadAsync();
        });
    }

    /// <summary>No file in conflict: the sentence about what stopped is the whole page.</summary>
    void ShowEmpty(bool empty)
    {
        NoConflicts.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Busy.During(MoreButton, () => LoadAsync());

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
