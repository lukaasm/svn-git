using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Sg.Core;

namespace Sg.App;

/// <summary>A compact footer outside the page host, sharing the page's content edges.</summary>
public sealed class TaskPane : UserControl
{
    readonly TextBlock _summary = new() { Text = "Tasks", TextTrimming = TextTrimming.CharacterEllipsis };
    readonly ProgressBar _progress = new() { Height = 3, Visibility = Visibility.Collapsed };
    readonly StackPanel _rows = new();
    readonly Dictionary<Guid, Row> _views = [];
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    readonly TextBlock _empty = new() { Text = "No tasks this session.", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _body = new() { Visibility = Visibility.Collapsed };
    readonly Button _toggle;
    readonly ComboBox _filter = new() { MinWidth = 170, SelectedIndex = 0 };
    readonly TextBlock _viewSummary = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    readonly Button _clear;
    public NavHost? Navigation { get; set; }
    sealed record Row(Border Card, Button Toggle, TextBlock Title, TextBlock Detail, TextBlock Output, Button Cancel, ProgressBar Bar, Button FollowUp);

    public TaskPane()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_summary);
        var chevron = new FontIcon { Glyph = "\uE70D", FontSize = 12 };
        Grid.SetColumn(chevron, 1); header.Children.Add(chevron);
        _toggle = new Button
        {
            Content = header, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["QuietButton"]
        };
        AutomationProperties.SetAutomationId(_toggle, "TaskQueueToggle");
        AutomationProperties.SetAutomationId(_summary, "TaskQueueSummary");
        _toggle.Click += (_, _) =>
        {
            _body.Visibility = _body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            chevron.Glyph = _body.Visibility == Visibility.Visible ? "\uE70E" : "\uE70D";
        };
        var toolbar = new Grid { Padding = new Thickness(12, 0, 12, 8), ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetAutomationId(_filter, "TaskFilter");
        AutomationProperties.SetName(_filter, "Show tasks");
        var filters = new[] { "All tasks", "Active", "Needs attention", "Finished" };
        for (var i = 0; i < filters.Length; i++)
        {
            var item = new ComboBoxItem { Content = filters[i] };
            AutomationProperties.SetAutomationId(item, "TaskFilter" + i);
            _filter.Items.Add(item);
        }
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Refresh();
        AutomationProperties.SetAutomationId(_viewSummary, "TaskFilterSummary");
        var view = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        view.Children.Add(_filter); view.Children.Add(_viewSummary);
        toolbar.Children.Add(view);
        _clear = new Button { Content = "Clear finished", Padding = new Thickness(8, 4, 8, 4), Style = (Style)Application.Current.Resources["QuietButton"] };
        AutomationProperties.SetAutomationId(_clear, "ClearFinishedTasks");
        const string clearHelp = "Clear all finished results from this session, including failures, across every filter. Running tasks and saved recovery records are kept.";
        ToolTipService.SetToolTip(_clear, clearHelp);
        AutomationProperties.SetHelpText(_clear, clearHelp);
        _clear.Click += (_, _) => { Session.Tasks.ClearFinished(); Refresh(); };
        Grid.SetColumn(_clear, 1); toolbar.Children.Add(_clear);
        _body.Children.Add(toolbar);
        var list = new StackPanel(); list.Children.Add(_empty); list.Children.Add(_rows);
        _body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var layout = new StackPanel(); layout.Children.Add(_toggle); layout.Children.Add(_progress); layout.Children.Add(_body);
        var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = layout };
        Content = border;
        void Theme() => border.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        Theme(); ActualThemeChanged += (_, _) => Theme();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }
    void Refresh()
    {
        var tasks = Session.Tasks.Snapshot();
        var active = tasks.Where(t => t.Active).ToArray();
        var attention = tasks.Count(t => t.State is TaskState.NeedsAttention or TaskState.Failed);
        var current = active.FirstOrDefault();
        _summary.Text = $"Tasks · {active.Length} active · {tasks.Count(t => !t.Active)} finished"
            + (attention > 0 ? $" · {attention} need attention" : "")
            + (current == null ? "" : $" — {current.Title}: {Message(current).Split('\n')[0]}");
        AutomationProperties.SetName(_toggle, _summary.Text);
        _progress.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        _progress.IsIndeterminate = current != null && current.Percent == null;
        _progress.Value = current?.Percent ?? 0;
        bool Visible(TaskSnapshot task) => _filter.SelectedIndex switch
        {
            1 => task.Active,
            2 => task.State is TaskState.NeedsAttention or TaskState.Failed,
            3 => !task.Active,
            _ => true
        };
        var shown = tasks.Count(Visible);
        _viewSummary.Text = $"{shown} of {tasks.Count} tasks";
        _clear.IsEnabled = tasks.Any(t => !t.Active);
        _empty.Text = tasks.Count == 0 ? "No tasks this session." : _filter.SelectedIndex switch
        {
            1 => "No active tasks. Finished results remain in All tasks.",
            2 => "No tasks need attention.",
            3 => "No finished tasks yet. Running work remains in Active.",
            _ => "No tasks this session."
        };
        _empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var id in _views.Keys.Except(tasks.Select(t => t.Id)).ToArray())
        {
            _rows.Children.Remove(_views[id].Card); _views.Remove(id);
        }
        foreach (var task in tasks)
        {
            if (!_views.TryGetValue(task.Id, out var row))
            {
                row = MakeRow(task.Id);
                _views.Add(task.Id, row);
                _rows.Children.Insert(0, row.Card);
            }
            row.Card.Visibility = Visible(task) ? Visibility.Visible : Visibility.Collapsed;
            var elapsed = (task.Finished ?? DateTimeOffset.Now) - task.Started;
            row.Title.Text = $"{State(task)} · {task.Title} · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
            AutomationProperties.SetName(row.Toggle, row.Title.Text);
            var detail = Message(task) + "\n" + task.Root;
            if (row.Detail.Text != detail) row.Detail.Text = detail;
            if (row.Output.Text != task.Log) row.Output.Text = task.Log;
            row.Cancel.Visibility = task.Active ? Visibility.Visible : Visibility.Collapsed;
            row.Cancel.IsEnabled = !task.StopRequested;
            row.Cancel.Content = task.StopRequested ? "Stop requested" : task.StopAtBoundary ? "Stop after current step" : "Cancel task";
            ToolTipService.SetToolTip(row.Cancel, task.CancellationExplanation);
            AutomationProperties.SetHelpText(row.Cancel, task.CancellationExplanation);
            row.Bar.Visibility = task.Active ? Visibility.Visible : Visibility.Collapsed;
            row.Bar.IsIndeterminate = task.Active && task.Percent == null;
            row.Bar.Value = task.Percent ?? 0;
            row.FollowUp.Visibility = !task.Active && task.FollowUp != null ? Visibility.Visible : Visibility.Collapsed;
            if (task.FollowUp is { } link)
            {
                row.FollowUp.Content = link.Label;
                var available = link.Kind == TaskTargetKind.Folder || SameRoot(task.Root, Session.Root?.RootPath);
                row.FollowUp.IsEnabled = available;
                var explanation = available ? link.Path : "Open this root first: " + task.Root;
                ToolTipService.SetToolTip(row.FollowUp, explanation);
                AutomationProperties.SetHelpText(row.FollowUp, explanation);
            }
        }
    }
    static bool SameRoot(string a, string? b) => string.Equals(a.TrimEnd('/', '\\'), b?.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
    void OpenResult(Guid id)
    {
        var task = Session.Tasks.Snapshot().FirstOrDefault(t => t.Id == id);
        if (task == null || task.Active || task.FollowUp is not { } link) return;
        if (link.Kind == TaskTargetKind.Folder)
        {
            if (Directory.Exists(link.Path)) Session.OpenInExplorer(link.Path);
            else OutputWindow.Show("The result folder is no longer available: " + link.Path);
            return;
        }
        if (!SameRoot(task.Root, Session.Root?.RootPath) || Navigation == null) return;
        switch (link.Kind)
        {
            case TaskTargetKind.Replay when Directory.Exists(link.Path):
                Navigation.Go(() => new ConflictPage(link.Path), "resolve:" + link.Path); break;
            case TaskTargetKind.Update when Directory.Exists(link.Path):
                Navigation.Go(() => new UpdateBranchPage(link.Path), "update-branch:" + link.Path); break;
            case TaskTargetKind.Backup:
                Navigation.Go(() => new BackupPage(), "backup"); break;
            default:
                Navigation.Go(() => new ActivityPage(), "activity"); break;
        }
    }

    static string Message(TaskSnapshot t) => t.Stopping
        ? t.StatusLabel + ": " + t.CancellationExplanation + " Repository actions remain blocked.\n" + t.Detail : t.Detail;
    internal static string State(TaskSnapshot t) => t.State switch
    {
        TaskState.Succeeded => "✓ " + t.StatusLabel,
        TaskState.NeedsAttention or TaskState.Failed => "! " + t.StatusLabel,
        _ => t.StatusLabel
    };
    Row MakeRow(Guid id)
    {
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var output = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var cancel = new Button { Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(cancel, "CancelTask_" + id);
        cancel.Click += (_, _) => { Session.Tasks.Cancel(id); Refresh(); };
        var followUp = new Button { Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(followUp, "TaskResult_" + id);
        followUp.Click += (_, _) => OpenResult(id);
        var bar = new ProgressBar { Height = 3 };
        var body = new StackPanel { Spacing = 8, Padding = new Thickness(12, 0, 12, 12), Visibility = Visibility.Collapsed };
        body.Children.Add(detail); body.Children.Add(followUp); body.Children.Add(bar); body.Children.Add(output);
        var toggle = new Button { Content = title, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["QuietButton"] };
        AutomationProperties.SetAutomationId(toggle, "Task_" + id);
        toggle.Click += (_, _) => body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(toggle); Grid.SetColumn(cancel, 1); heading.Children.Add(cancel);
        var layout = new StackPanel(); layout.Children.Add(heading); layout.Children.Add(body);
        var card = new Border { Child = layout, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] };
        return new(card, toggle, title, detail, output, cancel, bar, followUp);
    }
}

/// <summary>The parent owns busy gating; a child's validation and selection state stay untouched.</summary>
public sealed class TaskGate : ContentControl
{
    ContentControl? _gate;
    FrameworkElement? _child;
    string _help = "";
    bool _blocked;
    bool _explicitHelp;
    // Preserve normal help when a page refresh arrives before the task-change handler.
    public static string Explain(DependencyObject control, string defaultHelp)
    {
        for (DependencyObject? parent = control; parent != null; parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
        {
            if (parent is not TaskGate gate) continue;
            gate._help = defaultHelp;
            gate._explicitHelp = true;
            return Session.Tasks.Blocking(Session.Root?.RootPath ?? "")?.BlockingExplanation ?? defaultHelp;
        }
        return defaultHelp;
    }
    public TaskGate()
    {
        IsTabStop = false;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) =>
        {
            if (_gate == null && Content is FrameworkElement child)
            {
                _child = child;
                if (!_explicitHelp) _help = AutomationProperties.GetHelpText(child);
                SetBinding(VisibilityProperty, new Binding { Source = child, Path = new PropertyPath("Visibility"), Mode = BindingMode.OneWay });
                Content = null;
                _gate = new ContentControl
                {
                    Content = child, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false
                };
                Content = _gate;
            }
            Session.Tasks.Changed += Changed;
            Changed();
        };
        Unloaded += (_, _) => Session.Tasks.Changed -= Changed;
    }
    void Changed()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(Changed); return; }
        var busy = Session.Tasks.Blocking(Session.Root?.RootPath ?? "");
        if (_gate != null) _gate.IsEnabled = busy == null;
        var reason = busy?.BlockingExplanation;
        // Keep the wrapper enabled so the tooltip remains reachable over its disabled child.
        ToolTipService.SetToolTip(this, reason);
        AutomationProperties.SetHelpText(this, reason ?? "");
        if (_child != null)
        {
            if (reason != null && !_blocked && !_explicitHelp) _help = AutomationProperties.GetHelpText(_child);
            if (reason != null || _blocked) AutomationProperties.SetHelpText(_child, reason ?? _help);
            if (_explicitHelp) ToolTipService.SetToolTip(_child, reason ?? _help);
        }
        _blocked = reason != null;
    }
}

/// <summary>A pending worktree has a place before any folder is created. It has no destructive actions.</summary>
public sealed class PendingWorktrees : UserControl
{
    readonly StackPanel _rows = new() { Spacing = 8 };
    public string? Checkout { get; set; }
    public PendingWorktrees()
    {
        Content = _rows;
        Loaded += (_, _) => { Session.Tasks.Changed += Changed; Changed(); };
        Unloaded += (_, _) => Session.Tasks.Changed -= Changed;
    }
    public void Refresh() => Changed();
    void Changed()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(Changed); return; }
        var tasks = Session.Tasks.Snapshot().Where(t => t.Active && t.Root == Session.Root?.RootPath
            && t.Worktree != null && t.Worktree.Checkout == Checkout).ToArray();
        var signature = string.Join("|", tasks.Select(t => t.Id));
        if (Equals(Tag, signature)) return;
        Tag = signature;
        _rows.Children.Clear();
        foreach (var task in tasks)
        {
            var body = new StackPanel { Spacing = 8, Padding = new Thickness(16), Opacity = 0.7 };
            body.Children.Add(new TextBlock { Text = task.Worktree!.Branch + " · Preparing worktree", FontSize = 18 });
            body.Children.Add(new ProgressBar { IsIndeterminate = true });
            body.Children.Add(new TextBlock { Text = task.Worktree.Path + "\n" + task.Title + " — follow progress and results in Tasks.", TextWrapping = TextWrapping.Wrap });
            _rows.Children.Add(new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Child = body });
        }
        Visibility = tasks.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
