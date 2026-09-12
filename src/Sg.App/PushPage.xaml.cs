using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>Review what leaves the machine, then push. One SVN commit per working copy.</summary>
public sealed partial class PushPage : SgPage
{
    readonly string _worktree;
    readonly ListFilter _filter;
    bool _canPush;
    PushPreview? _preview;
    List<PushRepoRow> _repos = new();
    /// <summary>The last push went through. The empty branch it left behind is the success, not a warning.</summary>
    bool _justPushed;

    /// <summary>
    /// How much of the branch this push sends. Picking a commit in the list narrows it to the run that
    /// ends there; everything above waits for the next push.
    /// </summary>
    PushScope _scope = PushScope.Whole;

    /// <summary>Set while the code fills the list, so rebinding it does not read as the user picking a line.</summary>
    bool _binding;

    /// <summary>Which load the page is on, so a read that lands late does not draw over a newer one.</summary>
    int _generation;

    /// <summary>One preview per pause in the picking, not one per selection change.</summary>
    readonly DispatcherTimer _scopeDebounce = new() { Interval = TimeSpan.FromMilliseconds(140) };

    /// <summary>Every commit of the preview, newest first. The list on screen is these through the filter.</summary>
    List<CommitRow> _commitRows = new();
    /// <summary>Where the push cuts: the index in _commitRows of the last commit going. -1 before the first preview.</summary>
    int _cut = -1;
    string _commitsTitle = "Commits on the branch";
    readonly CommitFilter _commitFilter;

    public PushPage(string worktree)
    {
        InitializeComponent();
        _worktree = worktree;
        Title = "Push to SVN";
        Subtitle = worktree;
        ColumnSplitter.Attach(Splitter);
        Shortcuts.DiffNavigation(this, Diff);
        Shortcuts.Add(this, VirtualKey.Enter, VirtualKeyModifiers.Control, () => { if (PushButton.IsEnabled) _ = PushAsync(); });
        FileActions.Attach(Files, n => PathUtil.Join(_worktree, n.FullPath));
        _filter = new ListFilter(Filter, Files, FilesHeader, r => ((FileRow)r).Display, r => ((FileRow)r).Group);
        _filter.Picked += OnPicked;
        _filter.ExpectStats = true;
        _commitFilter = new CommitFilter(CommitFilterBox, this);
        _commitFilter.Changed += ShowCommits;
        _scopeDebounce.Tick += (_, _) => { _scopeDebounce.Stop(); _ = LoadAsync(); };
        Message.Minimum = Session.Root?.Config.MinMessageLength ?? 10;
        // The shared text is needed while any working copy still uses it, and each own text has the same rule.
        Message.Needed = () => _repos.Count == 0 || _repos.Any(r => !r.Custom);
        Message.ExtraOk = () => _repos.All(r => r.Ok);
        Message.Confirm = () =>
        {
            var where = string.Join(", ", _repos.Select(r => r.Repo + (r.Custom ? " (own message)" : "")));
            var p = _preview;
            var part = p is { Partial: true }
                ? $" It sends the oldest {p.Sending} of {p.Commits.Count} commits and leaves {p.Commits.Count - p.Sending} on the branch."
                : "";
            return $"This makes {p?.Groups.Count ?? 0} SVN commit(s) that everyone can see: {where}.{part} Continue?";
        };
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        var root = Session.Require();
        Session.Log.Sink = Pane;
        var gen = ++_generation;
        var scope = _scope;
        var first = _preview == null;
        if (first) { CommitsSkeleton.Show(); FilesSkeleton.Show(); }
        // Into a local, and published only once it is known to be both current and real. Assigned straight
        // into the field, a read that failed emptied it under a screen still showing a full preview, and an
        // older read that landed late left the field describing something the screen no longer showed.
        var read = await Runner.Quiet(Pane, () => Push.Preview(root, _worktree, scope));
        if (gen != _generation) return;
        CommitsSkeleton.Hide();
        FilesSkeleton.Hide();
        if (read == null)
        {
            ResultBar.Severity = InfoBarSeverity.Error;
            ResultBar.Message = "The branch could not be read. What is on screen is the last good read; the log says why.";
            ResultBar.IsOpen = true;
            _canPush = false;
            SyncPushButton();
            return;
        }
        _preview = read;
        var p = _preview;
        Checkout ??= p.Checkout;
        Branch ??= p.Branch;
        Subtitle = $"{p.Branch}  →  svn/{p.Checkout}   {_worktree}";
        Header.Text = p.Partial
            ? $"Sending {p.Sending} of {p.Commits.Count} commit(s), {p.Entries.Count()} file(s), {p.Groups.Count} SVN commit(s)"
            : $"{p.Commits.Count} commit(s), {p.Entries.Count()} file(s), {p.Groups.Count} SVN commit(s)";
        var warnings = new List<string>();
        if (p.Dirty) warnings.Add("The worktree has uncommitted changes. Commit or discard them first.");
        if (p.NeedsRebase) warnings.Add("The branch is behind the snapshot. Push rebases it first. A conflict stops the push.");
        warnings.AddRange(p.Problems);
        _justPushed = false;
        WarnBar.Message = string.Join("\n", warnings);
        WarnBar.IsOpen = warnings.Count > 0;
        // Push rebases anyway, so this is not a refusal. Doing it here means a conflict surfaces now,
        // rather than after the push has already synced and moved the checkout.
        WarnBar.ActionButton = p.NeedsRebase ? RebaseNowButton() : null;
        // Newest first, so the ones above the boundary are the ones that stay. The picked line is the
        // last one going, which puts the selection right on the edge between the two halves.
        _commitRows = p.Commits.Select((c, i) =>
        {
            var row = CommitRow.From(c, snapshot: false);
            row.Staying = i < p.Commits.Count - p.Sending;
            return row;
        }).ToList();
        _cut = _commitRows.Count == 0 ? -1 : p.Commits.Count - p.Sending;
        _commitsTitle = p.Partial
            ? $"Commits on the branch, sending the oldest {p.Sending}"
            : "Commits on the branch";
        ShowCommits();
        AllCommitsButton.IsEnabled = p.Partial;
        PartialBar.Message = p.Partial
            ? $"{p.Commits.Count - p.Sending} commit(s) stay on the branch, over the new snapshot, ready for the next push."
            : "";
        PartialBar.IsOpen = p.Partial;
        // A branch that equals the snapshot says so where its commits would be. After a push that is
        // the success, and the result bar above already said it.
        NothingToPush.Visibility = p.Commits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = p.Commits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _filter.SetItems(p.Groups.SelectMany(g => g.Entries.Select(e => new FileRow
        {
            Wc = g.Wc,
            Status = e.Status,
            Path = e.Path,
            OldPath = e.OldPath,
            Display = $"{(g.Wc.Length == 0 ? "root" : g.Wc)}  {e.Status}  {e.Path}" + (e.OldPath != null ? $"  (was {e.OldPath})" : ""),
        })).ToList(), "Files, one SVN commit per working copy");
        if (Message.Text.Trim().Length == 0) Message.Text = p.DefaultMessage;
        // The working copies the push commits to, in the order it commits them. A row that already
        // carries its own message keeps it across a refresh.
        var minimum = root.Config.MinMessageLength;
        _repos = p.Groups.Select((g, i) =>
        {
            var old = _repos.FirstOrDefault(r => r.Wc.Equals(g.Wc, StringComparison.OrdinalIgnoreCase));
            var row = new PushRepoRow { Wc = g.Wc, Files = g.Entries.Count, RepoColor = i, Minimum = minimum, Shared = () => Message.Text };
            if (old is { Custom: true }) { row.Message = old.Message; row.Custom = true; }
            row.Changed += SyncPushButton;
            return row;
        }).ToList();
        Repos.ItemsSource = _repos;
        ShowChecks(p);
        _canPush = p.Ready;
        SyncPushButton();
        if (p.Commits.Count == 0) { Diff.ShowText("", "nothing to push"); return; }

        // The numbers beside each row come from a read of counts alone, and nothing waits for them.
        _ = ShowCountsAsync(gen, p);

        // The patch of everything is only read when it is the thing on screen.
        if (_filter.HasPick) return;
        var title = $"all files, svn/{p.Checkout} → {p.Branch}, unified";
        Diff.BeginLoading(title);
        var patch = await Task.Run(() => root.Git.UnifiedDiff(p.Worktree, p.Base, p.Tip));
        if (gen == _generation && !_filter.HasPick) Diff.ShowUnified(patch, title);
    }

    async Task ShowCountsAsync(int gen, PushPreview p)
    {
        var counts = await Task.Run(() => Session.Require().Git.NumStat(p.Worktree, p.Base, p.Tip));
        if (gen == _generation) _filter.SetStats(counts);
    }

    /// <summary>
    /// A line picked in the commit list is the last commit this push sends. Everything under it goes
    /// with it, everything above it stays on the branch and is pushed next time.
    /// </summary>
    void Commits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || Commits.SelectedItem is not CommitRow picked) return;
        // Against every commit, not against the list on screen: with a filter on, the line under the
        // picked one may be ten commits down the branch, and all ten go with it.
        var index = _commitRows.IndexOf(picked);
        if (index < 0) return;
        var sending = _commitRows.Count - index;
        var scope = sending >= _commitRows.Count ? PushScope.Whole : PushScope.First(sending);
        if (scope == _scope) return;
        _scope = scope;
        // The preview is a git log, a git diff and an svn status over every path the branch changed.
        // Walking this list with the arrow keys used to start one of those per key press.
        _scopeDebounce.Stop();
        _scopeDebounce.Start();
    }

    /// <summary>
    /// The commits as the filter leaves them. The cut is a commit, not a line number, so it survives
    /// the filter: the last commit going is the picked line while it is on the list, and no line is
    /// picked while the filter hides it, which changes nothing about what the push sends.
    /// </summary>
    void ShowCommits()
    {
        var shown = _commitFilter.Apply(_commitRows);
        var cut = _cut >= 0 && _cut < _commitRows.Count ? _commitRows[_cut] : null;
        _binding = true;
        Commits.ItemsSource = shown;
        Commits.SelectedItem = cut != null && shown.Contains(cut) ? cut : null;
        _binding = false;
        CommitsHeader.Text = _commitFilter.Active
            ? _commitsTitle + ", " + CommitFilter.Showing(shown.Count, _commitRows.Count)
            : _commitsTitle;
    }

    async void AllCommits_Click(object sender, RoutedEventArgs e)
    {
        if (!_scope.Partial) return;
        _scope = PushScope.Whole;
        await Busy.During(sender, () => LoadAsync());
    }

    async void OnPicked(TreeNode node)
    {
        if (_preview == null) return;
        var git = Session.Require().Git;
        var p = _preview;
        if (node.Row is not FileRow row)
        {
            // A folder, or a whole working copy: everything the push sends under it, as one patch.
            var folder = node.FullPath;
            var title = $"{(folder.Length == 0 ? "root" : folder)}   {node.FileCount} file(s), svn/{p.Checkout} → {p.Branch}, unified";
            Diff.BeginLoading(title);
            var patch = await Task.Run(() => git.UnifiedDiff(p.Worktree, p.Base, p.Tip, folder.Length == 0 ? null : folder));
            if (_filter.IsCurrent(node) && ReferenceEquals(_preview, p)) Diff.ShowUnified(patch, title);
            return;
        }
        await Diff.ShowFileAsync(row.Path, $"{row.Path}   svn/{p.Checkout} → {p.Branch}", new DiffView.Reads(
                () => row.Status == 'A' ? "" : git.ShowText(p.Base, row.OldPath ?? row.Path),
                () => row.Status == 'D' ? "" : git.ShowText(p.Tip, row.Path),
                () => git.UnifiedDiff(p.Worktree, p.Base, p.Tip, row.Path)),
            () => _filter.IsCurrent(node));
    }

    /// <summary>
    /// All six passing is one line. Any failing one names itself, the paths that cause it, and the
    /// thing that clears it where this page can run that thing.
    /// </summary>
    void ShowChecks(PushPreview p)
    {
        var failed = p.Checks.Where(c => !c.Ok).ToList();
        Checks.ItemsSource = failed.Select(c =>
        {
            var row = new CheckRow { Name = c.Name + ":", Detail = c.Detail };
            switch (c.Id)
            {
                case PushChecks.Clean:
                    row.Fix = PushFix.Commit;
                    row.ActionText = "Commit";
                    row.ActionGlyph = "";
                    row.ActionTip = "Commit what is uncommitted to the branch, then read the push again. Nothing goes to SVN here.";
                    break;
                case PushChecks.Rebasing:
                    row.Fix = PushFix.Resolve;
                    row.ActionText = "Resolve";
                    row.ActionGlyph = "";
                    row.ActionTip = "Open the resolver and finish or abort the rebase that stopped, then read the push again.";
                    break;
                case PushChecks.Collisions:
                    row.Paths = c.Paths;
                    row.Fix = PushFix.Shelve;
                    row.ActionText = "Shelve them";
                    row.ActionGlyph = "\uE7B8";
                    row.ActionTip = "Take the checkout's edits on exactly these files out of the way and keep them. "
                                    + "Nothing is lost: put them back from Shelved changes once the push is through.";
                    row.SecondFix = PushFix.CheckoutChanges;
                    row.SecondText = "Read them";
                    row.SecondGlyph = "\uE70F";
                    row.SecondTip = "Open the edits made directly in the checkout, so you can commit or discard the ones that collide.";
                    break;
            }
            return row;
        }).ToList();
        ChecksHeader.Text = failed.Count == 0
            ? $"Ready to push, all {p.Checks.Count} checks pass"
            : $"{failed.Count} of {p.Checks.Count} checks stop this push";
        ChecksIcon.Glyph = failed.Count == 0 ? "" : "";
        var brush = failed.Count == 0 ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush";
        ChecksIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush];
    }

    /// <summary>
    /// The button never explains a refusal; it is simply off until the push can run. The dialog's own
    /// button follows that and the messages: the shared one, if any working copy still uses it, and
    /// each one of their own.
    /// </summary>
    void SyncPushButton()
    {
        PushButton.IsEnabled = _canPush;
        // The same checks gate both: what stops a push from writing stops an apply from writing too.
        // Only the item is disabled, never the whole button: the menu still opens on the shelf.
        ApplyItem.IsEnabled = _canPush;
        Message.Ready = _canPush;
    }

    /// <summary>
    /// What is already put aside from this branch. It is in the push button's menu because the shelf is
    /// the other place a change can be instead of going to SVN, and because a push that was unblocked by
    /// shelving something needs the way back to it from here.
    /// </summary>
    void Shelf_Click(object sender, RoutedEventArgs e)
    {
        var branch = Branch;
        Go(() => new ShelfPage(null, _worktree, branch) { Checkout = Checkout, Branch = branch }, "shelf:" + _worktree);
    }

    /// <summary>
    /// The same write as a push, stopping before the server. No message is asked for, because nothing
    /// is committed, and the branch stays where it is: this is the way round when a change wants
    /// reading in the checkout, or a commit made by hand, before it goes.
    /// </summary>
    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var p = _preview;
        if (p == null) return;
        var where = string.Join(", ", p.Groups.Select(g => g.Wc.Length == 0 ? "root" : g.Wc));
        var what = p.Partial ? $"the oldest {p.Sending} of {p.Commits.Count} commits" : "the branch";
        if (!await Dialogs.Confirm(this, "Apply without committing",
            $"Write {what} into {p.Checkout} ({where}) and stop there? Nothing goes to the server, and {p.Branch} does not move. "
            + "They land as local changes to read and commit yourself.", "Apply")) return;

        var root = Session.Require();
        var scope = _scope;
        await Busy.During(PushButton, async () =>
        {
            ResultBar.IsOpen = false;
            var r = await Runner.Run(Pane, "apply",
                () => Push.Run(root, _worktree, null, interactive: true, scope: scope, finish: PushFinish.LeaveInCheckout));
            if (r == null)
            {
                ResultBar.Severity = InfoBarSeverity.Error;
                ResultBar.Message = "Nothing was applied. See the log.";
                ResultBar.IsOpen = true;
                SyncPushButton();
                return;
            }
            foreach (var g in r.Groups)
                Pane.Append($"  {(g.Wc.Length == 0 ? "root" : g.Wc),-30} {g.State}" + (g.Error != null ? "  " + g.Error.Split('\n')[0] : ""));
            var ok = r.Groups.All(g => g.State == "applied");
            ResultBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultBar.Message = ok
                ? $"Written into {r.Checkout}. Nothing went to the server and {r.BranchState}."
                : "Nothing was applied: a working copy refused it, and what had been written was put back.";
            ResultBar.ActionButton = ok ? ChangesButton() : null;
            ResultBar.IsOpen = true;
            await LoadAsync();
        }, restoreEnabled: false);
    }

    /// <summary>Where what was written is read and sent: the same window an edit made by hand goes out through.</summary>
    Button ChangesButton()
    {
        var b = new Button { Content = "Open the checkout changes" };
        ToolTipService.SetToolTip(b, "See what was written, file by file, and commit it to SVN when it is right.");
        b.Click += (_, _) =>
        {
            var co = Session.Root?.Checkout(_preview!.Checkout);
            if (co != null) Go(() => new SvnCommitPage(co) { Checkout = co.Name }, "changes:" + co.Name);
        };
        return b;
    }

    /// <summary>
    /// Runs the thing a failed check asked for, then reads the branch again in place. The page the
    /// user opened to push in is the page the push gets unblocked in.
    /// </summary>
    void Fix_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CheckRow row) RunFix(row.Fix, row, sender);
    }

    /// <summary>The quieter of the two buttons a check can carry. The same fixes, picked differently.</summary>
    void Fix2_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CheckRow row) RunFix(row.SecondFix, row, sender);
    }

    async void RunFix(PushFix fix, CheckRow row, object sender)
    {
        var root = Session.Require();
        switch (fix)
        {
            case PushFix.Commit:
                GoThen(() => new CommitPage(_worktree) { Checkout = Checkout, Branch = Branch }, "commit:" + _worktree, () => _ = LoadAsync());
                break;
            case PushFix.Resolve:
                GoThen(() => new ConflictPage(_worktree) { Checkout = Checkout, Branch = Branch }, "resolve:" + _worktree, () => _ = LoadAsync());
                break;
            case PushFix.CheckoutChanges:
                if (_preview != null)
                {
                    var co = root.Checkout(_preview.Checkout);
                    GoThen(() => new SvnCommitPage(co) { Checkout = Checkout }, "changes:" + co.Name, () => _ = LoadAsync());
                }
                break;
            case PushFix.Rebase:
                await Busy.During(sender, async () =>
                {
                    var r = await Runner.Run(Pane, "rebase", () => Ops.Rebase(root, _worktree));
                    // A rebase that stopped leaves the branch mid-flight; the resolver is the way on.
                    if (r is { Ok: false }) GoThen(() => new ConflictPage(_worktree) { Checkout = Checkout, Branch = Branch }, "resolve:" + _worktree, () => _ = LoadAsync());
                    else await LoadAsync();
                });
                break;
            case PushFix.Shelve:
                await Busy.During(sender, () => ShelveCollisionsAsync(row.Paths));
                break;
        }
    }

    /// <summary>
    /// The checkout's own edits on the files this push wants to write, put aside. That is the whole of
    /// the collision: the branch's version goes to SVN, and the edits come back afterwards, merged into
    /// what the push left behind. Only the colliding paths go, so the rest of the checkout is untouched.
    /// </summary>
    async Task ShelveCollisionsAsync(List<string> paths)
    {
        if (_preview == null || paths.Count == 0) return;
        var root = Session.Require();
        var co = root.Checkout(_preview.Checkout);
        var what = paths.Count == 1
            ? "Put the checkout's own edit to " + paths[0] + " aside, so this push can write that file."
            : $"Put the checkout's own edits to {paths.Count} file(s) aside, so this push can write them.";
        var r = await ShelfActions.SaveAsync(this, Pane, co.Path, paths, what, "before pushing " + Branch);
        if (r == null) return;
        ResultBar.Severity = InfoBarSeverity.Success;
        ResultBar.Message = $"{r.Shelf.Count} file(s) are on the shelf as \"{r.Shelf.Title}\". "
                            + "Push now, then put them back from Shelved changes on the checkout card.";
        ResultBar.IsOpen = true;
        await LoadAsync();
    }

    /// <summary>The left half of the split button. Its right half drops the menu and never gets here.</summary>
    async void Push_Click(SplitButton sender, SplitButtonClickEventArgs e) => await PushAsync();

    async Task PushAsync()
    {
        if (!await Message.AskAsync()) return;
        var root = Session.Require();
        var msg = Message.Clean;
        var own = _repos.Where(r => r.Custom).ToDictionary(r => r.Wc, r => r.Message, StringComparer.OrdinalIgnoreCase);
        var scope = _scope;
        await Busy.During(PushButton, async () =>
        {
            ResultBar.IsOpen = false;
            var r = await Runner.Run(Pane, "push", () => Push.Run(root, _worktree, msg, interactive: true, messageFor: own, scope: scope));
            if (r == null)
            {
                ResultBar.Severity = InfoBarSeverity.Error;
                ResultBar.Message = "The push did not run. See the log.";
                ResultBar.IsOpen = true;
                SyncPushButton();
                return;
            }
            foreach (var g in r.Groups)
                Pane.Append($"  {(g.Wc.Length == 0 ? "root" : g.Wc),-30} {g.State,-10}" + (g.Revision.HasValue ? $" r{g.Revision}" : "") + (g.Error != null ? "  " + g.Error.Split('\n')[0] : ""));
            foreach (var w in r.Warnings) Pane.Append("warning: " + w);
            // Three outcomes, not two: everything went, the part that was picked went and the rest is
            // waiting on purpose, or something failed. Only the last of those is an error.
            var onPurpose = scope.Partial && r.Groups.All(g => g.State == "committed");
            ResultBar.Severity = r.AllCommitted || onPurpose ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultBar.Message = r.AllCommitted
                ? $"Pushed. {r.Branch} now equals svn/{r.Checkout} at r{r.Revision}."
                : onPurpose
                    ? $"Pushed what was picked, at r{r.Revision}. {r.BranchState}, ready for the next push."
                    : $"Partly pushed. {r.BranchState}. Fix the problem and push again.";
            ResultBar.IsOpen = true;
            _justPushed = r.AllCommitted;
            // The next push starts from the whole of what is left, rather than silently keeping a count
            // that meant something about a branch that has changed under it.
            _scope = PushScope.Whole;
            // What went is offered again from the list of recent messages, and the box starts empty, so
            // LoadAsync can fill it from the commits that are left. Both commit pages already do this.
            if (r.AllCommitted || onPurpose) { MessageDialog.Remember(msg); Message.Text = ""; }
            if (!WindowHelper.IsForeground(this))
                Notifications.Show(r.AllCommitted || onPurpose ? "Push done" : "Push stopped half way", ResultBar.Message,
                    Notifications.Action("log", ("path", _worktree)),
                    new Notifications.ToastButton("Open the log", Notifications.Action("log", ("path", _worktree))));
            await LoadAsync();
        }, restoreEnabled: false);
    }

    Button RebaseNowButton()
    {
        var b = new Button { Content = "Rebase now", Tag = new CheckRow { Fix = PushFix.Rebase } };
        ToolTipService.SetToolTip(b, "Put the branch on the latest snapshot now. The push does it anyway; doing it here shows a conflict before the push starts writing.");
        b.Click += Fix_Click;
        return b;
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => LoadAsync());

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
