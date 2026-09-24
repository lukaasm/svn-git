using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>Root-wide feedback summaries. Source and full discussions load only after opening a thread.</summary>
public sealed class ReviewInboxPage : SgPage
{
    const int PageSize = 40;
    readonly TextBox _search = new() { PlaceholderText = "Search feedback, file, author, or worktree", MinWidth = 220 };
    readonly ComboBox _state = new() { ItemsSource = new[] { "Open comments", "Resolved comments", "All comments" }, SelectedIndex = 0 };
    readonly ComboBox _scope = new() { MinWidth = 180, MaxWidth = 360 };
    readonly StatusChip _summary = new() { Glyph = "\uE90A" };
    readonly TextBlock _count = new() { TextWrapping = TextWrapping.Wrap };
    readonly InfoBar _notice = new() { IsClosable = false, Severity = InfoBarSeverity.Warning };
    readonly ReadFeedback _loading = new() { StateId = "ReviewInboxLoading" };
    readonly EmptyState _empty = new() { Glyph = "\uE90A", Visibility = Visibility.Collapsed };
    readonly IconButton _clear = new() { Text = "Show all comments", Glyph = "\uE71C" };
    readonly IconButton _more = new() { Text = "Show more", Glyph = "\uE70D", HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel _list = new() { Spacing = 8 };
    readonly ScrollViewer _scroll;
    readonly StatusStrip _pane = new() { ShowReadFeedback = false };
    readonly PageReads _reads;
    readonly UiRefresh _render, _refresh;
    readonly Dictionary<string, ReviewInboxWorktree> _worktrees = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, InboxCard> _cards = [];
    ReviewInbox? _inbox;
    SgRoot? _root;
    Window? _window;
    bool _hidden = true, _busy, _pending, _restoring;
    int _shown = PageSize;
    string? _worktree;
    double? _returnOffset;
    sealed record Scope(string? Path, string Name) { public override string ToString() => Name; }
    sealed record ViewState(string Query, int State, string? Worktree, int Shown, double Offset);

    public ReviewInboxPage()
    {
        Title = "Review inbox"; Subtitle = "Feedback across this root's local worktrees · newest activity first";
        _scroll = new() { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _reads = new(active =>
        {
            _busy = active;
            if (active) _loading.Show("Reading worktree feedback…", _worktrees.Count == 0);
            else
            {
                _loading.Hide(); _render?.Request();
                if (_pending && !_hidden) { _pending = false; _refresh?.Request(); }
            }
        });
        _render = new(DispatcherQueue, () => { if (!_hidden) Render(); }, TimeSpan.FromMilliseconds(150));
        _refresh = new(DispatcherQueue, () => { if (!_hidden) { if (_busy) _pending = true; else _ = Reload(); } }, TimeSpan.FromMilliseconds(300));
        var refresh = new IconButton { Text = "Refresh", Glyph = "\uE72C" };
        refresh.Click += (_, _) => _ = Reload();
        var filters = new WrapRow { Spacing = 8 };
        filters.Children.Add(_search); filters.Children.Add(_state); filters.Children.Add(_scope); filters.Children.Add(refresh);
        var heading = new StackPanel { Spacing = 8 };
        heading.Children.Add(_summary); heading.Children.Add(filters); heading.Children.Add(_count); heading.Children.Add(_notice); heading.Children.Add(_loading);
        var grid = new Grid { Padding = new Thickness(16), RowSpacing = 8 };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(heading); Grid.SetRow(_scroll, 1); grid.Children.Add(_scroll);
        Grid.SetRow(_pane, 2); grid.Children.Add(_pane); Content = grid;
        Id(_search, "ReviewInboxSearch"); Id(_state, "ReviewInboxFilter"); Id(_scope, "ReviewInboxWorktree");
        Id(refresh, "ReviewInboxRefresh"); Id(_summary, "ReviewInboxSummary"); Id(_count, "ReviewInboxCount");
        Id(_scroll, "ReviewInboxResults"); Id(_more, "ReviewInboxMore"); Id(_notice, "ReviewInboxNotice");
        AutomationProperties.SetName(_scope, "Filter by worktree");
        AutomationProperties.SetName(_state, "Filter review status");
        AutomationProperties.SetName(_search, "Search review inbox");
        AutomationProperties.SetLiveSetting(_count, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _search.TextChanged += (_, _) => FilterChanged();
        _state.SelectionChanged += (_, _) => FilterChanged();
        _scope.SelectionChanged += (_, _) => { if (!_restoring) { _worktree = (_scope.SelectedItem as Scope)?.Path; FilterChanged(); } };
        _scope.DropDownClosed += (_, _) => _render.Request();
        _more.Click += (_, _) => { _shown += PageSize; Render(); };
        Id(_clear, "ReviewInboxClearFilters");
        _clear.Click += (_, _) => { _restoring = true; _search.Text = ""; _state.SelectedIndex = 2; _worktree = null; _scope.SelectedIndex = 0; _restoring = false; FilterChanged(); };
        Shortcuts.Add(this, Windows.System.VirtualKey.F, Windows.System.VirtualKeyModifiers.Control, () => _search.Focus(FocusState.Programmatic));
        Shortcuts.Add(this, Windows.System.VirtualKey.F5, () => _ = Reload());
        Unloaded += (_, _) => OnHidden();
    }
    static void Id(DependencyObject control, string id) => AutomationProperties.SetAutomationId(control, id);
    void FilterChanged()
    {
        if (_restoring) return;
        _shown = PageSize; _returnOffset = 0; _render.Request();
    }
    public override void OnShown(bool returning)
    {
        _hidden = false; _window = Window;
        if (_window != null) _window.Activated += Activated;
        _ = Reload();
    }
    public override void OnHidden()
    {
        _hidden = true; _reads.Cancel(); _inbox?.Dispose(); _inbox = null;
        if (_window != null) _window.Activated -= Activated;
        _window = null;
    }
    void Activated(object sender, WindowActivatedEventArgs args)
    { if (args.WindowActivationState != WindowActivationState.Deactivated) _refresh.Request(); }
    internal override object? CaptureViewState() => new ViewState(_search.Text, _state.SelectedIndex, _worktree, _shown, _scroll.VerticalOffset);
    internal override void RestoreViewState(object? state)
    {
        if (state is not ViewState view) return;
        _restoring = true; _search.Text = view.Query; _state.SelectedIndex = view.State; _worktree = view.Worktree;
        _shown = view.Shown; _returnOffset = view.Offset; _restoring = false;
    }
    async Task Reload()
    {
        if (_hidden) return;
        using var read = _reads.Begin();
        var root = Session.Require();
        if (_root != root) { _inbox?.Dispose(); _inbox = null; _root = root; _worktrees.Clear(); }
        try
        {
            if (_inbox == null)
            {
                _inbox = new ReviewInbox(root);
                _inbox.Changed += () => _refresh.Request();
            }
        }
        catch (Exception e) { Error(e.Message); return; }
        var complete = false; var inbox = _inbox;
        var data = await read.Run(_pane, () => inbox.Read(entry => DispatcherQueue.TryEnqueue(() =>
            {
                if (complete || !read.Current || _hidden) return;
                Accept(entry); _loading.Show("Reading feedback · " + entry.Branch, false); _render.Request();
            })), Error);
        if (!read.Current || _hidden) return;
        complete = true;
        if (data == null) { Render(); return; }
        foreach (var entry in data) Accept(entry);
        var paths = data.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _worktrees.Keys.Where(p => !paths.Contains(p)).ToArray()) _worktrees.Remove(path);
        if (_worktree != null && !paths.Contains(_worktree)) _worktree = null;
        var errors = _worktrees.Values.Where(w => w.Error != null).ToArray();
        _notice.IsOpen = errors.Length > 0 || _inbox.WatchError != null;
        _notice.Title = errors.Length > 0 ? "Some feedback could not be refreshed" : "Live updates unavailable";
        _notice.Message = errors.Length > 0 ? string.Join("\n", errors.Select(e => e.Branch + ": " + e.Error)) : _inbox.WatchError ?? "";
        Render();
    }
    void Error(string message) { _notice.Title = "Could not read feedback"; _notice.Message = message + " Refresh to retry."; _notice.IsOpen = true; }
    void Accept(ReviewInboxWorktree entry)
    {
        if (entry.Error != null && entry.Identity != null && _worktrees.TryGetValue(entry.Path, out var previous) && previous.Identity == entry.Identity)
            entry = entry with { Items = previous.Items, Revision = previous.Revision };
        _worktrees[entry.Path] = entry;
    }
    void Render()
    {
        if (_hidden) return;
        var rows = _cards.ToDictionary(p => p.Key, p => (FrameworkElement)p.Value);
        var anchor = BrowseScroll.Capture(_scroll, rows);
        var all = _worktrees.Values.SelectMany(w => w.Items.Select(item => (Worktree: w, Item: item))).ToArray();
        var open = all.Count(r => r.Item.State == "open");
        _summary.Text = $"{open} open · {all.Length - open} resolved · {_worktrees.Count} worktrees";
        _summary.Severity = open > 0 ? ChipSeverity.Attention : ChipSeverity.Neutral;
        if (!_scope.IsDropDownOpen)
        {
            var scopes = new[] { new Scope(null, "All worktrees") }.Concat(_worktrees.Values.OrderBy(w => w.Branch, StringComparer.OrdinalIgnoreCase).Select(w => new Scope(w.Path, $"{w.Branch} · {w.Items.Count(t => t.State == "open")} open"))).ToArray();
            if (!_scope.Items.Cast<Scope>().SequenceEqual(scopes))
            {
                _restoring = true; _scope.ItemsSource = scopes;
                _scope.SelectedItem = scopes.FirstOrDefault(s => s.Path == _worktree) ?? scopes[0]; _restoring = false;
            }
        }
        var state = _state.SelectedIndex switch { 1 => "resolved", 2 => "all", _ => "open" };
        var matches = all.Where(r => (_worktree == null || r.Worktree.Path == _worktree) && ReviewInbox.Matches(r.Item, r.Worktree, _search.Text, state))
            .OrderByDescending(r => r.Item.Updated).ThenBy(r => r.Worktree.Branch, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Item.Id, StringComparer.Ordinal).ToArray();
        _count.Text = $"Showing {Math.Min(_shown, matches.Length)} of {matches.Length} comments";
        var children = new List<UIElement>(); var visible = new HashSet<string>();
        foreach (var (worktree, item) in matches.Take(_shown))
        {
            var key = worktree.Path + "\0" + item.Id; visible.Add(key);
            if (!_cards.TryGetValue(key, out var card)) _cards[key] = card = new InboxCard(OpenThread);
            card.Update(worktree, item); children.Add(card);
        }
        foreach (var key in _cards.Keys.Where(k => !visible.Contains(k)).ToArray()) _cards.Remove(key);
        if (matches.Length == 0)
        {
            _empty.Title = _busy && all.Length == 0 ? "Reading feedback…" : _notice.IsOpen && all.Length == 0 ? "Feedback is unavailable" : all.Length == 0 ? "No review comments yet" : "No matching comments";
            _empty.Text = all.Length == 0 ? "Open Review code on a worktree to leave feedback. Comments from all local worktrees appear here." : "Change the status or worktree filter, or clear the search.";
            _empty.Action = all.Length == 0 ? null : _clear;
            _empty.Visibility = Visibility.Visible; children.Add(_empty);
        }
        if (_shown < matches.Length) { _more.Text = $"Show {Math.Min(PageSize, matches.Length - _shown)} more"; children.Add(_more); }
        foreach (var child in _list.Children.Where(c => !children.Contains(c)).ToArray()) _list.Children.Remove(child);
        for (var i = 0; i < children.Count; i++)
        {
            if (i < _list.Children.Count && ReferenceEquals(_list.Children[i], children[i])) continue;
            _list.Children.Remove(children[i]); _list.Children.Insert(i, children[i]);
        }
        if (_returnOffset is { } offset && !_busy) { _returnOffset = null; BrowseScroll.Restore(_scroll, offset); }
        else if (_returnOffset == null) BrowseScroll.Restore(_scroll, anchor, _cards.ToDictionary(p => p.Key, p => (FrameworkElement)p.Value), () => !_hidden);
    }
    void OpenThread(ReviewInboxWorktree worktree, ReviewInboxItem item) => DispatcherQueue.TryEnqueue(() =>
        Go(() => new CodeReviewPage(worktree.Path, item.Id) { Checkout = worktree.Checkout, Branch = worktree.Branch }, "code-review:" + worktree.Path + ":" + item.Id));
}
