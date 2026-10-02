using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Sg.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Sg.App;

/// <summary>
/// Two lists that never mix. Needs attention holds what is open, each with the one action that moves it
/// on and Dismiss, read the way the banner reads it, so the two never disagree. History holds what is
/// finished, one line per operation until it is opened. An operation whose worktree is gone offers no
/// resume, because nothing there can run.
/// </summary>
public sealed class ActivityPage : WorkflowPage
{
    const int PageSize = 20;
    readonly TextBox _search = new() { PlaceholderText = "Search history by branch, checkout, status, or step", MinWidth = 240 };
    readonly TextBlock _count = new() { Style = (Style)Application.Current.Resources["Secondary"] };
    readonly IconButton _more = new() { Text = "Show 20 more", Glyph = "\uE70E", HorizontalAlignment = HorizontalAlignment.Left };
    readonly IconButton _refresh = new() { Text = "Refresh", Glyph = "\uE72C" };
    readonly Grid _toolbar = new() { ColumnSpacing = 8 };
    readonly UiRefresh _searchRefresh;
    List<OperationRecord> _history = [];
    List<OperationRecord> _matches = [];
    HashSet<string> _worktrees = [];
    SgRoot? _root;
    int _itemsStart, _shown;
    sealed record ViewState(string Query, int Shown, double Offset, string[] Expanded);
    ViewState? _returning;
    bool _restoring;
    readonly Dictionary<string, Expander> _details = [];
    public ActivityPage() : base("Activity and recovery")
    {
        AutomationProperties.SetAutomationId(_search, "ActivitySearch");
        AutomationProperties.SetName(_search, "Search history");
        AutomationProperties.SetAutomationId(_count, "ActivityResults");
        AutomationProperties.SetLiveSetting(_count, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_more, "ActivityMore");
        AutomationProperties.SetAutomationId(_refresh, "ActivityRefresh");
        ToolTipService.SetToolTip(_refresh, "Read the operations again. F5 does the same.");
        _refresh.Click += (_, _) => _ = Reload();
        _searchRefresh = new(DispatcherQueue, () => { if (IsLoaded && _root != null) Render(reset: true); }, TimeSpan.FromMilliseconds(150));
        _search.TextChanged += (_, _) => { if (!_restoring) _searchRefresh.Request(); };
        _more.Click += (_, _) => Render(reset: false);
        _toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _toolbar.Children.Add(_search); Grid.SetColumn(_refresh, 1); _toolbar.Children.Add(_refresh);
    }
    public override void OnHidden() { base.OnHidden(); _root = null; }
    internal override object? CaptureViewState() => _root == null ? _returning : new ViewState(_search.Text, _shown,
        Scroll.VerticalOffset, _details.Where(p => p.Value.IsExpanded).Select(p => p.Key).ToArray());
    internal override void RestoreViewState(object? state)
    {
        if (state is not ViewState view) return;
        _returning = view;
        _restoring = true;
        _search.Text = view.Query;
        _restoring = false;
    }
    protected override async Task Reload()
    {
        _returning ??= CaptureViewState() as ViewState;
        using var generation = BeginRead(); var root = Session.Require();
        _root = null;
        Body.Children.Clear();
        var loading = ReadNotice("Loading recorded operations and recovery checkpoints…", "ActivityLoading");
        var data = await generation.Run(Pane, () =>
        {
            var records = Operations.List(root);
            var worktrees = root.Git.WorktreeList().Where(w => !w.Bare && Directory.Exists(w.Path)).Select(w => Key(w.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new { Records = records, Attention = Recovery.Read(root, records), Worktrees = worktrees };
        });
        if (!Current(generation)) return;
        if (data == null) { ReadFailed(loading, "Could not read the activity timeline. Retry or open the error log for details."); return; }
        _worktrees = data.Worktrees;
        Body.Children.Clear();
        var records = data.Records.ToDictionary(r => r.Id);
        if (data.Attention.Count > 0) Attention(root, data.Attention, records);
        else Status("Nothing needs attention", ChipSeverity.Success, "\uE73E");
        _history = data.Records.Where(r => r.Terminal).OrderByDescending(r => r.Updated).ToList();
        Text("History", true);
        Body.Children.Add(_toolbar);
        Body.Children.Add(_count);
        _itemsStart = Body.Children.Count;
        _root = root;
        var returning = _returning;
        Render(reset: true, take: returning?.Shown ?? PageSize);
        if (returning != null)
        {
            foreach (var id in returning.Expanded) if (_details.TryGetValue(id, out var detail)) detail.IsExpanded = true;
            BrowseScroll.Restore(Scroll, returning.Offset);
            _returning = null;
        }
    }

    static string Key(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    void Attention(SgRoot root, IReadOnlyList<RecoveryItem> items, Dictionary<string, OperationRecord> records)
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = $"Needs attention · {items.Count}", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        var handoff = new IconButton { Text = "Copy agent instructions", Glyph = "\uE8C8" };
        AutomationProperties.SetAutomationId(handoff, "ActivityHandoff");
        ToolTipService.SetToolTip(handoff, "Copy what an AI agent needs to look into these operations with sg: their state, the commands that read and settle them, and the rules. Nothing is sent anywhere.");
        Grid.SetColumn(handoff, 1); header.Children.Add(handoff);
        Body.Children.Add(header);
        var copied = new InfoBar { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Success,
            Message = "Agent instructions copied. Paste them into an agent that can run sg in this root, such as Claude Code." };
        AutomationProperties.SetAutomationId(copied, "ActivityHandoffCopied");
        Body.Children.Add(copied);
        handoff.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(Recovery.Handoff(root, items, records.Values));
            Clipboard.SetContent(package);
            copied.IsOpen = true;
        };
        foreach (var item in items)
        {
            var owned = (item.Operations ?? []).Select(id => records.GetValueOrDefault(id)).OfType<OperationRecord>().ToList();
            var record = owned.FirstOrDefault();
            var start = Body.Children.Count;
            Text(item.Title, true);
            if (item.Gone) Status("Worktree gone", ChipSeverity.Critical, "\uE7BA");
            else if (item.Replay) Status("Replay paused", ChipSeverity.Caution, "\uE769");
            else Status(record?.PhaseLabel ?? "Needs review", ChipSeverity.Caution, "\uE7BA");
            Text(item.Detail);
            Text(string.Join(" · ", new[] { record?.Checkout, record?.Updated.LocalDateTime.ToString("g"), item.Path }.Where(s => !string.IsNullOrEmpty(s))));
            var actions = Body.Children.Count;
            if (item.Gone)
            {
                if (record is { Before.Length: > 0 }) Action("Restore commits to a new branch", () => RestoreCheckpoint(root, record), true, mutates: true, glyph: "\uE8A7");
            }
            else if (item.Replay) Action("Review replay", () => Navigate(() => new ConflictPage(item.Path), "resolve:" + item.Path), true, glyph: "\uE8A5");
            else Action(item.Action, () => Navigate(() => new UpdateBranchPage(item.Path), "update-branch:" + item.Path), true, glyph: "\uE90F");
            // A paused replay is the worktree's own state, not a record's: it is finished or aborted, never dismissed.
            if (!item.Replay && owned.Count > 0)
            {
                var dismiss = Action("Dismiss", () => Dismiss(root, item), mutates: true, glyph: "\uE894");
                ToolTipService.SetToolTip(dismiss, "Close this record without running anything. Its checkpoint and saved shelves stay.");
                AutomationProperties.SetAutomationId(dismiss, "ActivityDismiss");
            }
            WrapActions(actions);
            Card(start, "Attention_" + (record?.Id ?? Key(item.Path)));
        }
    }

    async Task Dismiss(SgRoot root, RecoveryItem item)
    {
        if (!await Dialogs.ConfirmDismiss(this, [item.Title])) return;
        await Execute("Dismiss operation", () => item.Operations!.Select(id => Operations.FinishReview(root, id)).ToList());
    }

    async Task RestoreCheckpoint(SgRoot root, OperationRecord record)
    {
        var name = record.Branch + "-recovered-" + Guid.NewGuid().ToString("N")[..8];
        if (!await Dialogs.Confirm(this, "Restore checkpoint to a new branch?",
            $"This will:\n• Create branch {name}.\n• Create its worktree at {root.WorktreePathFor(name)}.\n• Restore the recorded commits at {record.Before}.\n\nYour current branch, working files, and shelves stay in place. SVN and remote backups are unchanged.", "Create recovery branch")) return;
        await Execute("Restore checkpoint", () => Operations.RestoreCheckpoint(root, record.Id, name), new(record.Checkout, name, root.WorktreePathFor(name)));
    }

    void Render(bool reset, int take = PageSize)
    {
        if (_root is not { } root) return;
        Body.Children.Remove(_more);
        if (reset)
        {
            var query = _search.Text.Trim();
            _matches = _history.Where(r => query.Length == 0 || string.Join('\n', r.Title, r.Branch, r.Checkout, r.PhaseLabel, r.Detail,
                string.Join('\n', r.Steps)).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            while (Body.Children.Count > _itemsStart) Body.Children.RemoveAt(_itemsStart);
            _details.Clear();
            _shown = 0;
        }
        var end = Math.Min(_shown + Math.Max(PageSize, take), _matches.Count);
        foreach (var record in _matches.Skip(_shown).Take(end - _shown)) Body.Children.Add(Entry(root, record));
        _shown = end;
        _count.Text = _matches.Count == 0 ? (_history.Count == 0 ? "No finished operations yet." : "No matching history. Change the search.")
            : $"Showing {_shown} of {_matches.Count} operations";
        if (_shown < _matches.Count)
        {
            _more.Text = $"Show {Math.Min(PageSize, _matches.Count - _shown)} more";
            Body.Children.Add(_more);
        }
    }

    /// <summary>One finished operation on one line: how it ended, what it was, where and when. Opening it shows the steps and the checkpoint.</summary>
    Expander Entry(SgRoot root, OperationRecord record)
    {
        var done = record.Phase == OperationPhase.Completed;
        var when = record.Updated.LocalDateTime.ToString("g");
        var line = new Grid { ColumnSpacing = 12 };
        line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        line.Children.Add(new StatusChip { Text = done ? "Complete" : "Closed", Severity = done ? ChipSeverity.Success : ChipSeverity.Neutral,
            Glyph = done ? "\uE73E" : "\uE81C", VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = record.Title + " · " + record.Branch, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); line.Children.Add(title);
        var meta = new TextBlock { Text = string.IsNullOrEmpty(record.Checkout) ? when : record.Checkout + " · " + when,
            Style = (Style)Application.Current.Resources["Secondary"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(meta, 2); line.Children.Add(meta);
        var details = new Expander { Header = line, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(details, $"{record.Title} · {record.Branch} · {(done ? "Complete" : "Closed")} · {when}");
        AutomationProperties.SetAutomationId(details, "ActivityEntry_" + record.Id);
        details.Expanding += (_, _) =>
        {
            if (details.Content != null) return;
            var content = new StackPanel { Spacing = 8 };
            if (!string.IsNullOrWhiteSpace(record.Detail)) content.Children.Add(new TextBlock { Text = record.Detail, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            foreach (var step in record.Steps) content.Children.Add(new TextBlock { Text = "✓ " + step, TextWrapping = TextWrapping.Wrap });
            if (record.Steps.Count == 0) content.Children.Add(new TextBlock { Text = "No completed steps recorded." });
            content.Children.Add(new TextBlock { Text = "Branch checkpoint: " + (record.Before.Length > 0 ? record.Before : "Not recorded"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            if (record.Path.Length > 0) content.Children.Add(new TextBlock { Text = record.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            var actions = new WrapRow { Spacing = 8 };
            if (record.Before.Length > 0)
            {
                var restore = new IconButton { Text = "Restore commits to a new branch", Glyph = "\uE8A7" };
                restore.Click += async (_, _) => await RestoreCheckpoint(root, record);
                actions.Children.Add(new TaskGate { Content = restore });
            }
            if (record.Path.Length > 0 && _worktrees.Contains(Key(record.Path)))
            {
                var history = new HyperlinkButton { Content = Label("View branch history", "\uE81C") };
                AutomationProperties.SetName(history, "View branch history");
                history.Click += (_, _) => DispatcherQueue.TryEnqueue(() => Go(() => new LogPage(record.Path), "log:" + record.Path));
                actions.Children.Add(history);
            }
            if (actions.Children.Count > 0) content.Children.Add(actions);
            details.Content = content;
        };
        _details[record.Id] = details;
        return details;
    }

    /// <summary>Moves what was added since start into one card. A Border has no automation peer, so the card's title carries its id.</summary>
    void Card(int start, string id)
    {
        var content = new StackPanel { Spacing = 8 };
        while (Body.Children.Count > start)
        {
            var child = Body.Children[start]; Body.Children.RemoveAt(start); content.Children.Add(child);
        }
        if (content.Children.FirstOrDefault() is TextBlock title) AutomationProperties.SetAutomationId(title, id);
        Body.Children.Add(new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(4),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = content });
    }
}
