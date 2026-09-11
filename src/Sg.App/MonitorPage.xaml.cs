using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

/// <summary>A category or a watched repository in the monitor tree.</summary>
public sealed class MonitorNode
{
    public string Name { get; set; } = "";
    public MonitorItem? Item { get; set; }
    public Thickness Indent => IsCategory ? new Thickness(0) : new Thickness(24, 0, 0, 0);
    public int Unread { get; set; }
    public string? Error { get; set; }
    public bool IsCategory => Item == null;
    // Written as escapes, because both of these were once literal glyphs and a scripted edit left two
    // empty strings behind: every row in the tree drew a blank where its icon is, and IconBrush - which
    // greys a paused repository and reddens a failing one - had nothing to colour.
    public string Glyph => IsCategory ? "\uE8B7" : "\uE71B";
    public Visibility UnreadVisibility => Unread > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ErrorVisibility => Error != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheckingVisibility => Item is { Checking: true } ? Visibility.Visible : Visibility.Collapsed;
    public Windows.UI.Text.FontWeight Weight => IsCategory || Unread > 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    public Brush IconBrush => (Brush)Application.Current.Resources[Error != null ? "SystemFillColorCriticalBrush" : (Item is { Enabled: false } ? "TextFillColorDisabledBrush" : "TextFillColorSecondaryBrush")];

    /// <summary>
    /// Everything this node draws. A check every few minutes ends in the same tree far more often than not,
    /// and rebinding an unchanged tree would slide every row in again under the user's eyes.
    /// </summary>
    public bool SameAs(MonitorNode o) =>
        Name == o.Name && Item?.Id == o.Item?.Id && Unread == o.Unread && Error == o.Error
        && Item?.Checking == o.Item?.Checking && Item?.Enabled == o.Item?.Enabled;

    /// <summary>
    /// What a screen reader announces. Without it every line of the tree reads out "Sg.App.MonitorNode",
    /// which is what a row with no name of its own falls back to.
    /// </summary>
    public override string ToString() =>
        Name + (Unread > 0 ? $", {Unread} unread" : "") + (Error != null ? ", error" : "");
}

/// <summary>Watched SVN URLs in categories, with their incoming commits.</summary>
public sealed partial class MonitorPage : SgPage
{
    readonly ListFilter _paths;
    MonitorItem? _selected;
    /// <summary>The repository whose revision details are on screen.</summary>
    MonitorItem? _shownItem;
    SvnLogRevision? _rev;
    bool _loaded;
    /// <summary>The page is selecting a revision itself; that is not the user reading it.</summary>
    bool _selecting;
    bool _rebuilding;
    bool _autoPicked;
    string? _pendingSelect;

    /// <summary>The category on screen, when the tree's pick is a group header and not one repository.</summary>
    string? _category;

    /// <summary>Which repository each row came from. A category list draws rows from several at once.</summary>
    readonly Dictionary<SvnRevRow, MonitorItem> _rowItem = new();

    /// <summary>The repository a row belongs to: its own in a category list, the picked one otherwise.</summary>
    MonitorItem? ItemOf(SvnRevRow row) => _rowItem.TryGetValue(row, out var i) ? i : _selected;

    public MonitorPage(string? selectId = null)
    {
        InitializeComponent();
        Title = "Project monitor";
        _pendingSelect = selectId;
        // The minimums match the column definitions, and their sum has to fit the overview's content
        // area, not a window of its own: this is the one page that lives behind the navigation pane.
        ColumnSplitter.Attach(TreeSplitter, minLeft: 170, minRight: 700);
        ColumnSplitter.Attach(ListSplitter, minLeft: 330, minRight: 360);
        Shortcuts.DiffNavigation(this, Diff);
        _paths = new ListFilter(PathsFilter, Paths, PathsHeader, r => ((SvnPathRow)r).Display);
        _paths.Picked += OnPicked;
        _paths.ExpectStats = true;
        MonitorService.Changed += OnChanged;
        Unloaded += (_, _) => MonitorService.Changed -= OnChanged;
        // Lists must not be filled or selected before the content is on screen. That crashes inside XAML.
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            Rebuild();
            if (_pendingSelect != null) SelectById(_pendingSelect);
            _pendingSelect = null;
        };
    }

    void OnChanged()
    {
        if (!_loaded) return;
        Rebuild();
        if (_category != null) ShowCategory(_category, keepSelection: true);
        else if (_selected != null) ShowItem(_selected, keepSelection: true);
    }

    void Rebuild()
    {
        var items = MonitorService.Store.Items;
        var nodes = new List<MonitorNode>();
        foreach (var g in items.GroupBy(i => i.Category, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new MonitorNode { Name = g.Key, Unread = g.Sum(i => i.Unread) });
            foreach (var i in g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                nodes.Add(new MonitorNode { Name = i.Name, Item = i, Unread = i.Unread, Error = i.Error });
        }
        _rebuilding = true;
        if (Tree.ItemsSource is List<MonitorNode> shown && shown.Count == nodes.Count
            && shown.Zip(nodes).All(p => p.First.SameAs(p.Second)))
            nodes = shown;
        else
            Tree.ItemsSource = nodes;
        if (_category != null) Tree.SelectedItem = nodes.FirstOrDefault(n => n.IsCategory && n.Name == _category);
        else if (_selected != null) Tree.SelectedItem = nodes.FirstOrDefault(n => n.Item?.Id == _selected.Id);
        _rebuilding = false;
        var total = MonitorService.TotalUnread;
        Subtitle = items.Count == 0 ? "nothing watched yet" : $"{items.Count} repositor{(items.Count == 1 ? "y" : "ies")}, {total} unread commit(s)";
        NothingWatched.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (items.Count == 0)
        {
            ItemHeader.Text = "Nothing watched yet";
            ItemState.Text = "Add a repository URL, or add your checkouts.";
        }
        else if (_category == null && (_selected == null || (_autoPicked && _selected.Unread == 0 && items.Any(i => i.Unread > 0))))
        {
            // Until the user picks one, follow the first repository that has something new.
            var first = items.FirstOrDefault(i => i.Unread > 0) ?? items.First();
            _rebuilding = true;
            Tree.SelectedItem = nodes.FirstOrDefault(n => n.Item?.Id == first.Id);
            _rebuilding = false;
            _autoPicked = true;
            ShowItem(first, keepSelection: false);
        }
    }

    /// <summary>Selects one watched repository, from a toast that named it.</summary>
    public void SelectById(string id)
    {
        if (!_loaded) { _pendingSelect = id; return; }
        if (Tree.ItemsSource is List<MonitorNode> nodes)
            Tree.SelectedItem = nodes.FirstOrDefault(n => n.Item?.Id == id);
    }

    void Tree_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        if (Tree.SelectedItem is MonitorNode { Item: { } item })
        {
            _autoPicked = false;
            if (!ReferenceEquals(item, _selected)) ShowItem(item, keepSelection: false);
        }
        else if (Tree.SelectedItem is MonitorNode { IsCategory: true } category)
        {
            // Every repository under it at once, the way the SVN log shows a monorepo: one history,
            // newest first, each line tagged with the working copy it came from.
            _autoPicked = false;
            ShowCategory(category.Name, keepSelection: false);
        }
    }

    /// <summary>One line of a revision list. showRepo tags it, which only a category list needs.</summary>
    static SvnRevRow Row(MonitorItem item, SvnLogRevision r, bool showRepo = false, int colour = 0) => new()
    {
        Revision = r.Revision,
        Author = r.Author,
        Date = Msg.Day(r.Date),
        Subject = Msg.Subject(r.Message),
        Entry = r,
        Mark = r.Revision > item.LastSeen ? RevMark.Unread : RevMark.None,
        Group = item.Name,
        ShowRepo = showRepo,
        RepoColor = colour,
        RepoTip = item.Url,
    };

    /// <summary>
    /// The oldest one not read yet, which is the bottom of the run of bold lines: the list is newest
    /// first, and reading forward in time is what makes "this and everything older is read" mean
    /// something. Nothing unread falls back to the newest line there is.
    /// </summary>
    static SvnRevRow? FirstUnread(List<SvnRevRow> rows) =>
        rows.LastOrDefault(r => r.Mark == RevMark.Unread) ?? rows.FirstOrDefault();

    void ShowItem(MonitorItem item, bool keepSelection)
    {
        var previousRev = keepSelection ? _rev?.Revision : null;
        _category = null;
        _rowItem.Clear();
        _selected = item;
        ItemHeader.Text = $"{item.Name}   ({item.Category})";
        CheckingRing.Visibility = item.Checking ? Visibility.Visible : Visibility.Collapsed;
        var checkedText = item.LastChecked.HasValue ? item.LastChecked.Value.ToLocalTime().ToString("HH:mm") : "never";
        ItemState.Text = $"{item.Url}\nHEAD r{item.Head}, seen up to r{item.LastSeen}, {item.Unread} unread. Checked {checkedText}, every {item.IntervalMinutes} min"
                         + (item.Enabled ? "" : ", paused") + (item.Notify ? ", toast on" : ", toast off") + (item.Checking ? ". Checking..." : "")
                         + (item.Error != null ? "\nerror: " + item.Error : "");
        var rows = item.Recent.Select(r => Row(item, r)).ToList();
        if (Revisions.ItemsSource is List<SvnRevRow> shownRows && shownRows.Count == rows.Count
            && shownRows.Zip(rows).All(p => p.First.SameAs(p.Second)))
            rows = shownRows;
        else
            Revisions.ItemsSource = rows;
        // A repository being read for the first time: bars where its revisions will be.
        if (item.Checking && rows.Count == 0) RevisionsSkeleton.Show(); else RevisionsSkeleton.Hide();
        // Nothing new takes the whole space right of the tree: the list, the message, the paths and the diff go with it.
        var empty = !item.Checking && rows.Count == 0;
        NoRevisions.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Detail.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        _selecting = true;
        try
        {
            if (previousRev != null)
            {
                var again = rows.FirstOrDefault(r => r.Revision == previousRev);
                if (again != null) Revisions.SelectedItem = again;
            }
            else
            {
                _paths.Clear("Changed paths");
                DetailHead.Text = "";
                DetailMessage.Text = "";
                _rev = null;
                var open = FirstUnread(rows);
                if (open != null) Revisions.SelectedItem = open;
                else Diff.ShowText("", item.Checking ? "reading the server..." : "no revisions yet");
            }
        }
        finally { _selecting = false; }
    }

    /// <summary>
    /// Every repository under one category, merged into one history and sorted newest first, each line
    /// tagged with the repository it came from. This is the question a category is asked - what happened
    /// across the monorepo today - and it is the same shape the SVN log gives a checkout and its externals.
    /// </summary>
    void ShowCategory(string name, bool keepSelection)
    {
        var previousRev = keepSelection ? _rev?.Revision : null;
        var previousItem = keepSelection ? _shownItem?.Id : null;
        _selected = null;
        _category = name;

        var members = MonitorService.Store.Items
            .Where(i => i.Category.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unread = members.Sum(i => i.Unread);
        var checking = members.Any(i => i.Checking);

        ItemHeader.Text = name;
        CheckingRing.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
        ItemState.Text = $"{members.Count} repositor{(members.Count == 1 ? "y" : "ies")}, {unread} unread commit(s). "
                         + "Every commit any of them has, newest first, tagged with the one it came from."
                         + (checking ? " Checking..." : "");

        // One colour per repository, handed out in the order the tree lists them, so an interleaved
        // history stays readable. Eight colours, and the tag wraps round after that.
        _rowItem.Clear();
        var rows = new List<SvnRevRow>();
        for (var i = 0; i < members.Count; i++)
            foreach (var r in members[i].Recent)
            {
                var row = Row(members[i], r, showRepo: true, colour: i);
                _rowItem[row] = members[i];
                rows.Add(row);
            }
        rows = rows.OrderByDescending(r => r.Entry.Date, StringComparer.Ordinal).ThenByDescending(r => r.Revision).ToList();
        Revisions.ItemsSource = rows;

        if (checking && rows.Count == 0) RevisionsSkeleton.Show(); else RevisionsSkeleton.Hide();
        var empty = !checking && rows.Count == 0;
        NoRevisions.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Detail.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        _selecting = true;
        try
        {
            var again = previousRev == null ? null
                : rows.FirstOrDefault(r => r.Revision == previousRev && ItemOf(r)?.Id == previousItem);
            if (again != null) { Revisions.SelectedItem = again; return; }
            _paths.Clear("Changed paths");
            DetailHead.Text = "";
            DetailMessage.Text = "";
            _rev = null;
            var open = FirstUnread(rows);
            if (open != null) Revisions.SelectedItem = open;
            else Diff.ShowText("", checking ? "reading the server..." : "no revisions yet");
        }
        finally { _selecting = false; }
    }

    /// <summary>
    /// The user has read this commit: it and everything older in its own repository are read, and what
    /// is newer stays unread. In a category list every row can name a different repository.
    /// </summary>
    void MarkRead(SvnRevRow row)
    {
        if (ItemOf(row) is not { } item) return;
        _autoPicked = false;
        MonitorService.MarkReadUpTo(item, row.Revision);
    }

    /// <summary>
    /// A press on the row that is already selected. SelectionChanged does not fire for it, and the row
    /// the page opens on is exactly the one a reader presses first, so the one commit sg picked for you
    /// was the one commit you could not mark read.
    /// </summary>
    void Revisions_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SvnRevRow row) MarkRead(row);
    }

    async void Revisions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Revisions.SelectedItem is not SvnRevRow row) return;
        if (ItemOf(row) is not { } item) return;
        // The same revision of the same repository is already on screen: the list was only rebuilt around it.
        var same = ReferenceEquals(_shownItem, item) && _rev?.Revision == row.Revision;
        _rev = row.Entry;
        _shownItem = item;
        // A press is the user reading it. A selection the page made itself is not.
        if (!_selecting) MarkRead(row);
        if (same) return;
        DetailHead.Text = $"r{row.Revision}   {row.Entry.Author}   {Msg.When(row.Entry.Date)}";
        DetailMessage.Text = Msg.Body(row.Entry.Message);
        _paths.SetItems(row.Entry.Paths.Select(p => new SvnPathRow
        {
            Path = p,
            Display = $"{p.Action}  {p.Path}" + (p.CopyFrom != null ? $"  (from {p.CopyFrom})" : ""),
        }).ToList(), "Changed paths");
        var svn = Session.Root?.Svn ?? new Svn("svn", new NullLog());
        var url = item.Url;
        var rev = row.Revision;
        var title = $"r{rev}  all files, unified";
        Diff.BeginLoading(title);
        var patch = await Task.Run(() => svn.DiffRevision(url, rev));
        if (_rev?.Revision != rev || !ReferenceEquals(_shownItem, item)) return;
        _paths.SetStats(DiffStats.Parse(patch));
        if (!_paths.HasPick) Diff.ShowUnified(patch, title);
    }

    async void OnPicked(TreeNode node)
    {
        if (_rev == null || _shownItem == null) return;
        var svn = Session.Root?.Svn ?? new Svn("svn", new NullLog());
        var rev = _rev.Revision;
        // The repository of the revision in the diff. In a category list that is not the tree's pick.
        var root = _shownItem.ReposRoot.TrimEnd('/');
        if (node.Row is not SvnPathRow row || (node.IsFolder && row.Path.Kind == "dir"))
        {
            // A folder: everything the revision changed under it, as one patch from the server.
            var url = root + "/" + node.FullPath.TrimStart('/');
            var title = $"{node.FullPath}   {node.FileCount} path(s), r{rev}, unified";
            Diff.BeginLoading(title);
            var patch = await Task.Run(() => svn.DiffRevision(url, rev));
            if (_paths.IsCurrent(node) && _rev?.Revision == rev) Diff.ShowUnified(patch, title);
            return;
        }
        var p = row.Path;
        if (p.Kind == "dir") { Diff.ShowText("folder: " + p.Path, p.Path); return; }
        var fileUrl = root + p.Path;
        await Diff.ShowFileAsync(p.Path, $"{p.Path}   r{rev - 1} → r{rev}", new DiffView.Reads(
                () => p.Action == "A" && p.CopyFrom == null ? "" : svn.CatUrl(fileUrl, rev - 1),
                () => p.Action == "D" ? "" : svn.CatUrl(fileUrl, rev)),
            // The revision can change under the selection too, so both have to still be the ones asked for.
            () => _paths.IsCurrent(node) && _rev?.Revision == rev);
    }

    // ---- commands ----

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        var input = await MonitorDialogs.Edit(this, null);
        if (input == null) return;
        MonitorService.Add(input.Name, input.Url, input.Category, input.Interval, input.Notify);
    }

    async void AddCheckouts_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Root;
        if (root == null) { await Dialogs.Info(this, "No root open", "Open a root in the overview first."); return; }
        // The reading half is two git processes per checkout and used to run here, on the UI thread, with
        // nothing on screen to say so: on a monorepo this size the window simply stopped for a moment.
        var cos = root.Config.Checkouts.ToList();
        var urls = await Busy.During(sender, () => Task.Run(() =>
            cos.SelectMany(co => MonitorService.CheckoutUrls(root, co)).ToList()));
        if (urls == null) return;
        var added = MonitorService.AddUrls(urls);
        await Dialogs.Info(this, "Checkouts added", added == 0 ? "Everything was already watched." : $"{added} URL(s) added. They are checked now.");
    }

    async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { await Dialogs.Info(this, "Nothing selected", "Pick a repository in the tree first."); return; }
        var input = await MonitorDialogs.Edit(this, _selected);
        if (input == null) return;
        _selected.Name = input.Name;
        _selected.Category = input.Category.Length > 0 ? input.Category : "General";
        _selected.IntervalMinutes = input.Interval;
        _selected.Notify = input.Notify;
        _selected.Enabled = input.Enabled;
        if (!_selected.Url.Equals(input.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            _selected.Url = input.Url.TrimEnd('/');
            _selected.Recent.Clear();
            _selected.Head = 0;
            _selected.LastSeen = 0;
            _selected.LastNotified = 0;
        }
        MonitorService.Save();
        _ = MonitorService.CheckOneAsync(_selected);
    }

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { await Dialogs.Info(this, "Nothing selected", "Pick a repository in the tree first."); return; }
        if (!await Dialogs.Confirm(this, "Stop watching", $"Stop watching {_selected.Name}?", "Remove")) return;
        // Everything is put away before the service call, because Remove rebuilds the tree while it runs
        // and the rebuild reads this state. The message and the diff of the repository that just went
        // used to stay on screen, under a header that no longer named anything.
        var gone = _selected;
        _selected = null;
        _category = null;
        _rowItem.Clear();
        _shownItem = null;
        _rev = null;
        Revisions.ItemsSource = null;
        _paths.Clear("Changed paths");
        DetailHead.Text = "";
        DetailMessage.Text = "";
        Diff.ShowText("", "pick a repository");
        ItemHeader.Text = "Pick a repository";
        ItemState.Text = "";
        CheckingRing.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Collapsed;
        NoRevisions.Visibility = Visibility.Collapsed;
        MonitorService.Remove(gone);
    }

    async void CheckNow_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => MonitorService.CheckAllAsync());

    void MarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) MonitorService.MarkRead(_selected);
    }

    void MarkAllRead_Click(object sender, RoutedEventArgs e) => MonitorService.MarkAllRead();
}

public sealed record MonitorInput(string Name, string Url, string Category, int Interval, bool Notify, bool Enabled);

public static class MonitorDialogs
{
    public static async Task<MonitorInput?> Edit(object owner, MonitorItem? existing)
    {
        var name = new TextBox { Header = "Name", Text = existing?.Name ?? "", PlaceholderText = "engine trunk" };
        var url = new TextBox { Header = "SVN URL", Text = existing?.Url ?? "", PlaceholderText = "https://svn.example.com/svn/engine/trunk" };
        var category = new ComboBox { Header = "Category", IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = MonitorService.Categories.ToList() };
        category.Text = existing?.Category ?? (MonitorService.Categories.FirstOrDefault() ?? "General");
        var interval = new NumberBox { Header = "Check every N minutes", Value = existing?.IntervalMinutes ?? 5, Minimum = 1, Maximum = 720, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var notify = new CheckBox { Content = "Toast when new commits arrive", IsChecked = existing?.Notify ?? true };
        var enabled = new CheckBox { Content = "Enabled", IsChecked = existing?.Enabled ?? true };
        var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
        panel.Children.Add(name);
        panel.Children.Add(url);
        panel.Children.Add(category);
        panel.Children.Add(interval);
        panel.Children.Add(notify);
        panel.Children.Add(enabled);
        var d = new ContentDialog
        {
            XamlRoot = owner is Window w ? w.Content.XamlRoot : ((UIElement)owner).XamlRoot,
            Title = existing == null ? "Watch a repository" : "Edit " + existing.Name,
            Content = panel,
            PrimaryButtonText = existing == null ? "Add" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
        var u = url.Text.Trim();
        if (u.Length == 0 || !u.Contains("://")) { await Dialogs.Info(owner, "URL needed", "Give a full SVN URL, like https://svn.example.com/svn/engine/trunk."); return null; }
        var n = name.Text.Trim().Length > 0 ? name.Text.Trim() : u.TrimEnd('/').Split('/').Last();
        var iv = double.IsNaN(interval.Value) ? 5 : (int)interval.Value;
        return new MonitorInput(n, u, category.Text.Trim(), Math.Max(1, iv), notify.IsChecked == true, enabled.IsChecked == true);
    }
}
