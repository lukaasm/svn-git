using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// The shelf: changes that left a working copy and are waiting to come back. One list of them, the files
/// of the picked one, and its diff. Putting one back writes it into the folder it came from; a file that
/// moved on in the meantime gets the change merged into it, the way an svn update merges.
/// </summary>
public sealed partial class ShelfPage : SgPage
{
    readonly CheckoutConfig? _co;
    readonly string? _worktree;
    readonly string? _branch;
    readonly ListFilter _filter;
    List<ShelfRow> _rows = new();
    ShelfInfo? _picked;

    /// <summary>
    /// Which read of the list is the current one, and which read of one shelf is. They are counted apart
    /// on purpose: picking a row while the list is still loading used to raise the one number the load
    /// was watching, so the load threw its own answer away and left the list as it was.
    /// </summary>
    int _generation;
    int _pick;

    /// <summary>
    /// The shelves of one checkout, of one branch, or every one of them. What is given decides which,
    /// and where the "Open the changes" button of the empty page goes.
    /// </summary>
    public ShelfPage(CheckoutConfig? co = null, string? worktree = null, string? branch = null)
    {
        InitializeComponent();
        _co = co;
        _worktree = worktree;
        _branch = branch;
        Title = "Shelved changes";
        Checkout = co?.Name;
        Branch = branch;
        Subtitle = branch != null ? $"{branch}   {worktree}" : co != null ? $"{co.Name}   {co.Path}" : "every shelf in this root";
        ColumnSplitter.Attach(Splitter);
        Shortcuts.DiffNavigation(this, Diff);
        Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync());
        FileActions.Attach(Files, n => TargetOf(n.FullPath));
        _filter = new ListFilter(Filter, Files, FilesHeader, r => ((FileRow)r).Display);
        _filter.Picked += OnPicked;
        _filter.ExpectStats = true;
        EmptyChanges.Visibility = _co != null || _worktree != null ? Visibility.Visible : Visibility.Collapsed;
        Session.Log.Sink = Pane;
        _ = LoadAsync();
    }

    /// <summary>Where a file of the shelf lives on disk, for the right click menu that opens it.</summary>
    string TargetOf(string rel) => PathUtil.Join(_picked?.Path ?? _worktree ?? _co?.Path ?? "", rel);

    async Task LoadAsync(string? reselect = null)
    {
        var root = Session.Require();
        var gen = ++_generation;
        if (_rows.Count == 0) ShelvesSkeleton.Show();
        var keep = reselect ?? _picked?.Id;
        var all = await Runner.Quiet(Pane, () =>
            _branch != null ? Shelf.For(root, null, _branch)
            : _co != null ? Shelf.For(root, _co.Name, null)
            : Shelf.List(root));
        ShelvesSkeleton.Hide();
        if (all == null || gen != _generation) return;

        _rows = all.Select(s => new ShelfRow { Shelf = s }).ToList();
        Shelves.ItemsSource = _rows;
        var none = _rows.Count == 0;
        Empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
        ListHeader.Text = none ? "Shelves" : _rows.Count == 1 ? "Shelves (1)" : $"Shelves ({_rows.Count})";
        if (none) { ShowPicked(null); return; }
        Shelves.SelectedItem = _rows.FirstOrDefault(r => r.Shelf.Id == keep) ?? _rows[0];
    }

    void Shelves_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowPicked((Shelves.SelectedItem as ShelfRow)?.Shelf);

    /// <summary>
    /// One of the three buttons is running. Busy.During only disables the one that was pressed, and the
    /// other two act on the same shelf: Drop while Put back was still writing threw the shelf away
    /// underneath it, and what was half written stayed half written.
    /// </summary>
    bool _working;

    void SyncActions()
    {
        var shelf = _picked;
        RestoreButton.IsEnabled = !_working && shelf != null && !shelf.Gone;
        KeepButton.IsEnabled = !_working && shelf != null && !shelf.Gone;
        DropButton.IsEnabled = !_working && shelf != null;
    }

    /// <summary>Runs one shelf action with the other two off, and gives them back whatever happens.</summary>
    async Task GuardedAsync(object sender, Func<Task> work)
    {
        if (_working) return;
        _working = true;
        SyncActions();
        try { await Busy.During(sender, work, restoreEnabled: false); }
        finally { _working = false; SyncActions(); }
    }

    async void ShowPicked(ShelfInfo? shelf)
    {
        _picked = shelf;
        var gen = ++_pick;
        SyncActions();
        GoneBar.IsOpen = shelf?.Gone == true;
        if (shelf == null)
        {
            DetailHead.Text = "";
            DetailWhere.Text = "";
            _filter.Clear("Files");
            Diff.ShowText("", "nothing picked");
            return;
        }
        DetailHead.Text = shelf.Title;
        DetailWhere.Text = $"{shelf.Count} file(s) from {(shelf.IsCheckout ? "checkout " + shelf.Checkout : "branch " + shelf.Branch)}"
                           + $", made {shelf.Created.ToLocalTime():yyyy-MM-dd HH:mm}\n{shelf.Path}";

        var root = Session.Require();
        FilesSkeleton.Show();
        var changes = await Runner.Quiet(Pane, () => Shelf.Changes(root, shelf));
        FilesSkeleton.Hide();
        if (changes == null || gen != _pick) return;
        _filter.SetItems(changes.Select(c => new FileRow
        {
            Path = c.Path,
            Status = c.Status,
            Display = $"{c.Status}  {c.Path}",
        }).ToList(), "Files");

        var title = $"{shelf.Title}   {changes.Count} file(s), what the shelf changes, unified";
        Diff.BeginLoading(title);
        var patch = await Task.Run(() => Shelf.PatchText(root, shelf));
        if (gen != _pick) return;
        _filter.SetStats(DiffStats.Parse(patch));
        if (!_filter.HasPick) Diff.ShowUnified(patch, title);
    }

    async void OnPicked(TreeNode node)
    {
        var shelf = _picked;
        if (shelf == null) return;
        var root = Session.Require();
        if (node.Row is not FileRow row)
        {
            // A folder: everything under it, as one patch of the shelf.
            var folder = node.FullPath;
            var title = $"{folder}   {node.FileCount} file(s) of this shelf, unified";
            Diff.BeginLoading(title);
            var patch = await Task.Run(() => Shelf.PatchText(root, shelf, folder));
            if (_filter.IsCurrent(node)) Diff.ShowUnified(patch, title);
            return;
        }
        await Diff.ShowFileAsync(row.Path, $"{row.Path}   before → as the shelf holds it",
            new DiffView.Reads(
                () => Shelf.Side(root, shelf, row.Path, shelved: false),
                () => Shelf.Side(root, shelf, row.Path, shelved: true)),
            () => _filter.IsCurrent(node));
    }

    // ---- what can be done to one ----

    async void Restore_Click(object sender, RoutedEventArgs e) => await GuardedAsync(sender, () => RestoreAsync(keep: false));

    async void RestoreKeep_Click(object sender, RoutedEventArgs e) => await GuardedAsync(sender, () => RestoreAsync(keep: true));

    async Task RestoreAsync(bool keep)
    {
        var shelf = _picked;
        if (shelf == null) return;
        var root = Session.Require();
        ResultBar.IsOpen = false;
        var r = await Runner.Run(Pane, "put back " + shelf.Id, () => Shelf.Restore(root, shelf.Id, keep));
        if (r == null) return;
        var written = r.Written.Count + r.Deleted.Count;
        if (r.Conflicted.Count > 0)
        {
            ResultBar.Severity = InfoBarSeverity.Warning;
            ResultBar.Message = $"{written} file(s) are back. {r.Conflicted.Count} could not be merged and hold conflict markers now: "
                                + string.Join(", ", r.Conflicted.Take(5))
                                + ". The shelf was kept, so nothing is lost while you sort them out.";
        }
        else
        {
            ResultBar.Severity = InfoBarSeverity.Success;
            ResultBar.Message = $"{written} file(s) are back in {shelf.Path}."
                                + (r.Merged.Count > 0 ? $" {r.Merged.Count} had moved on and were merged." : "")
                                + (r.Kept ? " The shelf was kept." : "");
        }
        ResultBar.IsOpen = true;
        foreach (var p in r.Merged) Pane.Append("merged: " + p);
        foreach (var p in r.Conflicted) Pane.Append("conflict: " + p);
        await LoadAsync();
    }

    async void Drop_Click(object sender, RoutedEventArgs e)
    {
        var shelf = _picked;
        if (shelf == null) return;
        if (!await Dialogs.Confirm(this, "Drop this shelf",
                $"Throw away \"{shelf.Title}\" and the {shelf.Count} file(s) in it? They were taken out of the working copy, so this is the only copy left.", "Drop"))
            return;
        var root = Session.Require();
        await GuardedAsync(sender, async () =>
        {
            await Runner.Run(Pane, "drop " + shelf.Id, () => Shelf.Drop(root, shelf.Id));
            await LoadAsync();
        });
    }

    void Changes_Click(object sender, RoutedEventArgs e)
    {
        if (_worktree != null && _branch != null)
            Go(() => new CommitPage(_worktree) { Checkout = Checkout, Branch = _branch }, "commit:" + _worktree);
        else if (_co != null)
            Go(() => new SvnCommitPage(_co) { Checkout = _co.Name }, "svnchanges:" + _co.Name);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
