using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Sg.Core;

namespace Sg.App;

public sealed class ActivityPage : WorkflowPage
{
    const int PageSize = 20;
    readonly TextBox _search = new() { PlaceholderText = "Search all activity by branch, checkout, status, or step", MinWidth = 240 };
    readonly ComboBox _filter = new() { ItemsSource = new[] { "All history", "Needs attention", "Finished" }, SelectedIndex = 0 };
    readonly TextBlock _count = new() { Style = (Style)Application.Current.Resources["Secondary"] };
    readonly IconButton _more = new() { Text = "Show 20 more", Glyph = "\uE70E", HorizontalAlignment = HorizontalAlignment.Left };
    readonly Grid _toolbar = new() { ColumnSpacing = 8 };
    readonly UiRefresh _searchRefresh;
    List<OperationRecord> _records = [];
    List<OperationRecord> _matches = [];
    HashSet<string> _existingPaths = [];
    SgRoot? _root;
    int _itemsStart, _shown;
    DateTime? _day;
    public ActivityPage() : base("Activity and recovery")
    {
        AutomationProperties.SetAutomationId(_search, "ActivitySearch");
        AutomationProperties.SetName(_search, "Search all activity");
        AutomationProperties.SetAutomationId(_filter, "ActivityFilter");
        AutomationProperties.SetName(_filter, "Filter activity");
        AutomationProperties.SetAutomationId(_count, "ActivityResults");
        AutomationProperties.SetLiveSetting(_count, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_more, "ActivityMore");
        _searchRefresh = new(DispatcherQueue, () => { if (IsLoaded && _root != null) Render(reset: true); }, TimeSpan.FromMilliseconds(150));
        _search.TextChanged += (_, _) => _searchRefresh.Request();
        _filter.SelectionChanged += (_, _) => _searchRefresh.Request();
        _more.Click += (_, _) => Render(reset: false);
        _toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _toolbar.Children.Add(_search); Grid.SetColumn(_filter, 1); _toolbar.Children.Add(_filter);
    }
    public override void OnHidden() { base.OnHidden(); _root = null; }
    protected override async Task Reload()
    {
        using var generation = BeginRead(); var root = Session.Require();
        _root = null;
        Body.Children.Clear();
        var loading = ReadNotice("Loading recorded operations and recovery checkpoints…", "ActivityLoading");
        var data = await generation.Run(Pane, () =>
        {
            var records = Operations.List(root);
            var replays = root.Git.WorktreeList().Where(w => !w.Bare && Directory.Exists(w.Path) && Conflicts.HasPending(root.Git, w.Path) && records.All(r => r.Terminal || r.Path != w.Path)).ToList();
            var existingPaths = records.Select(r => r.Path).Distinct().Where(Directory.Exists).ToHashSet();
            return new { Records = records, Replays = replays, ExistingPaths = existingPaths };
        });
        if (!Current(generation)) return;
        if (data == null) { ReadFailed(loading, "Could not read the activity timeline. Retry or open the error log for details."); return; }
        _records = data.Records.OrderByDescending(r => r.Updated).ToList();
        _existingPaths = data.ExistingPaths;
        Body.Children.Clear();
        Status($"{_records.Count(r => !r.Terminal) + data.Replays.Count} need attention · {_records.Count(r => r.Terminal)} finished", ChipSeverity.Neutral, "\uE81C");
        Body.Children.Add(_toolbar);
        Body.Children.Add(_count);
        var refresh = Action("Refresh", Reload, glyph: "\uE72C");
        AutomationProperties.SetAutomationId(refresh, "ActivityRefresh");
        foreach (var replay in data.Replays)
        {
            var entryStart = Body.Children.Count;
            Text("Existing replay · " + replay.Branch, true);
            Status("Paused replay", ChipSeverity.Caution, "\uE7BA");
            Details("Replay details", [replay.Path, "Paused before operation history was recorded"]);
            Link("Review replay", "\uE90F", () => new ConflictPage(replay.Path), "resolve:" + replay.Path);
            TimelineEntry(entryStart, "PausedReplay");
        }
        _itemsStart = Body.Children.Count;
        _root = root;
        Render(reset: true);
    }

    void Render(bool reset)
    {
        if (_root is not { } root) return;
        Body.Children.Remove(_more);
        if (reset)
        {
            var query = _search.Text.Trim();
            _matches = _records.Where(r => (_filter.SelectedIndex switch { 1 => !r.Terminal, 2 => r.Terminal, _ => true })
                && (query.Length == 0 || string.Join('\n', OperationTitle(r), r.Branch, r.Checkout, r.PhaseLabel, r.Detail,
                    string.Join('\n', r.Steps)).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            while (Body.Children.Count > _itemsStart) Body.Children.RemoveAt(_itemsStart);
            _shown = 0; _day = null;
        }
        var end = Math.Min(_shown + PageSize, _matches.Count);
        foreach (var record in _matches.Skip(_shown).Take(end - _shown))
        {
            if (_day != record.Updated.LocalDateTime.Date) {
                _day = record.Updated.LocalDateTime.Date;
                Text(_day.Value.ToString("D"), true);
            }
            var entryStart = Body.Children.Count;
            Text(OperationTitle(record) + " · " + record.Branch, true);
            var severity = record.Phase == OperationPhase.Completed ? ChipSeverity.Success : record.Terminal ? ChipSeverity.Neutral : ChipSeverity.Caution;
            Status(record.PhaseLabel, severity, record.Phase == OperationPhase.Completed ? "\uE73E" : record.Terminal ? "\uE81C" : "\uE7BA");
            Text($"{record.Updated.LocalDateTime:g} · {record.Checkout}");
            if (!string.IsNullOrWhiteSpace(record.Detail)) Text(record.Detail);
            if (!record.Terminal) Link(record.Action, "\uE90F", () => new UpdateBranchPage(record.Path), "update-branch:" + record.Path);
            if (_existingPaths.Contains(record.Path)) Link("View branch history", "\uE81C", () => new LogPage(record.Path), "log:" + record.Path);
            var details = new Expander { Header = Label("Steps and checkpoint", "\uE8A5"), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(details, "Steps and checkpoint");
            details.Expanding += (_, _) =>
            {
                if (details.Content != null) return;
                var content = new StackPanel { Spacing = 8 };
                foreach (var step in record.Steps) content.Children.Add(new TextBlock { Text = "✓ " + step, TextWrapping = TextWrapping.Wrap });
                if (record.Steps.Count == 0) content.Children.Add(new TextBlock { Text = "No completed steps recorded." });
                content.Children.Add(new TextBlock { Text = "Branch checkpoint: " + (record.Before.Length > 0 ? record.Before : "Not recorded"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                content.Children.Add(new TextBlock { Text = record.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                if (record.Before.Length > 0)
                {
                    var restore = new IconButton { Text = "Restore commits to a separate branch", Glyph = "\uE8A7" };
                    restore.Click += async (_, _) =>
                    {
                        var name = record.Branch + "-recovered-" + Guid.NewGuid().ToString("N")[..8];
                        if (!await Dialogs.Confirm(this, "Restore checkpoint to a new branch?",
                            $"This will:\n• Create branch {name}.\n• Create its worktree at {root.WorktreePathFor(name)}.\n• Restore the recorded commits at {record.Before}.\n\nYour current branch, working files, and shelves stay in place. SVN and remote backups are unchanged.", "Create recovery branch")) return;
                        await Execute("Restore checkpoint", () => Operations.RestoreCheckpoint(root, record.Id, name), new(record.Checkout, name, root.WorktreePathFor(name)));
                    };
                    content.Children.Add(new TaskGate { Content = restore });
                }
                details.Content = content;
            };
            Body.Children.Add(details);
            TimelineEntry(entryStart, "ActivityEntry_" + record.Id);
        }
        _shown = end;
        _count.Text = _matches.Count == 0 ? (_records.Count == 0 ? "No recorded operations yet." : "No matching activity. Change the search or filter.")
            : $"Showing {_shown} of {_matches.Count} operations";
        if (_shown < _matches.Count)
        {
            _more.Text = $"Show {Math.Min(PageSize, _matches.Count - _shown)} more";
            Body.Children.Add(_more);
        }
    }

    void TimelineEntry(int start, string id)
    {
        var content = new StackPanel { Spacing = 8 };
        while (Body.Children.Count > start) {
            var child = Body.Children[start]; Body.Children.RemoveAt(start); content.Children.Add(child);
        }
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border { Width = 2, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
        row.Children.Add(new FontIcon { Glyph = "\uE915", FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 0, 0) });
        var card = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(4),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = content };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(card, id);
        Grid.SetColumn(card, 1); row.Children.Add(card); Body.Children.Add(row);
    }
}
