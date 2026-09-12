using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// Git commit in a worktree: pick files, read and edit each diff, stage or discard it one block at a
/// time, and write the message. Commits carry your own git identity.
///
/// A file has two diffs, the way git has two: the index against the working tree, which is what is not
/// staged yet, and HEAD against the index, which is what a commit would take. The switch over the diff
/// picks one; it only appears for a file that has something staged, because until then they are the same.
/// </summary>
public sealed partial class CommitPage : SgPage
{
    readonly string _worktree;
    List<ChangeRow> _rows = new();
    readonly ListFilter _filter;
    readonly ToggleButton _workingSide, _stagedSide;
    readonly bool _autoSelect;

    /// <summary>Which of a file's two diffs is on show, and the file that answer belongs to.</summary>
    bool _showStaged;
    string? _sideOf;

    /// <summary>The file the diff on show was read from, so a block action edits exactly what was read.</summary>
    string? _shownPath;
    string _shownModified = "";
    PatchFile? _shownPatch;
    /// <summary>There is a file on disk to write. A deleted file has blocks to stage but nothing to discard into.</summary>
    bool _shownOnDisk;

    /// <summary>autoSelect opens with the first file already picked, for a menu entry that names one.</summary>
    public CommitPage(string worktree, bool autoSelect = false)
    {
        InitializeComponent();
        _worktree = worktree;
        _autoSelect = autoSelect;
        Title = "Commit";
        Subtitle = worktree;
        ColumnSplitter.Attach(Splitter);
        Shortcuts.DiffNavigation(this, Diff);
        Shortcuts.Add(this, VirtualKey.Enter, VirtualKeyModifiers.Control, () => { if (CommitButton.IsEnabled) _ = CommitAsync(); });
        Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync(_shownPath));
        FileActions.Attach(Files, n => PathUtil.Join(_worktree, n.FullPath), ExtendMenu);
        _filter = new ListFilter(Filter, Files, FilesHeader, r => ((ChangeRow)r).Display);
        _filter.Picked += OnPicked;
        _filter.ExpectStats = true;

        (_workingSide, _stagedSide) = SideSwitch();
        Diff.SelectionChanged += SyncBlockButtons;
        Diff.DirtyChanged += SyncBlockButtons;
        Diff.ActionInvoked += OnDiffAction;
        _ = LoadAsync();
    }

    /// <summary>The two halves of a part staged file, as a pair of buttons in the diff's own header.</summary>
    (ToggleButton Working, ToggleButton Staged) SideSwitch()
    {
        ToggleButton Make(string text, string tip, bool staged)
        {
            var b = new ToggleButton { Content = text, FontSize = 12, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0 };
            ToolTipService.SetToolTip(b, tip);
            b.Click += (_, _) =>
            {
                if (_showStaged == staged) { SyncSideSwitch(); return; }   // the pair is one choice, not two switches
                _showStaged = staged;
                if (_filter.Selected<ChangeRow>() is { } row) _ = ShowFileAsync(row);
            };
            Diff.HeaderExtras.Children.Add(b);
            return b;
        }
        var working = Make("Not staged", "The changes on disk that the index does not have yet. Stage or discard them one block at a time.", staged: false);
        var staged = Make("Staged", "What the index holds: exactly what a commit of this file would take.", staged: true);
        return (working, staged);
    }

    /// <summary>Right click on a line: the file actions, then what can be done to these changes.</summary>
    void ExtendMenu(MenuFlyout menu, TreeNode node)
    {
        var entries = node.Rows().OfType<ChangeRow>().Select(r => r.Entry).ToList();
        if (entries.Count == 0) return;
        menu.Items.Add(new MenuFlyoutSeparator());

        Add("Stage changes", "\uE710", "Put the whole of these files in the index. A commit then takes exactly that.",
            () => StageAsync(entries.Select(e => e.Path).ToList()));
        Add("Unstage changes", "\uE738", "Take these files out of the index again. The files on disk are untouched.",
            () => UnstageAsync(entries.Select(e => e.Path).ToList()));
        Add("Discard changes", "\uE7A7", "Throw away these changes. A tracked file goes back to the last commit, an untracked one is deleted. Asks first.",
            () => DiscardAsync(entries));
        Add("Shelve", "\uE7B8", "Take these out of the worktree and keep them, to put back later. Nothing is lost and the branch is left clean.",
            () => ShelveAsync(entries.Select(e => e.Path).ToList()));
        Add("Delete file", "\uE74D", "Delete these files from disk and stage the deletion. Asks first.",
            () => DeleteAsync(entries));
        if (entries.Count == 1 && !entries[0].Untracked)
            Add("Blame", "\uE7B3", "Who last changed each line: the SVN revision for the lines that came in with a snapshot, and the branch's own commits for the rest.",
                () =>
                {
                    var path = entries[0].Path;
                    Go(() => new BlamePage(path, _worktree, null) { Checkout = Checkout, Branch = Branch }, "blame:" + path);
                    return Task.CompletedTask;
                });

        void Add(string text, string glyph, string tip, Func<Task> run)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            ToolTipService.SetToolTip(item, tip);
            item.Click += async (_, _) => await run();
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Which load the page is on. A read that comes back after the user has moved on, or after another
    /// reload started, checks this and drops its answer instead of drawing over the newer one.
    /// </summary>
    int _generation;

    /// <summary>reselect keeps one file open across a reload, for example after a block of it was staged.</summary>
    async Task LoadAsync(string? reselect = null)
    {
        var root = Session.Require();
        Session.Log.Sink = Pane;
        var gen = ++_generation;
        if (_filter.Count == 0) FilesSkeleton.Show();
        // The status walk is the slow one on a worktree this size, and reading HEAD used to queue behind
        // it. Two processes side by side, and the status brings the branch name back with it.
        var data = await Runner.Quiet(Pane, () =>
        {
            var (s, h) = Fan.Two(() => root.Git.Status(_worktree), () => root.Git.HeadSummary(_worktree));
            return new { Status = s, Head = h };
        });
        FilesSkeleton.Hide();
        if (data == null || gen != _generation) return;
        var (status, head) = (data.Status, data.Head);
        var branch = status.Branch ?? "(detached)";
        Branch ??= branch;
        Subtitle = $"{branch}   {_worktree}";
        _lastMessage = head.Message;
        Amend.IsEnabled = head.HasParent;
        AmendFromEmpty.Visibility = head.HasParent ? Visibility.Visible : Visibility.Collapsed;
        if (!head.HasParent) Amend.IsChecked = false;
        _rows = status.Entries.Select(e => new ChangeRow
        {
            Entry = e,
            Checked = e.Tracked,
            Display = $"{e.BothCodes}  {e.Path}" + (e.OldPath != null ? $"  (was {e.OldPath})" : ""),
        }).ToList();
        // Not during All and None: the summary walks every row, counts them and re-cleans the message, and
        // running that once per tick made a thousand file worktree freeze on one press. Each handler ends
        // with the one call that all of them stood in for.
        foreach (var r in _rows) r.PropertyChanged += (_, a) => { if (!_bulk && a.PropertyName == nameof(ChangeRow.Checked)) SyncCommitButton(); };
        _filter.SetItems(_rows, "Changes");
        SyncEmptyState();
        SyncCommitButton();
        _ = ShelfActions.ShowCountAsync(ShelfButton, CleanShelfButton, null, Branch, () => gen == _generation);
        if (reselect != null)
            _filter.Select(r => ((ChangeRow)r).Entry.Path.Equals(reselect, StringComparison.OrdinalIgnoreCase));
        else if (_autoSelect && !_filter.HasPick) _filter.SelectFirstFile();
        if (_rows.Count == 0) { Clear(); Diff.ShowText("", "no changes"); return; }

        // The numbers beside each row come from a read of counts alone, and nothing waits for them:
        // the list, the diff and the buttons are all usable before they land.
        _ = ShowCountsAsync(gen);

        // The patch of everything is only read when it is the thing on screen. Staging a block reloads
        // with a file picked, and reading the whole worktree's patch to then throw it away was most of
        // what that press cost.
        if (_filter.HasPick) return;
        const string title = "all tracked changes against HEAD, unified. Untracked files are not in it.";
        Clear();
        Diff.BeginLoading(title);
        var patch = await Task.Run(() => root.Git.UnifiedDiff(_worktree, "HEAD", null));
        if (gen == _generation && !_filter.HasPick) Diff.ShowUnified(patch, title);
    }

    async Task ShowCountsAsync(int gen)
    {
        var counts = await Task.Run(() => Session.Require().Git.NumStat(_worktree, "HEAD"));
        if (gen == _generation) _filter.SetStats(counts);
    }

    string _lastMessage = "";

    void Clear()
    {
        _shownPath = null;
        _shownPatch = null;
        _shownModified = "";
        _shownOnDisk = false;
        Diff.SetActions(Array.Empty<DiffView.DiffAction>());
        _workingSide.Visibility = _stagedSide.Visibility = Visibility.Collapsed;
    }

    async void OnPicked(TreeNode node)
    {
        if (node.Row is ChangeRow row) { await ShowFileAsync(row, node); return; }

        // A folder: every tracked change under it, as one patch. Nothing in it can be staged by block.
        Clear();
        var folder = node.FullPath;
        var title = $"{folder}   {node.FileCount} file(s), HEAD → working tree, unified. Untracked files are not in it.";
        Diff.BeginLoading(title);
        var patch = await Task.Run(() => Session.Require().Git.UnifiedDiff(_worktree, "HEAD", null, folder));
        if (_filter.IsCurrent(node)) Diff.ShowUnified(patch, title);
    }

    /// <summary>
    /// One file, on whichever side the switch is on. The two sides are read the same way: the text
    /// before, the text after, and the patch between them, which is where the blocks come from.
    /// </summary>
    async Task ShowFileAsync(ChangeRow row, TreeNode? node = null)
    {
        var git = Session.Require().Git;
        var entry = row.Entry;
        var abs = PathUtil.Join(_worktree, entry.Path);
        Clear();

        // The side is remembered per file: a reload after staging a block must come back to the same one.
        if (_sideOf != entry.Path)
        {
            _sideOf = entry.Path;
            _showStaged = !entry.HasUnstaged && entry.Staged;
        }
        else if (_showStaged && !entry.Staged) _showStaged = false;
        else if (!_showStaged && !entry.HasUnstaged && entry.Staged) _showStaged = true;

        _workingSide.Visibility = _stagedSide.Visibility = entry.Staged ? Visibility.Visible : Visibility.Collapsed;
        SyncSideSwitch();

        var head = entry.OldPath ?? entry.Path;
        var title = _showStaged
            ? $"{entry.Path}   HEAD → staged"
            : $"{entry.Path}   {(entry.Staged ? "staged" : "HEAD")} → working tree";

        var staged = _showStaged;
        var sides = await Diff.ShowFileAsync(entry.Path, title, new DiffView.Reads(
                () => staged
                    ? (entry.Code.StartsWith('A') ? "" : git.ShowTextIn(_worktree, "HEAD", head))
                    : (entry.Untracked ? "" : git.ShowIndexText(_worktree, entry.Path)),
                () => staged
                    ? git.ShowIndexText(_worktree, entry.Path)
                    : (File.Exists(abs) ? ReadTextSafe(abs) : ""),
                () => staged ? git.DiffStaged(_worktree, entry.Path) : git.DiffUnstaged(_worktree, entry.Path)),
            () => node == null || _filter.IsCurrent(node), editable: !staged && File.Exists(abs));

        if (sides == null) return;
        _shownPath = entry.Path;
        _shownModified = sides.Value.Modified;
        _shownOnDisk = !staged && File.Exists(abs);
        _shownPatch = Patch.FileFor(Patch.Parse(sides.Value.Unified), entry.Path);
        Diff.SetActions(staged
            ? new[] { DiffBlocks.UnstageAction }
            : new[] { DiffBlocks.StageAction, DiffBlocks.DiscardAction, DiffBlocks.SaveAction, DiffBlocks.UndoAction });
        SyncBlockButtons();
    }

    void SyncSideSwitch()
    {
        _workingSide.IsChecked = !_showStaged;
        _stagedSide.IsChecked = _showStaged;
    }

    /// <summary>What the buttons over the diff say and whether they are on, for wherever the cursor is now.</summary>
    void SyncBlockButtons()
    {
        var blocks = DiffBlocks.Under(_shownPatch, Diff.Selection);
        Diff.SetActionState(DiffBlocks.Stage, blocks.Count > 0, DiffBlocks.Label("Stage", blocks));
        Diff.SetActionState(DiffBlocks.Unstage, blocks.Count > 0, DiffBlocks.Label("Unstage", blocks));
        Diff.SetActionState(DiffBlocks.Discard, blocks.Count > 0 && _shownOnDisk, DiffBlocks.Label("Discard", blocks));
        Diff.SetActionState(DiffBlocks.Save, Diff.IsDirty);
        Diff.SetActionState(DiffBlocks.Undo, Diff.IsDirty);
    }

    async void OnDiffAction(string id)
    {
        if (id == DiffBlocks.Save) { await SaveAsync(); return; }
        if (id == DiffBlocks.Undo) { await LoadAsync(_shownPath); return; }

        var path = _shownPath;
        var patch = _shownPatch;
        var blocks = DiffBlocks.Under(patch, Diff.Selection);
        if (path == null || patch == null || blocks.Count == 0) return;
        if (Diff.IsDirty)
        {
            await Dialogs.Info(this, "Unsaved edits", "The editor holds changes the file on disk does not. Save them, or undo them, before acting on a block.");
            return;
        }
        var git = Session.Require().Git;
        var text = Patch.Render(patch, blocks);
        var what = DiffBlocks.Label(id, blocks).ToLowerInvariant() + " of " + path;

        // Stage and Unstage move a block through the index and can be undone by the other one. Discard
        // rewrites the file, and what it takes out was never committed anywhere: it is the one action on
        // this page with nothing behind it, and it used to run on a single keystroke.
        if (id == DiffBlocks.Discard && !await Dialogs.Confirm(this, "Discard " + DiffBlocks.Label(id, blocks).ToLowerInvariant(),
                $"Put {(blocks.Count == 1 ? "this block" : $"these {blocks.Count} blocks")} of {path} back the way the last commit has them?\n\n"
                + "The change goes; it is not on the branch and not in SVN, so there is nothing to take it back from.", "Discard"))
            return;
        var ok = id switch
        {
            DiffBlocks.Stage => await Runner.Run(Pane, what, () => git.ApplyPatch(_worktree, text, cached: true, reverse: false)),
            DiffBlocks.Unstage => await Runner.Run(Pane, what, () => git.ApplyPatch(_worktree, text, cached: true, reverse: true)),
            DiffBlocks.Discard => await Runner.Run(Pane, what, () => WriteReversed(PathUtil.Join(_worktree, path), blocks)),
            _ => false,
        };
        if (ok) await LoadAsync(reselect: path);
    }

    /// <summary>Puts the blocks back the way the left side has them, refusing when the file moved under the diff.</summary>
    void WriteReversed(string abs, List<PatchHunk> blocks)
    {
        var file = TextFile.Read(abs);
        if (file.Text != _shownModified) throw new SgException("the file changed on disk since this diff was read. Refresh, then pick the block again.");
        TextFile.Write(abs, Patch.Reverse(file.Text, blocks), file.Encoding);
    }

    /// <summary>The editor's text goes to disk. The file must still hold what the diff was read from.</summary>
    async Task SaveAsync()
    {
        var path = _shownPath;
        if (path == null || !Diff.IsDirty) return;
        var text = await Diff.ModifiedTextAsync();
        var abs = PathUtil.Join(_worktree, path);
        var before = _shownModified;
        var ok = await Runner.Run(Pane, "save " + path, () =>
        {
            var file = TextFile.Read(abs);
            if (file.Text != before) throw new SgException("the file changed on disk while it was open here. Refresh, then edit it again.");
            TextFile.Write(abs, text, file.Encoding);
        });
        if (!ok) return;
        Diff.MarkSaved();
        await LoadAsync(reselect: path);
    }

    static string ReadTextSafe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024) return "\0";
            return TextFile.Read(path).Text;
        }
        catch (IOException) { return ""; }
    }

    /// <summary>
    /// The checked changes leave the worktree and wait in the store. A rebase and a push both refuse a
    /// worktree that is not clean, so this is the way to get out of their way without throwing work away.
    /// </summary>
    async Task ShelveAsync(List<string> paths)
    {
        if (paths.Count == 0) { await Dialogs.Info(this, "Nothing checked", "Check the changes to shelve."); return; }
        var what = paths.Count == 1
            ? "Put the changes to " + paths[0] + " aside."
            : $"Put {paths.Count} change(s) of {Branch} aside.";
        var r = await ShelfActions.SaveAsync(this, Pane, _worktree, paths, what, ShelfActions.Suggest(paths));
        if (r == null) return;
        ResultBar.Severity = InfoBarSeverity.Success;
        ResultBar.Message = $"{r.Shelf.Count} file(s) are on the shelf as \"{r.Shelf.Title}\". The worktree holds what the branch has for them again.";
        ResultBar.IsOpen = true;
        await LoadAsync();
    }

    async void Shelve_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => ShelveAsync(Checked().Select(e2 => e2.Path).ToList()));

    void Shelf_Click(object sender, RoutedEventArgs e)
    {
        var branch = Branch;
        Go(() => new ShelfPage(null, _worktree, branch) { Checkout = Checkout, Branch = branch }, "shelf:" + _worktree);
    }

    /// <summary>Every row is being ticked or unticked at once, so the summary waits for the end of it.</summary>
    bool _bulk;

    void SetAll(bool on)
    {
        _bulk = true;
        try { foreach (var r in _rows) r.Checked = on; }
        finally { _bulk = false; }
        SyncCommitButton();
    }

    void All_Click(object sender, RoutedEventArgs e) => SetAll(true);
    void None_Click(object sender, RoutedEventArgs e) => SetAll(false);

    List<StatusEntry> Checked() => _rows.Where(r => r.Checked).Select(r => r.Entry).ToList();

    /// <summary>The button opens the message once something is ticked; the message decides whether the commit can go.</summary>
    void SyncCommitButton()
    {
        var picked = Checked();
        var amending = Amend.IsChecked == true;
        CommitButton.IsEnabled = picked.Count > 0 || amending;
        Message.Ready = CommitButton.IsEnabled;
        var verb = amending ? "Amend" : "Commit";
        CommitLabel.Text = picked.Count == 0 ? verb : $"{verb} {picked.Count} file" + (picked.Count == 1 ? "" : "s");

        var partly = picked.Count(p => p.Staged && p.HasUnstaged);
        Summary.Text = _rows.Count == 0
            ? (amending ? "Nothing new: this rewrites the message of the last commit." : "")
            : $"{_rows.Count} change(s), untracked files are unchecked."
              + (partly == 0 ? "" : $"  {partly} of them {(partly == 1 ? "goes" : "go")} in as staged only.");
    }

    /// <summary>
    /// The one sentence, or the page. A clean worktree has neither files nor a diff, so the page is the
    /// sentence - unless Amend is ticked, which is a commit with no files and needs the message box.
    /// </summary>
    void SyncEmptyState()
    {
        var empty = _rows.Count == 0 && Amend.IsChecked != true;
        NoChanges.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>From the empty page: tick Amend and show the page, so the message dialog has somewhere to come from.</summary>
    void AmendFromEmpty_Click(object sender, RoutedEventArgs e)
    {
        Amend.IsChecked = true;
        Amend_Click(sender, e);
    }

    void Amend_Click(object sender, RoutedEventArgs e)
    {
        // The message of the commit being replaced starts the new one, the way git commit --amend does.
        if (Amend.IsChecked == true && Message.Text.Trim().Length == 0) Message.Text = _lastMessage;
        else if (Amend.IsChecked != true && Message.Text.TrimEnd() == _lastMessage.TrimEnd()) Message.Text = "";
        Message.Title = Amend.IsChecked == true ? "Amend the last commit" : "Commit";
        Message.PrimaryButtonText = Amend.IsChecked == true ? "Amend" : "Commit";
        SyncCommitButton();
        // Unticking it on a clean worktree used to leave the page filled with nothing: an empty file card,
        // a diff saying "no changes" and a dead Commit button, with no way back to the one sentence.
        SyncEmptyState();
    }

    /// <summary>The left half of the split button. Its right half drops the menu and never gets here.</summary>
    async void Commit_Click(SplitButton sender, SplitButtonClickEventArgs e) => await CommitAsync();

    async Task CommitAsync()
    {
        var amending = Amend.IsChecked == true;
        Message.Confirm = amending
            ? () => "This replaces the last commit on " + Branch + ". Do it only if nobody has it yet. Continue?"
            : null;
        // An empty box starts with the prefix the last commit on this branch carried, "gui: " say, with
        // the caret after it. The branch's own last commit only: a snapshot is titled "Fort r266" and
        // has none, so a fresh branch starts empty. Delete it and it stays deleted for this showing;
        // the box keeps its text between showings, so only an empty one is filled.
        if (!amending && Message.Text.Trim().Length == 0 && Msg.Prefix(_lastMessage) is { } prefix)
            Message.Text = prefix + " ";
        if (!await Message.AskAsync()) return;
        var root = Session.Require();
        var picked = Checked();
        var msg = Message.Clean;
        var paths = picked.SelectMany(p => p.OldPath != null ? new[] { p.Path, p.OldPath } : new[] { p.Path }).Distinct().ToList();
        var staged = picked.Where(p => p.Staged).Select(p => p.Path).ToList();
        await Busy.During(CommitButton, async () =>
        {
            ResultBar.IsOpen = false;
            var sha = await Runner.Run(Pane, amending ? "amend" : "commit",
                () => root.Git.CommitPathsAsUser(_worktree, paths, staged, msg, amending));
            if (sha != null)
            {
                MessageDialog.Remember(msg);
                Pane.Append((amending ? "amended into " : "committed ") + sha[..10]);
                ResultBar.Severity = InfoBarSeverity.Success;
                ResultBar.Message = $"{(amending ? "Amended into" : "Committed")} {sha[..10]} on {Branch}. Nothing went to SVN.";
                ResultBar.IsOpen = true;
                Message.Text = "";
                Amend.IsChecked = false;
                Message.Title = "Commit";
                Message.PrimaryButtonText = "Commit";
            }
            await LoadAsync();
        }, restoreEnabled: false);
    }

    async Task StageAsync(List<string> paths)
    {
        if (paths.Count == 0) return;
        var root = Session.Require();
        if (await Runner.Run(Pane, "stage", () => root.Git.AddPaths(_worktree, paths))) await LoadAsync(_shownPath);
    }

    async Task UnstageAsync(List<string> paths)
    {
        if (paths.Count == 0) return;
        var root = Session.Require();
        if (await Runner.Run(Pane, "unstage", () => root.Git.Unstage(_worktree, paths))) await LoadAsync(_shownPath);
    }

    async Task DiscardAsync(List<StatusEntry> picked)
    {
        var root = Session.Require();
        if (picked.Count == 0) { await Dialogs.Info(this, "Nothing checked", "Check the files to discard."); return; }
        var what = picked.Count == 1 ? $"the changes in {picked[0].Path}" : $"the changes in {picked.Count} file(s)";
        if (!await Dialogs.Confirm(this, "Discard changes", $"Throw away {what}? Untracked files get deleted. This cannot be undone.", "Discard")) return;
        await Runner.Run(Pane, "discard", () =>
        {
            root.Git.RestoreFromHead(_worktree, picked.Where(p => p.Tracked).Select(p => p.Path));
            foreach (var p in picked.Where(p => p.Untracked))
            {
                var abs = PathUtil.Join(_worktree, p.Path);
                if (File.Exists(abs)) File.Delete(abs);
            }
        });
        await LoadAsync();
    }

    async Task DeleteAsync(List<StatusEntry> picked)
    {
        if (picked.Count == 0) return;
        var what = picked.Count == 1 ? picked[0].Path : $"{picked.Count} file(s)";
        if (!await Dialogs.Confirm(this, "Delete from disk", $"Delete {what} and stage the deletion? This cannot be undone.", "Delete")) return;
        var root = Session.Require();
        await Runner.Run(Pane, "delete", () =>
        {
            var tracked = picked.Where(p => p.Tracked).Select(p => p.Path).ToList();
            if (tracked.Count > 0) root.Git.RemovePaths(_worktree, tracked);
            foreach (var p in picked.Where(p => p.Untracked))
            {
                var abs = PathUtil.Join(_worktree, p.Path);
                if (File.Exists(abs)) File.Delete(abs);
            }
        });
        await LoadAsync();
    }

    async void Stage_Click(object sender, RoutedEventArgs e) =>
        await Busy.During(sender, () => StageAsync(Checked().Select(p => p.Path).ToList()));

    async void Unstage_Click(object sender, RoutedEventArgs e) =>
        await Busy.During(sender, () => UnstageAsync(Checked().Where(p => p.Staged).Select(p => p.Path).ToList()));

    async void Discard_Click(object sender, RoutedEventArgs e) =>
        await Busy.During(sender, () => DiscardAsync(Checked()));

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // A reload re-reads the file and replaces what is in the editor, so anything typed and not saved
        // goes with it. Every block action already refuses while the editor is dirty; this asks instead,
        // because refusing a refresh is the one thing that leaves no way forward.
        if (Diff.IsDirty && !await Dialogs.Confirm(this, "Unsaved edits",
                $"The editor holds changes {_shownPath} on disk does not have. Refreshing reads the file again and loses them.", "Refresh anyway"))
            return;
        await Busy.During(sender, () => LoadAsync(_shownPath));
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
