using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Sg.App;

/// <summary>
/// One line of a file tree: a folder, a file, or an SVN path that is both. Nothing outside this file
/// builds one. Windows hand their rows to <see cref="FileTree"/> and bind to what comes back.
/// </summary>
public sealed class TreeNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    CheckableRow? _source;
    PropertyChangedEventHandler? _hook;
    bool? _checked = false;
    bool _expanded = true;

    internal TreeNode(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }

    /// <summary>What the line shows: one folder name, a run of folders the tree joined, or a file name.</summary>
    public string Name { get; internal set; }

    /// <summary>The path from the top of the tree. The context menu opens this.</summary>
    public string FullPath { get; internal set; }

    /// <summary>
    /// The row behind this line. A folder the tree invented has none. An SVN folder that itself
    /// changed has one, and wears its status letter like any file.
    /// </summary>
    public StatusRow? Row { get; internal set; }

    public TreeNode? Parent { get; internal set; }

    /// <summary>The lines under this one. Empty means a file, and no chevron.</summary>
    public List<TreeNode> Children { get; } = new();

    /// <summary>A working copy at the top of the tree. It never merges into the folder below it.</summary>
    public bool IsGroup { get; internal set; }

    /// <summary>Where a working copy sorts: the root first, then the externals by name.</summary>
    internal int Order { get; set; }

    /// <summary>Identifies the node while the tree is being built. Meaningless afterwards.</summary>
    internal string Key { get; set; } = "";

    /// <summary>How many files sit under this line, itself included when it is a file.</summary>
    public int FileCount { get; internal set; }

    /// <summary>How many lines are above this one in the tree. The line indents by it.</summary>
    public int Depth { get; internal set; }

    /// <summary>Eight pixels a level: half of what TreeView gave, which walked a deep file off the right edge.</summary>
    public Thickness Indent => new(Depth * 8, 0, 0, 0);

    /// <summary>The filter, which puts the lines under this one in or takes them out when it opens or closes.</summary>
    internal Action<TreeNode>? Toggled;

    public void Toggle() => IsExpanded = !IsExpanded;

    public string ChevronGlyph => _expanded ? "\uE70D" : "\uE76C";
    public Visibility ChevronVisibility => IsFolder ? Visibility.Visible : Visibility.Collapsed;

    public bool IsFolder => Children.Count > 0;

    /// <summary>The name, plus whatever the row writes after the path, such as "(was ...)".</summary>
    public string Text
    {
        get
        {
            if (Row == null) return Name;
            var text = Row.PathText;
            var path = Row.TreePath;
            return text.Length > path.Length && text.StartsWith(path, StringComparison.Ordinal)
                ? Name + text[path.Length..]
                : Name;
        }
    }

    /// <summary>
    /// What a screen reader announces for the line. Without it a file tree reads out the class name,
    /// once per row.
    /// </summary>
    public override string ToString()
    {
        var code = Row?.Code.Trim() ?? "";
        var what = IsFolder ? $"{FileCount} file(s)" : "";
        return string.Join("  ", new[] { code, Text, what }.Where(s => s.Length > 0));
    }

    public string Code => Row?.Code ?? "";
    public Brush CodeBrush => StatusColors.BrushFor(Code);
    /// <summary>An invented folder has no status, so its badge is the transparent one and the column still lines up.</summary>
    public Brush CodeBackground => StatusColors.BackgroundFor(Code);
    public string CodeTip => Row == null ? "" : StatusColors.Describe(Row.Code);

    /// <summary>A folder the tree invented has no status, so it wears no badge: the name follows the chevron.</summary>
    public Visibility BadgeVisibility => Row == null ? Visibility.Collapsed : Visibility.Visible;

    bool _statsPending;

    /// <summary>The grey bar where the numbers will be, while the patch they come from is still being read.</summary>
    public Visibility StatsPendingVisibility => _statsPending ? Visibility.Visible : Visibility.Collapsed;

    internal void ExpectStats()
    {
        if (_statsPending) return;
        _statsPending = true;
        Raise(nameof(StatsPendingVisibility));
    }

    public string Glyph => IsFolder ? "" : "";
    int _added, _removed;
    bool _hasStats;

    /// <summary>"+12" in green, once the patch has been read. Empty for a file with no lines added, and before the patch.</summary>
    public string AddedText => _hasStats && _added > 0 ? "+" + _added : "";
    /// <summary>"-3" in red, once the patch has been read.</summary>
    public string RemovedText => _hasStats && _removed > 0 ? "-" + _removed : "";

    /// <summary>The numbers for this line. A folder gets the sum of its files from the filter.</summary>
    internal void SetStats(int added, int removed)
    {
        _added = added;
        _removed = removed;
        _hasStats = true;
        if (_statsPending)
        {
            _statsPending = false;
            Raise(nameof(StatsPendingVisibility));
        }
        Raise(nameof(AddedText));
        Raise(nameof(RemovedText));
    }

    internal (int Added, int Removed, bool Has) Stats => (_added, _removed, _hasStats);

    public Visibility GlyphVisibility => IsFolder ? Visibility.Visible : Visibility.Collapsed;
    public string CountText => IsFolder ? FileCount.ToString() : "";

    /// <summary>The whole path, which the line itself only shows the tail of.</summary>
    public string Tip => FullPath.Length == 0 ? Name : FullPath;

    /// <summary>Everything opens expanded: the compressor makes the tree shallow enough to read whole.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Raise(nameof(IsExpanded));
            Raise(nameof(ChevronGlyph));
            Toggled?.Invoke(this);
        }
    }

    /// <summary>
    /// Ticked, not ticked, or part of each. A folder writes its answer down to every file under it;
    /// a file writes it to the row, which is what the window then commits or discards.
    /// </summary>
    public bool? Checked
    {
        get => _checked;
        set
        {
            // Only a click writes here, and a click says plainly which way it went. Half is what a
            // folder works out from its files, never something set from outside.
            Push(value == true);
            Parent?.Pull();
        }
    }

    void Push(bool on)
    {
        foreach (var c in Children) c.Push(on);
        if (_source != null) _source.Checked = on;
        Set(on);
    }

    /// <summary>A child changed. Work out what this folder is now, and tell its own parent.</summary>
    internal void Pull()
    {
        if (Children.Count == 0) return;
        bool? now = Children.All(c => c.Checked == true) ? true
            : Children.All(c => c.Checked == false) ? false
            : null;
        if (now == _checked) return;
        Set(now);
        Parent?.Pull();
    }

    void Set(bool? value)
    {
        if (_checked == value) return;
        _checked = value;
        Raise(nameof(Checked));
    }

    /// <summary>
    /// Follows the row, so All and None on the window's toolbar move the tick boxes too. The row is
    /// the truth; the node only shows it.
    /// </summary>
    internal void Attach(CheckableRow? row)
    {
        if (row == null) return;
        _source = row;
        _checked = row.Checked;
        _hook = (_, a) =>
        {
            if (a.PropertyName != nameof(CheckableRow.Checked)) return;
            Set(row.Checked);
            Parent?.Pull();
        };
        row.PropertyChanged += _hook;
    }

    /// <summary>Lets go of the rows. The filter builds a new tree on every keystroke; the rows outlive it.</summary>
    public void Detach()
    {
        foreach (var c in Children) c.Detach();
        if (_source != null && _hook != null) _source.PropertyChanged -= _hook;
        _source = null;
        _hook = null;
        Toggled = null;
    }

    /// <summary>Every row on and under this line. A folder action works on all of them.</summary>
    public IEnumerable<StatusRow> Rows()
    {
        if (Row != null) yield return Row;
        foreach (var c in Children)
            foreach (var r in c.Rows())
                yield return r;
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Turns a flat list of rows into folders and files. Every file list in the app draws one of these:
/// 71 changed paths across four working copies is a tree to walk, not a list to scroll.
/// </summary>
public static class FileTree
{
    /// <summary>
    /// The rows as a tree. groupOf, where it is given, puts one working copy at each root: the same
    /// cut a push or an SVN commit goes out in, which these lists used to draw as a group header.
    /// </summary>
    public static List<TreeNode> Build(IReadOnlyList<StatusRow> rows, Func<object, string>? groupOf)
    {
        var roots = new List<TreeNode>();
        var index = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var group = groupOf?.Invoke(row) ?? "";
            var node = groupOf == null ? null : Group(roots, index, group);
            var segments = Split(row.TreePath);
            // A grouped row's path already starts with its working copy. The root says it once.
            for (var i = groupOf == null ? 0 : Shared(Split(group), segments); i < segments.Length; i++)
                node = Child(roots, index, node, segments[i]);
            // A path that is the working copy itself, or nothing at all, still gets a line of its own.
            node ??= Child(roots, index, null, row.TreePath);
            node.Row = row;
            node.Attach(row as CheckableRow);
        }

        foreach (var r in roots)
        {
            if (r.IsGroup) foreach (var c in r.Children) Compress(c);
            else Compress(r);
        }
        Sort(roots, groupOf != null);
        foreach (var r in roots) { Count(r); Settle(r); Depths(r, 0); }
        return roots;
    }

    /// <summary>
    /// The same rows as one line each, no folders. A tree of scattered matches reads worse than the
    /// list it came from, so a filtered list stays flat.
    /// </summary>
    public static List<TreeNode> Flat(IReadOnlyList<StatusRow> rows, Func<object, string>? groupOf)
    {
        var roots = new List<TreeNode>();
        var index = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var group = groupOf?.Invoke(row) ?? "";
            var parent = groupOf == null ? null : Group(roots, index, group);
            var segments = Split(row.TreePath);
            var start = groupOf == null ? 0 : Shared(Split(group), segments);
            var name = string.Join('/', segments.Skip(start));
            var node = new TreeNode(name.Length == 0 ? row.TreePath : name, row.TreePath) { Parent = parent, Row = row };
            node.Attach(row as CheckableRow);
            if (parent == null) roots.Add(node); else parent.Children.Add(node);
        }

        foreach (var r in roots) { Count(r); Settle(r); Depths(r, 0); }
        return roots;
    }

    static TreeNode Group(List<TreeNode> roots, Dictionary<string, TreeNode> index, string group)
    {
        // Child keys all start with a slash, so this prefix cannot collide with one.
        var key = "wc:" + group;
        if (index.TryGetValue(key, out var found)) return found;
        var node = new TreeNode(group.Length == 0 ? "root" : group, group)
        {
            IsGroup = true,
            Order = group.Length == 0 ? 0 : 1,
            Key = key,
        };
        index[key] = node;
        roots.Add(node);
        return node;
    }

    static TreeNode Child(List<TreeNode> roots, Dictionary<string, TreeNode> index, TreeNode? parent, string name)
    {
        var key = (parent?.Key ?? "") + "/" + name;
        if (index.TryGetValue(key, out var found)) return found;
        var full = parent == null || parent.FullPath.Length == 0 ? name : parent.FullPath + "/" + name;
        var node = new TreeNode(name, full) { Parent = parent, Key = key };
        index[key] = node;
        if (parent == null) roots.Add(node); else parent.Children.Add(node);
        return node;
    }

    /// <summary>
    /// A folder holding nothing but one more folder becomes one line. Paths here are deep, and
    /// libs/engine/src/render/backend spread over five lines is five lines that say nothing.
    /// </summary>
    static void Compress(TreeNode node)
    {
        while (node.Row == null && node.Children.Count == 1 && node.Children[0].IsFolder)
        {
            var only = node.Children[0];
            node.Name += "/" + only.Name;
            node.FullPath = only.FullPath;
            node.Row = only.Row;
            // The row moves with the name, and so must the checkbox it answers for: adopting the row
            // without attaching it left a line whose tick wrote nothing.
            node.Attach(node.Row as CheckableRow);
            node.Children.Clear();
            node.Children.AddRange(only.Children);
            foreach (var c in node.Children) c.Parent = node;
        }
        foreach (var c in node.Children) Compress(c);
    }

    /// <summary>Folders first, then files, each by name. Working copies keep the order a push uses.</summary>
    static void Sort(List<TreeNode> nodes, bool grouped)
    {
        if (grouped)
            nodes.Sort((a, b) => a.Order != b.Order ? a.Order - b.Order : Compare(a.Name, b.Name));
        else
            nodes.Sort(ByKind);
        foreach (var n in nodes) Sort(n.Children, false);
    }

    static int ByKind(TreeNode a, TreeNode b) =>
        a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : Compare(a.Name, b.Name);

    static int Compare(string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

    static int Count(TreeNode node)
    {
        node.FileCount = node.Children.Count == 0 ? 1 : node.Children.Sum(Count);
        return node.FileCount;
    }

    static void Depths(TreeNode node, int depth)
    {
        node.Depth = depth;
        foreach (var c in node.Children) Depths(c, depth + 1);
    }

    /// <summary>Gives every folder the tick state its files add up to, before anything is on screen.</summary>
    static void Settle(TreeNode node)
    {
        foreach (var c in node.Children) Settle(c);
        node.Pull();
    }

    static string[] Split(string path) => path.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>How many leading segments two paths agree on.</summary>
    static int Shared(string[] a, string[] b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && string.Equals(a[n], b[n], StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }
}
