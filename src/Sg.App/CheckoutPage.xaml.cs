using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// The overview's page: one checkout, its worktrees, and every action on either. The main window
/// owns the state of the root and hands this page the checkout to show; the page owns what it draws
/// and everything that starts from a worktree card.
/// </summary>
public sealed partial class CheckoutPage : SgPage
{
    readonly MainWindow _owner;
    CheckoutRow? _current;
    int _shown;
    int _busyDepth;
    /// <summary>A removal is being asked about or run. The dialog is modal to the window, not to the button.</summary>
    bool _removing;
    readonly Dictionary<string, WorktreeSize> _sizes = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _measuring = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A measured worktree, with the write time it was measured at, so a stale one is spotted, and what
    /// each shared folder inside it cost, so a full copy can be named rather than only counted.
    /// </summary>
    sealed record WorktreeSize(FolderSize Size, DateTime? Touched, List<SharedCost> Shared, string? CloneProblem, string Mode, List<string> Rels);

    public CheckoutPage(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Title = "Overview";
        // A card's glyph colour, its sentence colour and its accent button are a brush and a style taken
        // out of the app's resources, and the ones taken belong to the theme that was on at the time. The
        // chips beside them re-read their own on a theme change; without this the rest of the card does
        // not, and Windows switching to dark under an open page left dark ink on a dark card.
        ActualThemeChanged += (_, _) =>
        {
            if (WorktreeList.ItemsSource is List<WorktreeRow> rows) foreach (var r in rows) r.Repaint();
        };
    }

    /// <summary>The strip the overview's operations report into. The main window uses it for its own reads.</summary>
    public StatusStrip Strip => Pane;

    /// <summary>The checkout on screen, or null.</summary>
    public CheckoutRow? Current => _current;

    /// <summary>Coming back from a commit, a push, or the settings: read the root again, as closing that window used to.</summary>
    public override void OnShown(bool returning)
    {
        if (!returning) return;
        _ = _owner.RefreshAsync();
        // A commit page, a shelve, an import: something may have changed a branch, and the backup follows.
        _owner.BackupSoon();
    }

    /// <summary>The bar along the top runs while the state is read.</summary>
    public void SetBusy(bool on)
    {
        _busyDepth += on ? 1 : -1;
        BusyBar.IsIndeterminate = _busyDepth > 0;
        Motion.FadeTo(BusyBar, _busyDepth > 0 ? 1 : 0);
    }

    /// <summary>Bars where the worktree cards will be, for the first read of a root.</summary>
    public void ShowSkeleton(bool on) => OverviewSkeleton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Puts one checkout on screen. Null is "no root open", which is its own page.</summary>
    public void Show(CheckoutRow? row, StatusResult? status)
    {
        _current = row;
        _shown++;   // anything a previous checkout's size walk reports from here is stale
        var noRoot = Session.Root == null;
        NoRoot.Visibility = noRoot ? Visibility.Visible : Visibility.Collapsed;
        Scroll.Visibility = noRoot ? Visibility.Collapsed : Visibility.Visible;
        var has = row != null;
        CheckoutBar.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        WorktreesHeader.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        Title = row?.Name ?? "Overview";
        Checkout = row?.Name;
        Subtitle = row?.Path ?? (noRoot ? "no root open" : Session.Root?.RootPath ?? "");
        if (row == null || status == null)
        {
            WorktreeList.ItemsSource = null;
            NoWorktrees.Visibility = Visibility.Collapsed;
            return;
        }
        var co = status.Checkouts.First(c => c.Name == row.Name);
        CoName.Text = row.Name;
        CoRevision.Text = "r" + co.Revision;
        // How long since the last sync, in the quiet voice; the exact time is one hover away. What the
        // server has since then is the badge on the right, so this line says only when, not what.
        CoSynced.Text = co.SnapshotTaken is { } taken ? "synced" + WorktreeRow.Ago(taken) : "never synced";
        Tip(CoRevision, $"The snapshot svn/{row.Name} is at r{co.Revision}: the exact SVN state every branch of this checkout is built on.");
        Tip(CoSynced, co.SnapshotTaken is { } t
            ? $"The snapshot was taken {t.LocalDateTime:yyyy-MM-dd HH:mm}, the last time this checkout was synced. Sync takes a new one from the server."
            : "No snapshot has been taken yet. Sync takes the first one.");
        // Detail is the URL and the path, one per line. The header over the page already writes the
        // path, so the toolbar takes the line the header does not have and keeps both in its tooltip.
        CoUrl.Text = row.Detail.Split('\n')[0];
        Tip(CoUrl, row.Detail);
        Tip(FolderButton, "Open " + row.Path + " in Explorer.");
        Tip(BackupButton, status.BackupUrl != null
            ? "What the backup repository holds, a branch back from it, and a backup now. " + status.BackupUrl
            : "No backup repository is set. Set its URL in Settings, and every branch, the uncommitted changes and the shelves go there as thin histories.");
        ShowRemote(_owner.Remote.GetValueOrDefault(row.Name), _owner.RemoteErrors.GetValueOrDefault(row.Name));
        ShowLocalEdits(_owner.LocalEdits.TryGetValue(row.Name, out var edits) ? edits : null);
        ShowShelves(co.Shelves);
        var worktrees = status.Worktrees
            .Where(w => w.Base.Equals(row.Name, StringComparison.OrdinalIgnoreCase))
            .Select(w => new WorktreeRow
            {
                Branch = w.Branch,
                Path = w.Path,
                Base = w.Base,
                Dirty = w.Dirty,
                DirtyFiles = w.DirtyFiles,
                Behind = w.Behind,
                Pending = w.Pending,
                Missing = w.Missing,
                Stopped = w.Stopped,
                Conflicts = w.Conflicts,
                Ahead = w.Ahead,
                BaseRevision = co.Revision,
                Detail = w.Path,
                Shared = w.Shared,
                Shelves = w.Shelves,
                NotBackedUp = w.NotBackedUp,
                BackedUp = w.BackedUp,
                BackupOn = status.BackupUrl != null,
            })
            // By name, and by nothing else. Sorting by what each worktree wants put the loudest first,
            // which reads well in a screenshot and badly in use: committing, rebasing or syncing changes
            // the rank, so a card moved out from under the pointer at the moment its state changed - the
            // one moment you were certainly looking at it. A name does not change on its own, so the card
            // is where it was last time. What each one wants is on the card: its badges, its sentence,
            // its one button, and the count in the header above them all.
            .OrderBy(r => r.Branch, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // The list is rebound only when the worktrees themselves are different: one added, one removed,
        // one renamed. A worktree that only changed state is written onto the row that is already bound,
        // and the card repaints what changed rather than being destroyed and built again. The order is by
        // name and the names decide the match, so the two lists line up position for position.
        //
        // Rebinding for a state change cost the whole list: every card and the ten rows inside each one
        // went, taking the hover under the pointer, the keyboard focus, and every size already measured
        // - for one file edited in one worktree, on a timer, while the user was reading a different card.
        if (WorktreeList.ItemsSource is List<WorktreeRow> bound && bound.Count == worktrees.Count
            && bound.Zip(worktrees).All(p => p.First.Path.Equals(p.Second.Path, StringComparison.OrdinalIgnoreCase)))
        {
            for (var i = 0; i < bound.Count; i++)
                if (!bound[i].SameAs(worktrees[i])) bound[i].Update(worktrees[i]);
            worktrees = bound;
        }
        else
        {
            // The cards that were open stay open, and what was already measured is carried over rather
            // than being read again from an empty card that says "measuring..." for a second time.
            if (WorktreeList.ItemsSource is List<WorktreeRow> old)
                foreach (var r in worktrees)
                    if (old.FirstOrDefault(o => o.Path.Equals(r.Path, StringComparison.OrdinalIgnoreCase)) is { } was)
                        r.CarryMeasured(was);
            WorktreeList.ItemsSource = worktrees;
        }
        ShowWorktreesHeader(worktrees);
        _ = MeasureWorktreesAsync(worktrees, _shown);
        NoWorktrees.Visibility = worktrees.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens the card of one branch, for the crumb that names it.</summary>
    public void Expand(string branch)
    {
        if (WorktreeList.ItemsSource is not List<WorktreeRow> rows) return;
        var row = rows.FirstOrDefault(r => r.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase));
        if (row != null) row.IsExpanded = true;
    }

    /// <summary>What the server has that the snapshot does not, and what the checkout has that SVN does not.</summary>
    int _behind, _localEdits;

    /// <summary>
    /// A toolbar button says what it is; its tooltip says what is true right now. The three that used to
    /// be rows with a description under them keep that sentence, one hover away instead of one chevron.
    /// </summary>
    static void Tip(DependencyObject o, string text) => ToolTipService.SetToolTip(o, text);

    /// <summary>
    /// One loud button on the toolbar, never two. A worktree card offers exactly one next action and the
    /// checkout says the same thing in the same place.
    ///
    /// Commit is not styled from here any more. It is an AccentSplitButton, which carries its own accent
    /// brushes and is not a Button, so AccentButtonStyle cannot be put on it: setting it threw, and threw
    /// only when there was something to commit, because with nothing to commit this line set null and
    /// null is allowed. It goes quiet the other way, by being disabled, which ShowLocalEdits does.
    /// So the sync takes the accent when the commit has nothing to be loud about.
    /// </summary>
    void ShowNextAction() =>
        SyncButton.Style = _localEdits == 0 && _behind > 0
            ? (Style)Application.Current.Resources["AccentButtonStyle"]
            : null;

    /// <summary>
    /// Sync is the only button about the server now, and it is two buttons depending on the answer: it
    /// syncs outright when there is nothing to bring in, and opens the incoming page first when there is,
    /// because syncing blind over a server that moved is the one thing worth reading before doing. Its
    /// tooltip has to say which of the two it is about to be, and then say what the server actually said.
    /// </summary>
    void ShowServerState(RemoteCheckResult? result, string state)
    {
        var syncs = result is { Behind: false };
        Tip(SyncButton, (syncs
            ? "svn update the checkout, then take a new snapshot into svn/<name>. Branches are not touched, rebase them when you want the new base. "
            : "Opens what the server has that the snapshot does not - the revisions, their messages and their diffs - with a Sync button under them. ") + state);
    }

    public void ShowRemote(RemoteCheckResult? result, string? error)
    {
        // The ring turns while the answer is still on its way.
        CoRemoteRing.Visibility = result == null && error == null ? Visibility.Visible : Visibility.Collapsed;
        if (result == null)
        {
            CoRemoteBadge.Visibility = Visibility.Collapsed;
            ShowServerState(result, error != null ? "The server could not be asked: " + error.Split('\n')[0] : "Asking the server what it has.");
            _behind = 0;
            ShowNextAction();
            return;
        }
        if (!result.Behind)
        {
            CoRemoteBadge.Visibility = Visibility.Collapsed;
            ShowServerState(result, "Up to date with the server.");
            _behind = 0;
            ShowNextAction();
            return;
        }
        var parts = result.Entries.Where(e => e.Behind)
            .Select(e => $"{(e.Rel.Length == 0 ? "root" : e.Rel)} r{e.Snapshot} → r{e.Server}");
        CoRemoteBadge.Count = result.Commits;
        CoRemoteBadge.Visibility = Visibility.Visible;
        ShowServerState(result, $"{result.Commits}{(result.Commits >= 50 ? "+" : "")} commit(s) on the server to sync: {string.Join(", ", parts)}");
        _behind = result.Commits;
        ShowNextAction();
    }

    /// <summary>
    /// What is waiting on the shelf for this checkout. The button is always on the toolbar, because a
    /// shelf nobody knows about is a shelf nothing comes off; the badge only appears when something is on it.
    /// </summary>
    void ShowShelves(int count)
    {
        CoShelfBadge.Count = count;
        CoShelfBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // It stays on the row so the shelf is always where you left it, and greys out when the shelf is
        // empty: the page would have nothing on it, and a button you can press to reach nothing reads as
        // a place something might be. Its tooltip still says so, because a disabled button must explain.
        ShelfButton.IsEnabled = count > 0;
        Tip(ShelfButton, count == 0
            ? "Changes taken out of the checkout and kept, to put back later. This is how a local edit stops blocking a push of the same file. Nothing is waiting right now."
            : $"{count} set(s) of changes are waiting to go back into the checkout. Open to read one and write it back.");
    }

    public void ShowLocalEdits(int? count)
    {
        // One button, in one place. It was two - the accent Commit on the first row while there was
        // something to commit, and "Checkout changes" on the second row otherwise - and they opened the
        // same page, so which of them was on screen was a rule to learn for no gain. Commit stays put and
        // greys out when nothing is waiting, rather than coming and going and moving the row with it.
        _localEdits = count ?? 0;
        ShowNextAction();
        SvnCommitButton.IsEnabled = count is > 0;

        if (count is null or 0)
        {
            CoLocalBadge.Visibility = Visibility.Collapsed;
            // A disabled button owes the reader the reason, so this is what the tooltip is for.
            Tip(SvnCommitButton, count == null
                ? "Reading what is edited directly in the checkout..."
                : "Nothing is edited directly in the checkout. Work that belongs to a branch lives in its worktree.");
            return;
        }
        CoLocalBadge.Count = count.Value;
        CoLocalBadge.Visibility = Visibility.Visible;
        Tip(SvnCommitButton, $"{count} file(s) edited directly in the checkout. Pick them, see each diff, write a message, "
                             + "and commit them straight to SVN. One commit per working copy. Asks first.");
    }

    /// <summary>
    /// Walks each worktree and caches the answer against the worktree's newest write time, so a
    /// build inside it invalidates the number. One walk per worktree at a time: a 16 GB tree takes
    /// longer than the refresh timer, and starting a second walk would only fight the first for the
    /// disk. Results are dropped when the page has moved on to another checkout.
    /// </summary>
    async Task MeasureWorktreesAsync(List<WorktreeRow> rows, int shown)
    {
        // The write times are one folder listing each. Reading them in one hop puts every size that
        // is already known on screen at once, instead of a thread hop per card before the first one shows.
        var paths = rows.Select(r => r.Path).ToList();
        var touchedByPath = await Task.Run(() => paths.Select(DiskUsage.LastTouched).ToList());
        if (shown != _shown) return;

        var pending = new List<(WorktreeRow Row, DateTime? Touched)>();
        // Left to a walk that was already running when this one started. Its own Apply belongs to the
        // page state it began under, and that state is gone, so it writes the cache and stops. These are
        // read back at the end instead - without this the card said "measuring..." until the next tick.
        var elsewhere = new List<WorktreeRow>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (Known(rows[i], touchedByPath[i]) is { } known) Apply(rows[i], known);
            else pending.Add((rows[i], touchedByPath[i]));
        }
        foreach (var (row, touched) in pending)
        {
            if (shown != _shown) return;
            var path = row.Path;
            if (!_measuring.Add(path)) { elsewhere.Add(row); continue; }

            // The shared folders are walked on their own and left out of the rest, so every byte is
            // walked once and the walk can say which of them are a full copy. A junction costs nothing
            // and a cloned folder reports its full size while sharing every block with the checkout,
            // so neither is added back; a real copy is real bytes and is.
            var co = Session.Root?.Config.Checkouts.FirstOrDefault(c => c.Name.Equals(row.Base, StringComparison.OrdinalIgnoreCase));
            var rels = co?.Junctions ?? [];
            var how = row.Shared;
            // Whether a block clone is even possible between this checkout and this worktree. It decides
            // whether the copy badge is advice or only an amount: where a clone cannot be made, sg's own
            // branch command tells you to use copy, and the badge must not then argue with it.
            var cloneProblem = co == null ? "a clone needs a checkout to clone from"
                : SharedFolders.CloneProblem(co.Path, Path.GetDirectoryName(path) ?? path);
            WorktreeSize measured;
            try
            {
                measured = await Task.Run(() =>
                {
                    var shared = DiskUsage.MeasureShared(path, rels, how);
                    var own = DiskUsage.Measure(path, rels);
                    return new WorktreeSize(own with { Bytes = own.Bytes + shared.Sum(s => s.Own) }, touched, shared, cloneProblem, how, rels.ToList());
                });
            }
            catch (Exception) { row.SizeText = ""; continue; }
            finally { _measuring.Remove(path); }

            _sizes[path] = measured;
            if (shown != _shown) return;
            Apply(row, measured);
        }
        // Whatever another walk finished while this one ran. It put the answer in the cache and could not
        // put it on a card, so this is where the card gets it.
        foreach (var row in elsewhere)
            if (shown == _shown && Known(row, DiskUsage.LastTouched(row.Path)) is { } late) Apply(row, late);
        if (shown == _shown) ShowWorktreesHeader(rows);

        /// <summary>
        /// A cached answer that is still about this worktree as it stands. The write time was the only
        /// test, and it is not the only thing the number depends on: the shared folders come from the
        /// checkout's config and the mode from the branch, so editing the junction list or making the
        /// branch again with another --shared left a size on screen that was measured under the old rule.
        /// </summary>
        WorktreeSize? Known(WorktreeRow row, DateTime? touched)
        {
            if (!_sizes.TryGetValue(row.Path, out var known)) return null;
            if (known.Touched != touched || !known.Mode.Equals(row.Shared, StringComparison.OrdinalIgnoreCase)) return null;
            var junctions = Session.Root?.Config.Checkouts
                .FirstOrDefault(c => c.Name.Equals(row.Base, StringComparison.OrdinalIgnoreCase))?.Junctions ?? [];
            return known.Rels.SequenceEqual(junctions, StringComparer.OrdinalIgnoreCase) ? known : null;
        }

        void Apply(WorktreeRow row, WorktreeSize size)
        {
            row.SizeText = size.Size.ToString();
            row.ShowCopied(size.Shared, size.CloneProblem);
            row.Reclaimable = row.Ahead == 0 && !row.Dirty && !row.Pending && !row.RebaseInProgress
                              && size.Touched != null && DateTime.Now - size.Touched.Value > TimeSpan.FromDays(14);
        }
    }

    /// <summary>
    /// How many worktrees there are, how many are asking for something, and what they cost. The count
    /// of those asking is the answer to why the overview was opened, so it is read before any card.
    /// </summary>
    void ShowWorktreesHeader(List<WorktreeRow> rows)
    {
        if (rows.Count == 0) { WorktreesHeader.Text = "Worktrees"; return; }
        var text = $"Worktrees ({rows.Count})";
        var wanting = rows.Count(r => r.Wants);
        text += wanting == 0 ? ", none need you" : wanting == 1 ? ", 1 needs you" : $", {wanting} need you";
        var known = rows.Where(r => _sizes.ContainsKey(r.Path)).ToList();
        if (known.Count == rows.Count) text += $", {DiskUsage.Human(known.Sum(r => _sizes[r.Path].Size.Bytes))} on disk";
        WorktreesHeader.Text = text;
    }

    // ---- no root: the two ways in, the same ones the pane offers ----

    async void OpenRoot_Click(object sender, RoutedEventArgs e) => await _owner.OpenRootAsync();

    void NewRoot_Click(object sender, RoutedEventArgs e) => _owner.NewRoot();

    // ---- the checkout card: the main window owns these actions, the pane menu reaches them too ----

    void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) _owner.SyncOrPreview(_current.Config, sender);
    }


    async void NewBranch_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => _owner.NewBranchAsync(_current?.Config));

    void SvnChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) _owner.ShowSvnChanges(_current);
    }

    void Shelf_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var co = _current.Config;
        Go(() => new ShelfPage(co) { Checkout = co.Name }, "shelf:" + co.Name);
    }

    /// <summary>The left half of the split button: the same page the button always opened.</summary>
    void SvnCommit_Click(SplitButton sender, SplitButtonClickEventArgs e) => SvnChanges_Click(sender, null!);

    /// <summary>
    /// The right half: every change in the checkout, out of it and onto the shelf, without going to the
    /// page first. No list is picked from because there is nothing to pick - the reason to do this from
    /// here is that something else wants the checkout clean, and half of it clean would not do.
    /// </summary>
    async void ShelveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var co = _current.Config;
        var r = await ShelfActions.SaveAsync(this, Pane, co.Path, null, $"the {_localEdits} change(s) in {co.Name}", "checkout changes");
        if (r == null) return;
        await _owner.RefreshAsync();
    }

    void SvnLog_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) _owner.ShowSvnLog(_current);
    }

    void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) _owner.ShowMerge(_current);
    }

    void ServerBranch_Click(object sender, RoutedEventArgs e) => _owner.ShowServerBranch(_current?.Config);

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) _owner.EditCheckout(_current);
    }

    void OpenCheckout_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) Session.OpenInExplorer(_current.Path);
    }


    // ---- worktree cards ----

    static WorktreeRow? WorktreeOf(object sender) =>
        (sender as FrameworkElement)?.Tag as WorktreeRow ?? (sender as FrameworkElement)?.DataContext as WorktreeRow;

    /// <summary>The button beside the name does whatever the card's one line says to do.</summary>
    void Primary_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row == null) return;
        switch (row.Primary)
        {
            case WorktreeAction.Commit: OpenCommit(row); break;
            case WorktreeAction.Log: OpenLog(row); break;
            case WorktreeAction.Push: OpenPush(row); break;
            case WorktreeAction.Rebase: Rebase_Click(sender, e); break;
            case WorktreeAction.Resolve: OpenResolver(row); break;
            case WorktreeAction.Remove: Remove_Click(sender, e); break;
        }
    }

    void OpenCommit(WorktreeRow row) =>
        Go(() => new CommitPage(row.Path) { Checkout = row.Base, Branch = row.Branch }, "commit:" + row.Path);

    /// <summary>The left half of a card's Commit split button: the same page the plain button opened.</summary>
    void WorktreeCommit_Click(SplitButton sender, SplitButtonClickEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) OpenCommit(row);
    }

    /// <summary>
    /// The right half: every uncommitted change in the worktree, out of it and onto the shelf, without
    /// going to the page first. Nothing is picked from a list because the reason to do this from here is
    /// that a rebase or a push wants the worktree clean, and half of it clean would not do.
    /// </summary>
    async void WorktreeShelveAll_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row == null) return;
        var what = row.DirtyFiles == 0 ? $"the uncommitted changes in {row.Branch}" : $"the {row.DirtyFiles} change(s) in {row.Branch}";
        var r = await ShelfActions.SaveAsync(this, Pane, row.Path, null, what, "worktree changes");
        if (r == null) return;
        _owner.BackupSoon();
        await _owner.RefreshAsync();
    }

    void OpenLog(WorktreeRow row) =>
        Go(() => new LogPage(row.Path) { Checkout = row.Base, Branch = row.Branch }, "log:" + row.Path);

    void OpenPush(WorktreeRow row) =>
        Go(() => new PushPage(row.Path) { Checkout = row.Base, Branch = row.Branch }, "push:" + row.Path);

    void OpenResolver(WorktreeRow row) =>
        Go(() => new ConflictPage(row.Path) { Checkout = row.Base, Branch = row.Branch }, "resolve:" + row.Path);

    void Commit_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) OpenCommit(row);
    }

    void Log_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) OpenLog(row);
    }

    void WorktreeShelf_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row == null) return;
        Go(() => new ShelfPage(null, row.Path, row.Branch) { Checkout = row.Base, Branch = row.Branch }, "shelf:" + row.Path);
    }

    void Push_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) OpenPush(row);
    }

    void Resolve_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) OpenResolver(row);
    }

    async void Rebase_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) await Busy.During(sender, () => RebaseAsync(row));
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) Session.OpenInExplorer(row.Path);
    }

    void Terminal_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) FileActions.OpenTerminal(row.Path);
    }

    void Editor_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row != null) FileActions.OpenEditor(row.Path);
    }

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        // Reached from the card button and from the worktree menu, and it awaits a git status before it
        // shows anything: a second press in that gap opened a second ContentDialog, which WinUI refuses
        // with an exception out of an async void handler, and the app went with it.
        if (row == null || _removing) return;
        _removing = true;
        try { await RemoveAsync(row, sender); }
        finally { _removing = false; }
    }

    async Task RemoveAsync(WorktreeRow row, object sender)
    {
        var root = Session.Require();

        // The card's chips are as old as the last refresh, and this is the one action that cannot be
        // undone. Ask git again before naming what goes, so a file edited since then is still counted.
        var dirty = row.Dirty;
        if (!row.Missing)
        {
            try { dirty = await Task.Run(() => !root.Git.IsClean(row.Path)); }
            catch (Exception) { /* keep what the card knew */ }
        }

        var losses = new List<Dialogs.Loss>();
        if (row.Ahead > 0)
            losses.Add(new Dialogs.Loss(ChipSeverity.Critical, "\uE898",
                row.Ahead == 1 ? "1 commit that is not in SVN" : $"{row.Ahead} commits that are not in SVN"));
        if (row.Pending)
            losses.Add(new Dialogs.Loss(ChipSeverity.Critical, "\uE898", "a push that stopped half way"));
        if (dirty)
            losses.Add(new Dialogs.Loss(ChipSeverity.Attention, "\uE70F", "uncommitted changes"));

        if (!await Dialogs.ConfirmLoss(this, "Remove " + row.Branch,
                $"The worktree folder and the branch both go.\n{row.Path}",
                losses,
                "Anything already pushed stays in SVN.",
                "Remove"))
            return;
        // The ring belongs to the removal, not to the question about it: a spinner beside a modal
        // dialog says nothing, and the work after it takes as long as a git worktree remove takes.
        await Busy.During(sender, async () =>
        {
            await Runner.Run(Pane, "remove " + row.Branch, () => Ops.Remove(root, row.Branch, force: true));
            await _owner.RefreshAsync();
        });
    }

    /// <summary>
    /// Writes the branch to a file the far side can put back. The commits go as patches and the base
    /// goes as SVN revisions, because the machine at the other end builds its own snapshot.
    /// </summary>
    async void Export_Click(object sender, RoutedEventArgs e)
    {
        var row = WorktreeOf(sender);
        if (row == null) return;
        var root = Session.Require();
        var file = await WindowHelper.PickSaveFile(this, Export.SuggestName(row.Branch), "sg export", Export.Extension);
        if (file == null) return;
        var res = await Busy.During(sender, () => Runner.Run(Pane, "export " + row.Branch, () => Export.Write(root, row.Path, file)));
        if (res == null) return;
        await Dialogs.Info(this, "Exported " + res.Branch,
            $"{res.Commits} commit(s), {res.Bytes / 1024} KB\n{res.File}\n\n"
            + (res.Uncommitted > 0
                ? $"{res.Uncommitted} uncommitted change(s) are NOT in it: an export carries commits. Commit or shelve them and export again if they matter.\n\n"
                : "")
            + "On the other machine, open the checkout of the same repository and use 'Import a branch'.");
    }

    /// <summary>What the backup holds, and a branch back from it. The page says so before anything is made.</summary>
    void Backup_Click(object sender, RoutedEventArgs e) =>
        GoThen(() => new BackupPage { Checkout = _current?.Name }, "backup", () => _ = _owner.RefreshAsync());

    /// <summary>The other end of that. The page it opens says what is in the file before anything is made.</summary>
    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var file = await WindowHelper.PickOpenFile(this, Export.Extension);
        if (file == null) return;
        GoThen(() => new ImportPage(file) { Checkout = _current?.Name }, "import:" + file, () => _ = _owner.RefreshAsync());
    }

    async Task RebaseAsync(WorktreeRow row)
    {
        var root = Session.Require();
        var r = await Runner.Run(Pane, "rebase", () => Ops.Rebase(root, row.Path));
        if (r == null) { await _owner.RefreshAsync(); return; }
        if (r.Ok)
        {
            _owner.BackupSoon();
            Pane.Append($"{r.Branch} is on the latest svn/{r.Checkout}, {r.Ahead} commit(s) ahead");
            if (r.Refreshed.Count > 0) Pane.Append("shared folders refreshed from the checkout: " + string.Join(", ", r.Refreshed));
        }
        else
        {
            Pane.Append("it stopped on conflicts, opening the resolver");
            OpenResolver(row);
            return;   // the page refreshes when the resolver is left
        }
        await _owner.RefreshAsync();
    }
}
