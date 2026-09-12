using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// Edits made directly in an SVN checkout: see them, read and edit each diff, revert them whole or one
/// block at a time, ignore what should never have been listed, or commit them straight to SVN.
///
/// SVN has no index, so a file has one diff here: BASE against the working copy. A block goes back to
/// BASE the way TortoiseSVN's "revert this hunk" does, and the file itself can be edited in place.
/// </summary>
public sealed partial class SvnCommitPage : SgPage
{
    readonly CheckoutConfig _co;
    List<SvnChangeRow> _rows = new();
    readonly ListFilter _filter;
    readonly bool _autoSelect;

    /// <summary>The file the diff on show was read from, so a block action edits exactly what was read.</summary>
    string? _shownPath;
    string _shownModified = "";
    PatchFile? _shownPatch;

    /// <summary>autoSelect opens with the first file already picked, for a menu entry that names one.</summary>
    public SvnCommitPage(CheckoutConfig co, bool autoSelect = false)
    {
        InitializeComponent();
        _co = co;
        _autoSelect = autoSelect;
        Title = "Changes in the checkout";
        Checkout = co.Name;
        Subtitle = $"{co.Name}   {co.Path}";
        ColumnSplitter.Attach(Splitter);
        Shortcuts.DiffNavigation(this, Diff);
        Shortcuts.Add(this, VirtualKey.Enter, VirtualKeyModifiers.Control, () => { if (CommitButton.IsEnabled) _ = CommitAsync(); });
        Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync(_shownPath));
        FileActions.Attach(Files, n => PathUtil.Join(_co.Path, n.FullPath), ExtendMenu);
        _filter = new ListFilter(Filter, Files, FilesHeader, r => ((SvnChangeRow)r).Display, r => ((SvnChangeRow)r).Group);
        _filter.Picked += OnPicked;
        _filter.ExpectStats = true;
        Message.Minimum = Session.Root?.Config.MinMessageLength ?? 10;
        Message.Confirm = () =>
        {
            var groups = _rows.Where(r => r.Checked).Select(r => r.Change.Wc).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return $"This makes {groups} SVN commit(s) that everyone can see, straight from the checkout. Continue?";
        };
        Diff.SelectionChanged += SyncBlockButtons;
        Diff.DirtyChanged += SyncBlockButtons;
        Diff.ActionInvoked += OnDiffAction;
        Session.Log.Sink = Pane;
        _ = LoadAsync();
    }

    /// <summary>Right click on a line: the file actions, then what can be done to these changes.</summary>
    void ExtendMenu(MenuFlyout menu, TreeNode node)
    {
        var changes = node.Rows().OfType<SvnChangeRow>().Select(r => r.Change).ToList();
        if (changes.Count == 0) return;
        menu.Items.Add(new MenuFlyoutSeparator());

        Add("Discard", "\uE7A7", "Put these files back the way SVN has them; an unversioned one is deleted. Asks first, and Undo on the bar brings them back.",
            () => RevertAsync(changes.Select(c => c.Path).ToList()));

        var unversioned = changes.Where(c => c.Item == "unversioned").ToList();
        if (unversioned.Count > 0)
            Add("Add to ignore list", "\uE8F8", "Set svn:ignore on the folder each of these sits in, so it stops being listed. The property change is itself a change to commit.",
                () => IgnoreAsync(unversioned.Select(c => c.Path).ToList()));

        Add("Shelve", "\uE7B8", "Take these out of the checkout and keep them, to put back later. A local edit that blocks a push stops blocking it.",
            () => ShelveAsync(changes.Select(c => c.Path).ToList()));

        Add("Delete file", "\uE74D", "Delete these from disk. A versioned file is deleted through svn, so the deletion is a change to commit. Asks first.",
            () => DeleteAsync(changes.ToList()));
        if (changes.Count == 1 && changes[0].Versioned)
            Add("Blame", "\uE7B3", "Who last changed each line of this file, and in which revision.",
                () =>
                {
                    var path = changes[0].Path;
                    Go(() => new BlamePage(path, null, _co) { Checkout = Checkout }, "blame:" + path);
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
    /// Which load the page is on. A read that comes back after another reload started checks this and
    /// drops its answer instead of drawing over the newer one.
    /// </summary>
    int _generation;

    /// <summary>reselect keeps one file open across a reload, for example after a block of it was reverted.</summary>
    async Task LoadAsync(string? reselect = null)
    {
        var root = Session.Require();
        var gen = ++_generation;
        if (_filter.Count == 0) FilesSkeleton.Show();
        var changes = await Runner.Quiet(Pane, () => Ops.CheckoutChanges(root, _co));
        FilesSkeleton.Hide();
        if (changes == null || gen != _generation) return;
        _rows = changes.Select(c => new SvnChangeRow
        {
            Change = c,
            Checked = c.Versioned && c.Item is not ("conflicted" or "obstructed"),
            Display = $"{c.Code}  {(c.Wc.Length == 0 ? "root" : c.Wc)}  {c.Path}",
        }).ToList();
        foreach (var r in _rows) r.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(SvnChangeRow.Checked)) SyncCommitButton(); };
        _filter.SetItems(_rows, "Changes");
        Clean.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var groups = changes.Select(c => c.Wc).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Summary.Text = _rows.Count == 0 ? "" : $"{_rows.Count} change(s) in {groups} working cop{(groups == 1 ? "y" : "ies")}. Unversioned files are unchecked.";
        SyncCommitButton();
        _ = ShelfActions.ShowCountAsync(ShelfButton, CleanShelfButton, _co.Name, null, () => gen == _generation);
        if (reselect != null)
            _filter.Select(r => ((SvnChangeRow)r).Change.Path.Equals(reselect, StringComparison.OrdinalIgnoreCase));
        else if (_autoSelect && !_filter.HasPick) _filter.SelectFirstFile();
        if (_rows.Count == 0) { Clear(); Diff.ShowText("", "the checkout is clean"); return; }

        // svn has no way to count lines without handing over the whole patch, so this one read is both
        // the "all files" view and the numbers beside each row. Nobody waits for it: on a checkout with
        // hundreds of edits it is seconds, and the list and the buttons work without it.
        var wcs = changes.Where(c => c.Versioned).Select(c => c.Wc).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        const string title = "all versioned changes in the checkout, unified. Unversioned files are not in it.";
        if (!_filter.HasPick) { Clear(); Diff.BeginLoading(title); }
        _ = ShowPatchAsync(gen, wcs, title);
    }

    async Task ShowPatchAsync(int gen, List<string> wcs, string title)
    {
        var svn = Session.Require().Svn;
        var wanted = !_filter.HasPick;
        // One svn process per working copy, and a lazy Select ran them one after another inside the one
        // Task.Run. Fan.Map runs them side by side and keeps the input order, so the joined patch is the same.
        var patch = await Task.Run(() =>
            string.Join("\n", Fan.Map(wcs, wc => svn.DiffLocal(_co.Path, wc.Length == 0 ? "." : wc))));
        if (gen != _generation) return;
        _filter.SetStats(DiffStats.Parse(patch));
        if (wanted && !_filter.HasPick) Diff.ShowUnified(patch, title);
    }

    void Clear()
    {
        _shownPath = null;
        _shownPatch = null;
        _shownModified = "";
        Diff.SetActions(Array.Empty<DiffView.DiffAction>());
    }

    async void OnPicked(TreeNode node)
    {
        var svn = Session.Require().Svn;
        Clear();
        if (node.Row is not SvnChangeRow row || (node.IsFolder && Directory.Exists(PathUtil.Join(_co.Path, row.Change.Path))))
        {
            // A folder, or a whole working copy: every versioned change under it, as one patch.
            var folder = node.FullPath;
            var title = $"{(folder.Length == 0 ? "root" : folder)}   {node.FileCount} file(s), BASE → working copy, unified. Unversioned files are not in it.";
            Diff.BeginLoading(title);
            var patch = await Task.Run(() => svn.DiffLocal(_co.Path, folder.Length == 0 ? "." : folder));
            if (_filter.IsCurrent(node)) Diff.ShowUnified(patch, title);
            return;
        }
        var c = row.Change;
        var abs = PathUtil.Join(_co.Path, c.Path);
        if (Directory.Exists(abs))
        {
            Diff.ShowText("folder: " + c.Path + (c.Item == "unversioned" ? "\n\nUnversioned. Checking it adds everything under it." : ""), c.Path);
            return;
        }
        // A block can go back to BASE when there is a BASE and a file on disk to write.
        var hasBase = c.Item is "modified" or "replaced";

        var sides = await Diff.ShowFileAsync(c.Path, $"{c.Path}   BASE → working copy", new DiffView.Reads(
                () => c.Item is "unversioned" or "added" ? "" : svn.CatBase(_co.Path, c.Path),
                () => File.Exists(abs) ? ReadTextSafe(abs) : "",
                () => hasBase ? svn.DiffLocal(_co.Path, c.Path) : ""),
            () => _filter.IsCurrent(node), editable: File.Exists(abs));

        if (sides == null) return;
        _shownPath = c.Path;
        _shownModified = sides.Value.Modified;
        _shownPatch = hasBase ? Patch.FileFor(Patch.Parse(sides.Value.Unified), c.Path) : null;
        Diff.SetActions(hasBase
            ? new[] { DiffBlocks.DiscardToBaseAction, DiffBlocks.SaveAction, DiffBlocks.UndoAction }
            : new[] { DiffBlocks.SaveAction, DiffBlocks.UndoAction });
        SyncBlockButtons();
    }

    /// <summary>What the buttons over the diff say and whether they are on, for wherever the cursor is now.</summary>
    void SyncBlockButtons()
    {
        var blocks = DiffBlocks.Under(_shownPatch, Diff.Selection);
        Diff.SetActionState(DiffBlocks.DiscardToBase, blocks.Count > 0, DiffBlocks.Label("Discard", blocks));
        Diff.SetActionState(DiffBlocks.Save, Diff.IsDirty);
        Diff.SetActionState(DiffBlocks.Undo, Diff.IsDirty);
    }

    async void OnDiffAction(string id)
    {
        if (id == DiffBlocks.Save) { await SaveAsync(); return; }
        if (id == DiffBlocks.Undo) { await LoadAsync(_shownPath); return; }
        if (id != DiffBlocks.DiscardToBase) return;

        var path = _shownPath;
        var blocks = DiffBlocks.Under(_shownPatch, Diff.Selection);
        if (path == null || blocks.Count == 0) return;
        if (Diff.IsDirty)
        {
            await Dialogs.Info(this, "Unsaved edits", "The editor holds changes the file on disk does not. Save them, or undo them, before discarding a block.");
            return;
        }
        // A revert writes the file back to BASE. SVN has no index to hold the change and nothing on this
        // page has committed it anywhere, so what it takes out has no other copy left.
        if (!await Dialogs.Confirm(this, "Discard " + DiffBlocks.Label("discard", blocks).ToLowerInvariant(),
                $"Put {(blocks.Count == 1 ? "this block" : $"these {blocks.Count} blocks")} of {path} back the way SVN has them?\n\n"
                + "The change is not in SVN. Undo on the bar over the page brings it back, for as long as the file is left as the discard leaves it.", "Discard"))
            return;
        var abs = PathUtil.Join(_co.Path, path);
        var before = _shownModified;
        var what = DiffBlocks.Label("discard", blocks).ToLowerInvariant() + " of " + path;
        var written = await Runner.Run(Pane, what, () =>
        {
            var file = TextFile.Read(abs);
            if (file.Text != before) throw new SgException("the file changed on disk since this diff was read. Refresh, then pick the block again.");
            var after = Patch.Reverse(file.Text, blocks);
            TextFile.Write(abs, after, file.Encoding);
            return new { After = after, file.Encoding };
        });
        if (written == null) return;
        Discards.Announce(ResultBar, what, "The text it wrote over is kept until the bar is closed.",
            Discards.Rewrite(Pane, abs, before, written.After, written.Encoding), () => LoadAsync(reselect: path));
        await LoadAsync(reselect: path);
    }

    /// <summary>The editor's text goes to disk. The file must still hold what the diff was read from.</summary>
    async Task SaveAsync()
    {
        var path = _shownPath;
        if (path == null || !Diff.IsDirty) return;
        var text = await Diff.ModifiedTextAsync();
        var abs = PathUtil.Join(_co.Path, path);
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
            if (new FileInfo(path).Length > 4 * 1024 * 1024) return "\0";
            return TextFile.Read(path).Text;
        }
        catch (IOException) { return ""; }
    }

    void All_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Checked = true; SyncCommitButton(); }
    void None_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Checked = false; SyncCommitButton(); }

    List<string> Checked() => _rows.Where(r => r.Checked).Select(r => r.Change.Path).ToList();

    /// <summary>The button opens the message once something is ticked; the message decides whether the commit can go.</summary>
    void SyncCommitButton()
    {
        var picked = _rows.Count(r => r.Checked);
        CommitButton.IsEnabled = picked > 0;
        Message.Ready = picked > 0;
        CommitLabel.Text = picked == 0 ? "Commit to SVN" : $"Commit {picked} to SVN";
    }

    /// <summary>The left half of the split button. Its right half drops the menu and never gets here.</summary>
    async void Commit_Click(SplitButton sender, SplitButtonClickEventArgs e) => await CommitAsync();

    async Task CommitAsync()
    {
        if (!await Message.AskAsync()) return;
        var root = Session.Require();
        var paths = Checked();
        var msg = Message.Clean;
        await Busy.During(CommitButton, async () =>
        {
            ResultBar.IsOpen = false;
            var r = await Runner.Run(Pane, "svn commit", () => Ops.SvnCommit(root, _co, paths, msg));
            if (r != null)
            {
                foreach (var g in r.Groups)
                    Pane.Append($"  {(g.Wc.Length == 0 ? "root" : g.Wc),-30} {g.State,-10}" + (g.Revision.HasValue ? $" r{g.Revision}" : "") + (g.Error != null ? "  " + g.Error.Split('\n')[0] : ""));
                ResultBar.ActionButton = null;
                ResultBar.Severity = r.AllCommitted ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                ResultBar.Message = r.AllCommitted
                    ? $"Committed. Snapshot is now r{r.Sync?.Revision}."
                    : "Some working copies did not commit. See the log. Their changes stay in the checkout.";
                ResultBar.IsOpen = true;
                if (r.AllCommitted) { MessageDialog.Remember(msg); Message.Text = ""; }
            }
            await LoadAsync();
        }, restoreEnabled: false);
    }

    async Task RevertAsync(List<string> paths)
    {
        if (paths.Count == 0) { await Dialogs.Info(this, "Nothing checked", "Check the changes to revert."); return; }
        var root = Session.Require();
        var what = paths.Count == 1 ? $"the changes in {paths[0]}" : $"{paths.Count} change(s) in the checkout";
        if (!await Dialogs.Confirm(this, "Discard changes",
                $"Put {what} back the way SVN has them? Unversioned files get deleted.\n\n"
                + "They go onto the shelf first, so Undo on the bar over the page brings them back. A discard nobody asks back for is dropped after a week. "
                + "A file whose svn property changed is reverted outright: a shelf cannot hold a property.",
                "Discard")) return;
        // The shelf is the discard: saving one reverts the files. What it cannot hold is reverted the old way.
        var shelf = await Discards.ShelveAsync(Pane, _co.Path, paths,
            rest => Ops.SvnRevert(root, _co, rest.ToList(), deleteUnversioned: true));
        if (shelf != null)
            Discards.Announce(ResultBar, what, $"They wait on the shelf as \"{shelf.Title}\" for a week, or until you drop them.",
                Discards.Restore(Pane, shelf), () => LoadAsync());
        await LoadAsync();
    }

    /// <summary>
    /// svn:ignore on the folder each file sits in, the way TortoiseSVN does it. That property change is
    /// itself a local change of that folder, so the list gains it and the next commit of that folder carries it.
    /// </summary>
    async Task IgnoreAsync(List<string> paths)
    {
        if (paths.Count == 0) return;
        var what = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} name(s)";
        if (!await Dialogs.Confirm(this, "Add to ignore list",
            $"Add {what} to svn:ignore on the containing folder? The property change is a change of that folder, and goes to the server with the next commit of it.", "Ignore")) return;
        var root = Session.Require();
        await Runner.Run(Pane, "svn:ignore", () =>
        {
            foreach (var group in paths.GroupBy(p => PathUtil.Rel(Path.GetDirectoryName(p) ?? ""), StringComparer.OrdinalIgnoreCase))
                root.Svn.AddToIgnore(_co.Path, group.Key, group.Select(p => Path.GetFileName(p)));
        });
        await LoadAsync();
    }

    async Task DeleteAsync(List<Ops.SvnChange> changes)
    {
        if (changes.Count == 0) return;
        var what = changes.Count == 1 ? changes[0].Path : $"{changes.Count} file(s)";
        if (!await Dialogs.Confirm(this, "Delete from disk", $"Delete {what}? A versioned file is deleted through svn, so the deletion is a change to commit. This cannot be undone.", "Delete")) return;
        var root = Session.Require();
        await Runner.Run(Pane, "delete", () =>
        {
            var versioned = changes.Where(c => c.Versioned && c.Item != "deleted").Select(c => c.Path).ToList();
            if (versioned.Count > 0) root.Svn.Rm(_co.Path, versioned);
            foreach (var c in changes.Where(c => !c.Versioned))
            {
                var abs = PathUtil.Join(_co.Path, c.Path);
                if (File.Exists(abs)) File.Delete(abs);
                else if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
            }
        });
        await LoadAsync();
    }

    /// <summary>
    /// The checked changes leave the checkout and wait in the store. This is the way out of the refusal a
    /// push makes when the checkout has an edit on a file the branch also changed: shelve, push, put back.
    /// </summary>
    async Task ShelveAsync(List<string> paths)
    {
        if (paths.Count == 0) { await Dialogs.Info(this, "Nothing checked", "Check the changes to shelve."); return; }
        var what = paths.Count == 1
            ? "Put the changes to " + paths[0] + " aside."
            : $"Put {paths.Count} change(s) of {_co.Name} aside.";
        var r = await ShelfActions.SaveAsync(this, Pane, _co.Path, paths, what, ShelfActions.Suggest(paths));
        if (r == null) return;
        ResultBar.ActionButton = null;
        ResultBar.Severity = InfoBarSeverity.Success;
        ResultBar.Message = $"{r.Shelf.Count} file(s) are on the shelf as \"{r.Shelf.Title}\". The checkout holds what SVN has for them again.";
        ResultBar.IsOpen = true;
        await LoadAsync();
    }

    async void Shelve_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => ShelveAsync(Checked()));

    void Shelf_Click(object sender, RoutedEventArgs e) =>
        Go(() => new ShelfPage(_co) { Checkout = _co.Name }, "shelf:" + _co.Name);

    async void Revert_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => RevertAsync(Checked()));

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // The same rule the git changes page follows: a reload replaces what is in the editor, so it asks.
        if (Diff.IsDirty && !await Dialogs.Confirm(this, "Unsaved edits",
                $"The editor holds changes {_shownPath} on disk does not have. Refreshing reads the file again and loses them.", "Refresh anyway"))
            return;
        await Busy.During(sender, () => LoadAsync(_shownPath));
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
