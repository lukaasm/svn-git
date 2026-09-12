using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;

namespace Sg.App;

/// <summary>The branch's commits and the snapshots below them. Details and files under the list, the diff on the right.</summary>
public sealed partial class LogPage : SgPage
{
    readonly string _worktree;
    readonly bool _autoSelect;
    readonly ListFilter _filter;
    string _currentSha = "";

    public LogPage(string worktree, bool autoSelect = false)
    {
        InitializeComponent();
        _worktree = worktree;
        _autoSelect = autoSelect;
        Title = "Log";
        Subtitle = worktree;
        ColumnSplitter.Attach(Splitter);
        Shortcuts.DiffNavigation(this, Diff);
        // Blame belongs here as much as in the commit window: this is where a file is read rather than
        // changed, and "who wrote this line" is a question you ask while reading.
        FileActions.Attach(Files, n => PathUtil.Join(_worktree, n.FullPath), (menu, node) =>
        {
            if (node.Row is not FileRow row) return;
            var item = new MenuFlyoutItem { Text = "Blame", Icon = new FontIcon { Glyph = "\uE7B3" } };
            ToolTipService.SetToolTip(item, "Who last changed each line: the SVN revision for the lines that came in with a snapshot, and the branch's own commits for the rest.");
            item.Click += (_, _) => Go(() => new BlamePage(row.Path, _worktree, null) { Checkout = Checkout, Branch = Branch }, "blame:" + row.Path);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(item);
        });
        // The same three the buttons under the list offer, on the rows they act on. The buttons say what
        // is possible and stay where the eye can find them; the menu is for the hand that is already on
        // the commit. Both run exactly the same code, and the menu is greyed by the same rules.
        Commits.RightTapped += (_, e) =>
        {
            if (RowUnder(e.OriginalSource as DependencyObject) is not { } row) return;
            // Right clicking a commit outside the picked set picks it, so the menu is about what it is on.
            if (!Picked().Any(p => p.Sha == row.Sha)) Commits.SelectedItem = row;
            var menu = new MenuFlyout();
            Add(menu, "Revert", "", RevertButton.IsEnabled, RevertLabel.Text,
                "Add a commit on this branch that takes the picked ones back out. The commits themselves stay in the history.",
                () => Revert_Click(this, null!));
            Add(menu, "Squash", "", SquashButton.IsEnabled, SquashLabel.Text,
                "Make the picked commits into one. They have to sit next to each other, and none may be pushed.",
                () => Squash_Click(this, null!));
            Add(menu, "Reword", "", RewordButton.IsEnabled, "Reword",
                "Replace this commit with one that has the same files and a new message.",
                () => Reword_Click(this, null!));
            menu.ShowAt(Commits, new FlyoutShowOptions { Position = e.GetPosition(Commits) });
            e.Handled = true;
        };
        _filter = new ListFilter(Filter, Files, FilesHeader, r => ((FileRow)r).Display);
        _filter.Picked += OnPicked;
        _filter.ExpectStats = true;
        _commitFilter = new CommitFilter(CommitFilterBox, this);
        _commitFilter.Changed += ShowCommits;
        _ = LoadAsync();

        static void Add(MenuFlyout menu, string name, string glyph, bool on, string label, string tip, Action run)
        {
            var item = new MenuFlyoutItem { Text = label.StartsWith(name, StringComparison.Ordinal) ? label : name, Icon = new FontIcon { Glyph = glyph }, IsEnabled = on };
            ToolTipService.SetToolTip(item, tip);
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
    }

    /// <summary>The commit row a right click landed on, walking up from whatever part of it was hit.</summary>
    CommitRow? RowUnder(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { DataContext: CommitRow row }) return row;
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>select keeps one commit open across a reload, for the one a squash or a reword just made.</summary>
    async Task LoadAsync(string? select = null)
    {
        var root = Session.Require();
        Session.Log.Sink = Pane;
        CommitsSkeleton.Show();
        var rows = await Runner.Quiet(Pane, () =>
        {
            var git = root.Git;
            var branch = git.CurrentBranch(_worktree);
            var co = Ops.BaseCheckout(root, branch);
            var snapRef = root.SnapshotRef(co);
            // Two log processes, run side by side: the branch's own commits and the snapshots under them.
            var reads = Fan.Map(new[] { (Range: snapRef + "..HEAD", Limit: 500), (Range: snapRef, Limit: 50) },
                                x => git.Log(_worktree, x.Range, x.Limit));
            var list = reads[0].Select(c => CommitRow.From(c, snapshot: false)).ToList();
            list.AddRange(reads[1].Where(c => c.Subject != "sg root").Select(c => CommitRow.From(c, snapshot: true)));
            return new { Branch = branch, Checkout = co.Name, List = list };
        });
        CommitsSkeleton.Hide();
        if (rows == null) return;
        Checkout ??= rows.Checkout;
        Branch ??= rows.Branch;
        Subtitle = $"{rows.Branch}  on  svn/{rows.Checkout}   {_worktree}";
        _currentSha = "";
        _branch = rows.List.Where(r => !r.IsSnapshot).ToList();
        _snapshots = rows.List.Where(r => r.IsSnapshot).ToList();
        var empty = _branch.Count == 0 && _snapshots.Count == 0;
        NoCommits.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        // The list is about to be rebuilt out of commits that may not include the one on the right.
        // A reword or a squash leaves a sha nobody can click; a failed one leaves nothing selected.
        NothingPicked(empty ? "no commits" : null);
        ShowCommits();
        if (empty) return;
        var shown = (List<CommitRow>)Commits.ItemsSource;
        var wanted = select == null ? -1 : shown.FindIndex(r => r.Sha == select);
        if (wanted >= 0) Commits.SelectedIndex = wanted;
        else if (_autoSelect || select != null) Commits.SelectedIndex = 0;
    }

    /// <summary>
    /// Nothing is on the right: no header, no message, no files, no diff. Every path that leaves the
    /// page without a commit says it here, so none of them can leave the last one behind.
    /// </summary>
    void NothingPicked(string? why = null)
    {
        _currentSha = "";
        DetailHead.Text = "";
        DetailMessage.Text = "";
        _filter.Clear("Files");
        Diff.ShowText("", why ?? "pick a commit to see what it changed");
    }

    List<CommitRow> _branch = new();
    List<CommitRow> _snapshots = new();
    readonly CommitFilter _commitFilter;
    /// <summary>The list is being bound again, so an empty selection in the middle of it is not the user's doing.</summary>
    bool _rebuilding;

    /// <summary>
    /// The snapshots under the branch are folded into one line until they are asked for. A branch sits
    /// on a long tail of them, one per sync, each titled with every external's revision and URL: that is
    /// the log of SVN, not of the branch, and unfolded it buries the handful of commits the page is for.
    /// </summary>
    bool _snapshotsOpen;

    void ShowCommits()
    {
        List<CommitRow> list;
        if (_commitFilter.Active)
        {
            // A filter flattens the fold: a snapshot that matches is a line of its own, because the
            // word typed may be an external's revision and that is exactly what a snapshot is titled with.
            list = _commitFilter.Apply(_branch);
            list.AddRange(_commitFilter.Apply(_snapshots));
            CommitsHeader.Text = "Commits, " + CommitFilter.Showing(list.Count, _branch.Count + _snapshots.Count);
            Bind(list);
            return;
        }
        CommitsHeader.Text = "Commits on the branch, then the snapshots under it";
        list = new List<CommitRow>(_branch);
        if (_snapshots.Count > 0)
        {
            var newest = _snapshots[0];
            var header = new CommitRow
            {
                Sha = newest.Sha,
                Date = newest.Date,
                Author = "svn",
                IsSnapshot = true,
                IsGroup = true,
                GroupCount = _snapshots.Count,
                Expanded = _snapshotsOpen,
                Subject = _snapshots.Count == 1
                    ? "1 snapshot of SVN"
                    : $"{_snapshots.Count} snapshots of SVN, newest first",
                Toggled = _ =>
                {
                    _snapshotsOpen = !_snapshotsOpen;
                    ShowCommits();
                },
            };
            list.Add(header);
            if (_snapshotsOpen) list.AddRange(_snapshots);
        }
        Bind(list);
    }

    /// <summary>
    /// Puts a rebuilt list on screen. A new list has no selection, so the commit whose diff is on the
    /// right is picked again when it is still on the list, and the right side is cleared when the
    /// filter took it away: a diff of a commit that is not on the list would be a diff of nothing named.
    /// </summary>
    void Bind(List<CommitRow> list)
    {
        var keep = _currentSha.Length == 0 ? null : list.FirstOrDefault(r => r.Sha == _currentSha && !r.IsGroup);
        _rebuilding = true;
        Commits.ItemsSource = list;
        if (keep != null) Commits.SelectedItem = keep;
        _rebuilding = false;
        if (keep == null && _currentSha.Length > 0) NothingPicked();
        SyncRewriteButtons();
    }

    /// <summary>The branch's own commits among the picked ones, newest first. A snapshot is never one of them.</summary>
    List<CommitRow> Picked() => Commits.SelectedItems.OfType<CommitRow>().Where(r => !r.IsSnapshot).ToList();

    /// <summary>
    /// What the two rewrite buttons can do with what is picked. A snapshot in the selection turns both
    /// off rather than being quietly skipped: the picked set is what the button acts on, whole.
    /// </summary>
    void SyncRewriteButtons()
    {
        var all = Commits.SelectedItems.OfType<CommitRow>().ToList();
        var own = Picked();
        var snapshot = all.Count != own.Count;
        // The same rule the core enforces, asked here so the button is off rather than the press
        // being refused after a message has been written.
        var run = !snapshot && own.Count >= 2 && Consecutive(own);

        SquashButton.IsEnabled = run;
        RewordButton.IsEnabled = own.Count == 1 && !snapshot;
        // A revert adds a commit rather than rewriting one, so the picked commits need not sit together.
        RevertButton.IsEnabled = own.Count >= 1 && !snapshot;
        SquashLabel.Text = run ? $"Squash {own.Count}" : "Squash";
        RevertLabel.Text = own.Count > 1 && !snapshot ? $"Revert {own.Count}" : "Revert";
        PickHint.Text = all.Any(r => r.IsGroup) ? "Press that line to open the snapshots, or leave it folded."
            : snapshot ? "A snapshot of SVN is picked. Those cannot be rewritten."
            : own.Count >= 2 && !run ? "They have to sit next to each other."
            : own.Count >= 2 ? ""
            : "Ctrl or Shift picks more than one.";
    }

    /// <summary>
    /// The picked rows are one unbroken run of the branch, with nothing left out in the middle. Against
    /// the branch and not against the list on screen: with a filter on, two commits that sit next to
    /// each other on the list may have a dozen between them that the filter hid.
    /// </summary>
    bool Consecutive(List<CommitRow> picked)
    {
        var at = picked.Select(r => _branch.IndexOf(r)).Where(i => i >= 0).OrderBy(i => i).ToList();
        if (at.Count != picked.Count) return false;
        return at[^1] - at[0] == at.Count - 1;
    }

    async void Commits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        SyncRewriteButtons();
        // The details follow the line just clicked, which with several picked is the last one added.
        if ((e.AddedItems.LastOrDefault() ?? Commits.SelectedItem) is not CommitRow row)
        {
            // Ctrl+click can leave nothing picked at all, and then the right of the page is about nothing.
            if (Commits.SelectedItem == null) NothingPicked();
            return;
        }
        // The line a run of snapshots folds into is a fold, not a commit: pressing it opens the run.
        if (row.IsGroup)
        {
            row.Toggle();
            return;
        }
        if (row.Sha == _currentSha) return;
        _currentSha = row.Sha;
        var git = Session.Require().Git;
        var title = $"{row.Sha[..8]}  all files, unified";
        Diff.BeginLoading(title);
        if (_filter.Count == 0) FilesSkeleton.Show();
        // Three reads, and the one the eye waits for is the cheapest. The patch of a snapshot is the whole
        // of an SVN sync; the header and the file list are two short reads that do not need it, so they run
        // beside it and the page fills in two steps. Run one after another, a snapshot froze all three.
        var patchRead = Task.Run(() => git.CommitPatch(row.Sha));
        var head = await Runner.Quiet(Pane, async () =>
        {
            var details = Task.Run(() => git.Details(row.Sha));
            var files = Task.Run(() => git.ShowNameStatus(row.Sha));
            await Task.WhenAll(details, files);
            return new { Details = details.Result, Files = files.Result };
        });
        if (_currentSha != row.Sha) return;   // the user moved on; that selection hides the skeleton
        FilesSkeleton.Hide();
        // A commit a rewrite in another window took away reads like any other failure: the pane says so and
        // the page empties, so pressing the row again tries once more instead of hitting the sha guard.
        if (head == null) { NothingPicked("could not read that commit"); return; }
        DetailHead.Text = $"{head.Details.Sha}\n{head.Details.Author} <{head.Details.Email}>\n{head.Details.Date}"
                          + (head.Details.Parents.Length > 0 ? $"\nparent {head.Details.Parents[..Math.Min(8, head.Details.Parents.Length)]}" : "");
        DetailMessage.Text = head.Details.Body;
        _filter.SetItems(head.Files.Select(f => new FileRow
        {
            Status = f.Status,
            Path = f.Path,
            OldPath = f.OldPath,
            Display = $"{f.Status}  {f.Path}" + (f.OldPath != null ? $"  (was {f.OldPath})" : ""),
        }).ToList(), "Files");
        var patch = await Runner.Quiet(Pane, () => patchRead);
        if (_currentSha != row.Sha) return;
        if (patch == null) { Diff.ShowText("", "could not read the patch"); return; }
        Diff.ShowUnified(patch, title);
        _filter.SetStats(DiffStats.Parse(patch));
        // --select walks all the way to a file diff, so a check can see what the page really draws.
        if (_autoSelect) _filter.SelectFirstFile();
    }

    async void OnPicked(TreeNode node)
    {
        if (_currentSha.Length == 0) return;
        var git = Session.Require().Git;
        var sha = _currentSha;
        var shortSha = sha[..Math.Min(8, sha.Length)];
        if (node.Row is not FileRow row)
        {
            // A folder: everything the commit changed under it, as one patch.
            var folder = node.FullPath;
            var title = $"{folder}   {node.FileCount} file(s), {shortSha}^ → {shortSha}, unified";
            Diff.BeginLoading(title);
            var patch = await Task.Run(() => git.UnifiedDiff(_worktree, sha + "^", sha, folder));
            if (_filter.IsCurrent(node) && _currentSha == sha) Diff.ShowUnified(patch, title);
            return;
        }
        await Diff.ShowFileAsync(row.Path, $"{row.Path}   {shortSha}^ → {shortSha}", new DiffView.Reads(
                () => row.Status == 'A' ? "" : git.ShowText(sha + "^", row.OldPath ?? row.Path),
                () => row.Status == 'D' ? "" : git.ShowText(sha, row.Path),
                () => git.UnifiedDiff(_worktree, sha + "^", sha, row.Path)),
            // The commit can change under the selection too, so both have to still be the ones asked for.
            () => _filter.IsCurrent(node) && _currentSha == sha);
    }

    async void Squash_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked.Count < 2) return;
        var shas = picked.Select(p => p.Sha).ToList();
        // Asking for the message is also what checks the run: it throws the same refusals the squash would.
        var start = await Runner.Run(Pane, "read the messages", () => Ops.SquashMessage(Session.Require(), _worktree, shas));
        if (start == null) return;
        await RewriteAsync("Squash", $"Squash {picked.Count} commits", "Message for the commit that replaces them", start,
            $"This replaces {picked.Count} commits on {Branch} with one. Do it only while nobody else has them. Continue?",
            msg => Ops.Squash(Session.Require(), _worktree, shas, msg));
    }

    async void Reword_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked.Count != 1) return;
        var sha = picked[0].Sha;
        var start = await Runner.Run(Pane, "read the message", () => Session.Require().Git.Body(sha).TrimEnd());
        if (start == null) return;
        await RewriteAsync("Reword", "Reword", "New message for " + picked[0].ShortSha, start,
            $"This replaces commit {picked[0].ShortSha} on {Branch}, and every commit above it gets a new sha. Do it only while nobody else has them. Continue?",
            msg => Ops.Reword(Session.Require(), _worktree, sha, msg));
    }

    async void Revert_Click(object sender, RoutedEventArgs e)
    {
        var picked = Picked();
        if (picked.Count == 0) return;
        var shas = picked.Select(p => p.Sha).ToList();
        var start = await Runner.Run(Pane, "read the messages", () => Ops.RevertMessage(Session.Require(), _worktree, shas));
        if (start == null) return;
        var what = picked.Count == 1 ? picked[0].ShortSha : picked.Count + " commits";
        await RewriteAsync("Revert", picked.Count == 1 ? "Revert" : $"Revert {picked.Count}", "Message for the commit that undoes " + what, start,
            $"This adds a commit on {Branch} that takes {what} back out. The commits themselves stay in the history. Continue?",
            msg => Ops.Revert(Session.Require(), _worktree, shas, msg));
    }

    /// <summary>
    /// The shape all three share: the message, then the question about what it does to the history, then
    /// the work, then the list read again with the commit that came out of it picked.
    /// </summary>
    async Task RewriteAsync(string verb, string button, string header, string start, string question, Func<string, Ops.RewriteResult> work)
    {
        Message.Title = verb;
        Message.PrimaryButtonText = button;
        Message.Header = header;
        Message.Text = start;
        Message.Ready = true;
        Message.Confirm = () => question;
        if (!await Message.AskAsync()) return;
        var msg = Message.Clean;

        ResultBar.IsOpen = false;
        var r = await Runner.Run(Pane, verb.ToLowerInvariant(), () => work(msg));
        if (r != null)
        {
            MessageDialog.Remember(msg);
            Message.Text = "";
            ResultBar.Severity = InfoBarSeverity.Success;
            ResultBar.Message = verb == "Revert"
                ? $"{Short(r.Sha)} on {r.Branch} takes {(r.Replaced == 1 ? "that commit" : r.Replaced + " commits")} back out. Nothing went to SVN."
                : r.Replaced == 1
                    ? $"Reworded. {Short(r.Sha)} is on {r.Branch} now. Nothing went to SVN."
                    : $"{r.Replaced} commits are now {Short(r.Sha)} on {r.Branch}. Nothing went to SVN.";
            ResultBar.IsOpen = true;
        }
        await LoadAsync(select: r?.Sha);
    }

    static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
