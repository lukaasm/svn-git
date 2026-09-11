using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// The SVN history of the checkout and every external in one list, with changed paths and diffs from the
/// server. Opened from Sync it shows only what a sync would bring in, and hands the sync back to the
/// overview when the user says go.
/// </summary>
public sealed partial class SvnLogPage : SgPage
{
    sealed record WcChoice(string Rel, string Url, string ReposRoot, long WcRevision, long? SnapshotRevision);

    readonly CheckoutConfig _co;
    readonly ListFilter _paths;
    List<WcChoice> _wcs = new();
    WcChoice? _wc;
    SvnLogRevision? _rev;
    readonly bool _autoSelect;
    readonly bool _incoming;

    /// <summary>The user pressed Sync now. Whoever opened the page does the sync.</summary>
    public bool SyncRequested { get; private set; }

    public SvnLogPage(CheckoutConfig co, bool autoSelect = false, bool incoming = false)
    {
        InitializeComponent();
        _co = co;
        _autoSelect = autoSelect;
        _incoming = incoming;
        SyncButton.Visibility = EmptySync.Visibility = incoming ? Visibility.Visible : Visibility.Collapsed;
        ColumnSplitter.Attach(Splitter);
        Shortcuts.DiffNavigation(this, Diff);
        // The one file list in the app that had no right click at all. Reading a revision is exactly when
        // "open the folder this is in" and "who wrote this line" come up, and both were three pages away.
        FileActions.Attach(Paths, LocalOf, (menu, node) =>
        {
            // Gated the way this page's own diff code gates it, and then on the file being there.
            // IsFolder is Children.Count > 0, which is not a directory test: a revision that deletes a
            // whole folder logs one path of kind "dir" with nothing under it, and filtering the list
            // flattens every node, so IsFolder is false for all of them. Kind is what actually says.
            if (node.Row is not SvnPathRow row || row.Path.Kind == "dir") return;
            var rel = CheckoutRelative(node);
            // And blame reads the file in the checkout, so a path this revision deleted, or one that
            // belongs to another working copy, has nothing to read: no item rather than an error.
            if (rel == null || !File.Exists(PathUtil.Join(_co.Path, rel))) return;
            var item = new MenuFlyoutItem { Text = "Blame", Icon = new FontIcon { Glyph = "" } };
            ToolTipService.SetToolTip(item, "Who last changed each line of this file, and in which revision.");
            // The file as it is in the checkout, not as it was at this revision: blame is read forwards
            // from now, and a line's answer is the revision that last touched it either way.
            item.Click += (_, _) => Go(() => new BlamePage(rel, null, _co) { Checkout = Checkout }, "blame:" + rel);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(item);
        });
        _paths = new ListFilter(PathsFilter, Paths, PathsHeader, r => ((SvnPathRow)r).Display);
        _paths.Picked += OnPicked;
        _paths.ExpectStats = true;
        Title = incoming ? "Incoming changes" : "SVN log";
        Checkout = co.Name;
        Subtitle = $"{co.Name}   {co.Path}";
        NoRevisions.Title = incoming ? "Up to date with the server" : "No revisions";
        NoRevisions.Text = incoming
            ? "A sync brings nothing new in. It still takes a fresh snapshot, which is what branches build on."
            : "The server has no revisions for these working copies.";
        Session.Log.Sink = Pane;
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        var root = Session.Require();
        RevisionsSkeleton.Show();
        var wcs = await Runner.Quiet(Pane, () =>
        {
            var svn = root.Svn;
            var status = svn.Status(_co.Path, noIgnore: false);
            var rels = new List<string> { "" };
            rels.AddRange(status.Where(e => e.Item == "external" && e.Path.Length > 0).Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal));
            var infos = svn.InfoMany(_co.Path, rels.Select(r => r.Length == 0 ? "." : r), recursive: false);
            var snapSha = root.Git.RefSha(root.SnapshotRef(_co));
            var meta = snapSha != null ? SnapshotMeta.Parse(root.Git.Body(snapSha)) : null;
            var list = new List<WcChoice>();
            foreach (var rel in rels)
            {
                var info = infos.FirstOrDefault(i => i.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
                if (info == null) continue;
                long? snapRev = meta == null ? null : rel.Length == 0 ? meta.Revision : meta.Externals.TryGetValue(rel, out var er) ? er : null;
                list.Add(new WcChoice(rel, info.Url.TrimEnd('/'), info.ReposRoot.TrimEnd('/'), info.Revision, snapRev));
            }
            return list;
        });
        if (wcs == null)
        {
            // The reason is in the strip; the page shows what it still offers rather than a live skeleton.
            RevisionsSkeleton.Hide();
            NoRevisions.Title = "Could not read the checkout";
            NoRevisions.Text = "The working copies could not be read. The line at the bottom says why. Reload tries again.";
            ShowEmpty(true);
            return;
        }
        _wcs = wcs;
        if (wcs.Count == 0) { RevisionsSkeleton.Hide(); return; }
        await LoadAsync();
    }

    async void Reload_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => LoadAsync());

    /// <summary>No revision to show: the sentence is the whole page, with the sync it still offers.</summary>
    void ShowEmpty(bool empty)
    {
        NoRevisions.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The sync itself belongs to the overview: it has the log pane, the toasts and the refresh.</summary>
    void Sync_Click(object sender, RoutedEventArgs e)
    {
        SyncRequested = true;
        Close();
    }

    /// <summary>
    /// Every working copy at once, merged into one history and sorted by date. The monorepo is the root plus
    /// its externals, they move independently, and what a day looked like across all of them is the question
    /// this page is asked; the coloured tag on each row says which repository it came from.
    /// </summary>
    async Task LoadAsync()
    {
        if (_wcs.Count == 0) return;
        var root = Session.Require();
        var limit = double.IsNaN(Limit.Value) ? 100 : (int)Limit.Value;
        var wcs = _wcs;
        WcState.Text = $"{wcs.Count} working copies, reading the server...";
        if (Revisions.ItemsSource == null) RevisionsSkeleton.Show();
        ShowEmpty(false);
        // The URL, not the working copy path: a working copy path only shows history up to its own revision.
        var logs = await Runner.Quiet(Pane, () =>
            Task.WhenAll(wcs.Select(wc => Task.Run(() => (wc, entries: root.Svn.LogVerbose(_co.Path, wc.Url, limit))))));
        if (!ReferenceEquals(wcs, _wcs)) return;
        RevisionsSkeleton.Hide();
        if (logs == null)
        {
            // An unreachable server, a refused password, a repository that moved: the strip says which, and
            // Reload and Sync stay on screen. The skeleton used to pulse here for as long as the page lived.
            WcState.Text = "";
            NoRevisions.Title = "Could not read the server";
            NoRevisions.Text = "The line at the bottom says why. Reload tries again.";
            ShowEmpty(true);
            return;
        }
        var rows = new List<SvnRevRow>();
        var behind = new List<string>();
        var colour = 0;
        foreach (var (wc, entries) in logs)
        {
            // A sync does two things: svn update the working copy, and take a snapshot for the branches to
            // build on. So what it brings in is everything above whichever of the two is further behind.
            var floor = Math.Min(wc.WcRevision, wc.SnapshotRevision ?? wc.WcRevision);
            var pending = entries.Count(r => r.Revision > (_incoming ? floor : wc.WcRevision));
            var snap = wc.SnapshotRevision.HasValue
                ? (wc.SnapshotRevision == wc.WcRevision ? "the snapshot matches it" : $"the snapshot svn/{_co.Name} is at r{wc.SnapshotRevision}, run Sync")
                : "no snapshot yet";
            var tip = $"{(wc.Rel.Length == 0 ? "root" : wc.Rel)}\n{wc.Url}\nWorking copy at r{wc.WcRevision}, {snap}. "
                      + (pending == 0 ? "Nothing newer on the server." : $"{pending}{(pending >= limit ? "+" : "")} newer revision(s) on the server, not synced yet.");
            if (pending > 0) behind.Add($"{(wc.Rel.Length == 0 ? "root" : wc.Rel)} {pending}{(pending >= limit ? "+" : "")}");
            rows.AddRange(entries.Where(r => !_incoming || r.Revision > floor).Select(r => new SvnRevRow
            {
                Revision = r.Revision,
                Author = r.Author,
                Date = Msg.Day(r.Date),
                Subject = Msg.Subject(r.Message),
                Entry = r,
                Group = wc.Rel,
                RepoColor = colour,
                RepoTip = tip,
                ShowRepo = true,
                Mark = _incoming || r.Revision > wc.WcRevision ? RevMark.Behind
                    : r.Revision == wc.WcRevision ? RevMark.Current : RevMark.None,
            }));
            colour++;
        }
        // One history out of all of them: the repositories move independently, and what a day looked like
        // across the monorepo is the question this page is asked. The tag says where each row came from.
        rows = rows.OrderByDescending(r => r.Entry.Date, StringComparer.Ordinal).ThenByDescending(r => r.Revision).ToList();
        Revisions.ItemsSource = rows;
        ShowEmpty(rows.Count == 0);
        RevisionsHeader.Text = _incoming
            ? $"A sync brings in these ({rows.Count})"
            : $"Revisions, newest first ({rows.Count})";
        WcState.Text = behind.Count == 0
            ? (_incoming
                ? $"{wcs.Count} working copies, nothing to bring in. Sync still takes a fresh snapshot."
                : $"{wcs.Count} working copies, all up to date with the server.")
            : $"{wcs.Count} working copies, newer on the server: {string.Join(", ", behind)}.";
        SyncButton.IsEnabled = true;
        _paths.Clear("Changed paths");
        DetailHead.Text = "";
        DetailMessage.Text = "";
        _rev = null;
        _wc = null;
        Diff.ShowText("", rows.Count == 0 ? "no revisions" : "pick a revision to see what it changed");
        if (_autoSelect && rows.Count > 0) Revisions.SelectedItem = rows[0];
    }

    async void Revisions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Revisions.SelectedItem is not SvnRevRow row) return;
        // The diff comes from the working copy the row sits under, not from whichever one was picked last.
        _wc = _wcs.FirstOrDefault(w => w.Rel.Equals(row.Group, StringComparison.OrdinalIgnoreCase));
        if (_wc == null) return;
        _rev = row.Entry;
        DetailHead.Text = $"r{row.Revision}   {row.Entry.Author}   {Msg.When(row.Entry.Date)}";
        DetailMessage.Text = Msg.Body(row.Entry.Message);
        _paths.SetItems(row.Entry.Paths.Select(p => new SvnPathRow
        {
            Path = p,
            Display = $"{p.Action}  {p.Path}" + (p.CopyFrom != null ? $"  (from {p.CopyFrom})" : ""),
        }).ToList(), "Changed paths");
        // --select walks all the way to a file diff, so a check can see what the page really draws.
        if (_autoSelect) _paths.SelectFirstFile();
        var svn = Session.Require().Svn;
        var url = _wc.Url;
        var rev = row.Revision;
        var title = $"r{rev}  all files, unified";
        Diff.BeginLoading(title);
        var patch = await Task.Run(() => svn.DiffRevision(url, rev));
        if (_rev?.Revision != rev) return;
        _paths.SetStats(DiffStats.Parse(patch));
        if (!_paths.HasPick) Diff.ShowUnified(patch, title);
    }

    async void OnPicked(TreeNode node)
    {
        if (_rev == null || _wc == null) return;
        var svn = Session.Require().Svn;
        var rev = _rev.Revision;
        if (node.Row is not SvnPathRow row || (node.IsFolder && row.Path.Kind == "dir"))
        {
            // A folder: everything the revision changed under it, as one patch from the server.
            var url = _wc.ReposRoot + "/" + node.FullPath.TrimStart('/');
            var title = $"{node.FullPath}   {node.FileCount} path(s), r{rev}, unified";
            Diff.BeginLoading(title);
            var patch = await Task.Run(() => svn.DiffRevision(url, rev));
            if (_paths.IsCurrent(node) && _rev?.Revision == rev) Diff.ShowUnified(patch, title);
            return;
        }
        var p = row.Path;
        if (p.Kind == "dir")
        {
            Diff.ShowText($"folder: {p.Path}", p.Path);
            return;
        }
        var fileUrl = _wc.ReposRoot + p.Path;
        await Diff.ShowFileAsync(p.Path, $"{p.Path}   r{rev - 1} → r{rev}", new DiffView.Reads(
                () => p.Action == "A" && p.CopyFrom == null ? "" : svn.CatUrl(fileUrl, rev - 1),
                () => p.Action == "D" ? "" : svn.CatUrl(fileUrl, rev)),
            // The revision can change under the selection too, so both have to still be the ones asked for.
            () => _paths.IsCurrent(node) && _rev?.Revision == rev);
    }

    /// <summary>
    /// Where a path in the log lives on this disk, or null when this checkout does not hold it. The log
    /// lists paths the way the repository does - /branches/fort/dev/x.cpp - and a working copy is one
    /// folder of a repository, so its own share of that path comes off the front before the rest can be
    /// joined onto the folder it sits in. A path belonging to another working copy comes back null.
    ///
    /// This is string surgery and nothing more: it says where the file would be, not that it is there.
    /// A path the revision deleted gets an answer here too, so whoever needs the file to exist has to
    /// ask the disk. The right click menu does.
    /// </summary>
    string? CheckoutRelative(TreeNode node)
    {
        if (_wc == null) return null;
        var root = _wc.ReposRoot.TrimEnd('/');
        var mine = _wc.Url.TrimEnd('/').StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? _wc.Url.TrimEnd('/')[root.Length..]
            : "";
        var p = "/" + node.FullPath.TrimStart('/');
        if (mine.Length > 0)
        {
            if (!p.StartsWith(mine + "/", StringComparison.OrdinalIgnoreCase)
                && !p.Equals(mine, StringComparison.OrdinalIgnoreCase)) return null;
            p = p[mine.Length..];
        }
        var inside = p.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return _wc.Rel.Length == 0 ? inside : PathUtil.Join(_wc.Rel, inside);
    }

    string? LocalOf(TreeNode node) =>
        CheckoutRelative(node) is { } rel ? PathUtil.Join(_co.Path, rel) : null;

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
