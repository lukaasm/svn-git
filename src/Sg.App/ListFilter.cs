using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// A filter box over a file tree. One push in a monorepo lists every changed path across four
/// working copies, so the list needs narrowing more often than it needs scrolling. Rows arrive flat
/// and come out as folders and files; with groupOf each working copy is a root, which is the cut a
/// push commits in and what these lists used to draw as a group header.
///
/// The tree is drawn on a plain list, one line per visible node, indented by its depth. WinUI's
/// TreeView drew it once, but it indents 16 pixels a level from code, keeps minimum heights of its
/// own, and did not always answer a selection set from code; a list does exactly what it is told.
/// </summary>
public sealed class ListFilter
{
    readonly TextBox _box;
    readonly ListView _list;
    readonly TextBlock _header;
    readonly Func<object, string> _textOf;
    readonly Func<object, string>? _groupOf;

    IReadOnlyList<StatusRow> _all = Array.Empty<StatusRow>();
    List<TreeNode> _roots = new();
    ObservableCollection<TreeNode> _lines = new();
    IReadOnlyDictionary<string, DiffStats.Count>? _stats;

    /// <summary>
    /// The page reads a patch after it sets the rows and hands the numbers over with SetStats. Until
    /// they land every line shows a placeholder where they go. A page that never will leaves this off.
    /// </summary>
    public bool ExpectStats { get; set; }
    string _label = "";
    bool _accelerated;

    TreeNode? _picked;

    /// <summary>
    /// A line was picked: a file or a folder, selected with the mouse or the keyboard. Fired once per
    /// line, so a click on the line already open does not read its diff again.
    /// </summary>
    public event Action<TreeNode>? Picked;

    public ListFilter(TextBox box, ListView list, TextBlock header, Func<object, string> textOf, Func<object, string>? groupOf = null)
    {
        _box = box;
        _list = list;
        _header = header;
        _textOf = textOf;
        _groupOf = groupOf;
        _list.ItemsSource = _lines;
        // Every keystroke used to rebuild the whole tree on the UI thread, and on a push that lists a
        // few thousand paths that was a stutter per letter. One rebuild per pause in the typing instead.
        _debounce.Tick += (_, _) => { _debounce.Stop(); Apply(); };
        _box.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _list.Loaded += (_, _) => AddAccelerator();
        _list.SelectionChanged += (_, _) => Pick(_list.SelectedItem as TreeNode);
    }

    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(140) };

    void Pick(TreeNode? node)
    {
        if (node == null || ReferenceEquals(node, _picked)) return;
        _picked = node;
        Picked?.Invoke(node);
    }

    /// <summary>The line the diff on screen belongs to. A read that lands after the user moved on checks this.</summary>
    public bool IsCurrent(TreeNode node) => ReferenceEquals(_picked, node);

    /// <summary>The rows the window built. label is the header word, for example "Changes".</summary>
    public void SetItems<T>(IEnumerable<T> items, string label) where T : StatusRow
    {
        _all = items.Cast<StatusRow>().ToList();
        _label = label;
        _stats = null;   // new rows, a new patch: the old numbers would name the wrong files
        _debounce.Stop();   // new rows now; a keystroke still waiting would draw the old ones over them
        Apply();
    }

    public void Clear(string label) => SetItems(Array.Empty<StatusRow>(), label);

    /// <summary>
    /// Lines added and removed per path, from the patch the page loaded for its "all files" view. A
    /// file shows its own numbers, a folder the sum of its files. Kept, so the tree the filter box
    /// builds on every keystroke gets them again.
    /// </summary>
    public void SetStats(IReadOnlyDictionary<string, DiffStats.Count> stats)
    {
        _stats = stats;
        foreach (var r in _roots) Fill(r);
    }

    (int Added, int Removed, bool Has) Fill(TreeNode node)
    {
        if (_stats == null) return (0, 0, false);
        if (node.Children.Count == 0)
        {
            var c = node.Row == null ? null : DiffStats.For(_stats, node.Row.TreePath);
            // A file the patch does not name, an untracked one say, has no numbers; the placeholder still goes.
            node.SetStats(c?.Added ?? 0, c?.Removed ?? 0);
            return c == null ? (0, 0, false) : (c.Value.Added, c.Value.Removed, true);
        }
        int added = 0, removed = 0;
        var any = false;
        foreach (var child in node.Children)
        {
            var (a, r, has) = Fill(child);
            added += a;
            removed += r;
            any |= has;
        }
        node.SetStats(added, removed);
        return (added, removed, any);
    }

    static void Expect(TreeNode node)
    {
        node.ExpectStats();
        foreach (var c in node.Children) Expect(c);
    }

    /// <summary>True once a line has been picked since the rows were set. The "all files" diff yields to it.</summary>
    public bool HasPick => _picked != null;

    /// <summary>How many rows the window gave the filter, shown or not.</summary>
    public int Count => _all.Count;

    /// <summary>Every row the window handed over, whatever the filter is showing.</summary>
    public IEnumerable<T> Rows<T>() where T : StatusRow => _all.OfType<T>();

    /// <summary>The row on the selected line. A folder has none, and answers null.</summary>
    public T? Selected<T>() where T : StatusRow => (_list.SelectedItem as TreeNode)?.Row as T;

    /// <summary>Puts the selection back on one row after a reload, so an open diff survives it.</summary>
    public bool Select(Func<StatusRow, bool> match)
    {
        var node = Walk(_roots).FirstOrDefault(n => n.Row != null && match(n.Row));
        if (node == null) return false;
        // A line inside a closed folder is not on the list yet; open the folders above it first.
        for (var p = node.Parent; p != null; p = p.Parent)
            if (!p.IsExpanded) p.IsExpanded = true;
        _list.SelectedItem = node;
        _list.ScrollIntoView(node);
        Pick(node);
        return true;
    }

    /// <summary>The first file in the tree, top to bottom. --select walks this far.</summary>
    public bool SelectFirstFile() => Select(_ => true);

    static IEnumerable<TreeNode> Walk(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Walk(n.Children)) yield return c;
        }
    }

    /// <summary>The lines under a node that are on screen: its children, and under each open one, theirs.</summary>
    static IEnumerable<TreeNode> Visible(TreeNode node)
    {
        foreach (var c in node.Children)
        {
            yield return c;
            if (c.IsExpanded)
                foreach (var d in Visible(c)) yield return d;
        }
    }

    /// <summary>A folder opened or closed: its lines go in or come out under it, and nothing else moves.</summary>
    void Toggled(TreeNode node)
    {
        var index = _lines.IndexOf(node);
        if (index < 0) return;
        if (node.IsExpanded)
        {
            var i = index + 1;
            foreach (var d in Visible(node)) _lines.Insert(i++, d);
            return;
        }
        var lost = false;
        while (index + 1 < _lines.Count && _lines[index + 1].Depth > node.Depth)
        {
            lost |= ReferenceEquals(_lines[index + 1], _picked);
            _lines.RemoveAt(index + 1);
        }
        // The line the diff belonged to went in with the fold; the folder stands for it now.
        if (lost) _list.SelectedItem = node;
    }

    void Apply()
    {
        var query = _box.Text.Trim();
        var shown = query.Length == 0
            ? _all
            : _all.Where(r => _textOf(r).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        // The old tree holds a handler on every checkable row, and the rows outlive it.
        foreach (var n in _roots) n.Detach();
        _picked = null;
        // A tree of scattered matches reads worse than the list it came from, so a filter flattens it.
        _roots = query.Length == 0 ? FileTree.Build(shown, _groupOf) : FileTree.Flat(shown, _groupOf);
        foreach (var n in Walk(_roots)) n.Toggled = Toggled;
        if (_stats != null) foreach (var r in _roots) Fill(r);
        else if (ExpectStats) foreach (var r in _roots) Expect(r);
        // One new collection, one reset: adding four hundred lines one event at a time is what a big
        // push used to spend its first second on.
        _lines = new ObservableCollection<TreeNode>(_roots.SelectMany(r => new[] { r }.Concat(r.IsExpanded ? Visible(r) : Enumerable.Empty<TreeNode>())));
        _list.ItemsSource = _lines;

        _box.Visibility = _all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _header.Text = _all.Count == 0 ? _label
            : shown.Count == _all.Count ? $"{_label} ({_all.Count})"
            : $"{_label}, showing {shown.Count} of {_all.Count}";
    }

    /// <summary>Ctrl+F from anywhere in the window jumps to the box. It lives on the root, so the tree can hold focus.</summary>
    void AddAccelerator()
    {
        // Loaded can fire more than once. Guard on this filter, not on the window: a window with
        // two filtered lists would otherwise give Ctrl+F to whichever loaded first.
        if (_accelerated) return;
        // On the page when there is one, so the key goes away with the page. On the window root it
        // stacked up, and Ctrl+F reached the box of a page that was long gone.
        var root = PageOf(_list) ?? _list.XamlRoot?.Content as FrameworkElement;
        if (root == null) return;
        _accelerated = true;
        var accelerator = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) =>
        {
            _box.Focus(FocusState.Programmatic);
            _box.SelectAll();
            args.Handled = true;
        };
        root.KeyboardAccelerators.Add(accelerator);
        // On the window root so any focus reaches it, which is also what made WinUI float the key name
        // over the whole window. The filter box already says Ctrl+F in its placeholder.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    static FrameworkElement? PageOf(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Page page) return page;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
