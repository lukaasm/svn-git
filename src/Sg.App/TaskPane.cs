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
    sealed record Row(Border Card, Button Toggle, TextBlock Title, TextBlock Detail, TextBlock Output, Button Cancel, ProgressBar Bar);

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
        toolbar.Children.Add(new TextBlock { Text = "This session · select a task for its result and output", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var clear = new Button { Content = "Clear finished", Padding = new Thickness(8, 4, 8, 4), Style = (Style)Application.Current.Resources["QuietButton"] };
        clear.Click += (_, _) => Session.Tasks.ClearFinished();
        Grid.SetColumn(clear, 1); toolbar.Children.Add(clear);
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
        _empty.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            var elapsed = (task.Finished ?? DateTimeOffset.Now) - task.Started;
            row.Title.Text = $"{State(task)} · {task.Title} · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
            AutomationProperties.SetName(row.Toggle, row.Title.Text);
            var detail = Message(task) + "\n" + task.Root;
            if (row.Detail.Text != detail) row.Detail.Text = detail;
            if (row.Output.Text != task.Log) row.Output.Text = task.Log;
            row.Cancel.Visibility = task.Active ? Visibility.Visible : Visibility.Collapsed;
            row.Cancel.IsEnabled = !task.StopRequested;
            row.Cancel.Content = task.StopAtBoundary ? "Stop after current step" : "Cancel task";
            row.Bar.Visibility = task.Active ? Visibility.Visible : Visibility.Collapsed;
            row.Bar.IsIndeterminate = task.Active && task.Percent == null;
            row.Bar.Value = task.Percent ?? 0;
        }
    }
    static string Message(TaskSnapshot t) => t.StopRequested && t.Active
        ? (t.StopAtBoundary ? "Will stop after the current step. " : "Cancelling… ") + t.Detail : t.Detail;
    internal static string State(TaskSnapshot t) => t.State switch
    {
        TaskState.Waiting => "Waiting", TaskState.Running => "Running", TaskState.Succeeded => "✓ Completed",
        TaskState.NeedsAttention => "! Needs attention", TaskState.Failed => "! Failed", _ => "Cancelled"
    };
    Row MakeRow(Guid id)
    {
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var output = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        var cancel = new Button { Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(cancel, "CancelTask_" + id);
        cancel.Click += (_, _) => Session.Tasks.Cancel(id);
        var bar = new ProgressBar { Height = 3 };
        var body = new StackPanel { Spacing = 8, Padding = new Thickness(12, 0, 12, 12), Visibility = Visibility.Collapsed };
        body.Children.Add(detail); body.Children.Add(bar); body.Children.Add(output);
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
        return new(card, toggle, title, detail, output, cancel, bar);
    }
}

/// <summary>The parent owns busy gating; a child's validation and selection state stay untouched.</summary>
public sealed class TaskGate : ContentControl
{
    public TaskGate()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) =>
        {
            if (Content is FrameworkElement child)
                SetBinding(VisibilityProperty, new Binding { Source = child, Path = new PropertyPath("Visibility"), Mode = BindingMode.OneWay });
            Session.Tasks.Changed += Changed;
            Changed();
        };
        Unloaded += (_, _) => Session.Tasks.Changed -= Changed;
    }
    void Changed()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(Changed); return; }
        var busy = Session.Tasks.Blocking(Session.Root?.RootPath ?? "");
        IsEnabled = busy == null;
        ToolTipService.SetToolTip(this, busy == null ? null : $"Waiting for {busy.Title}. See Tasks below.");
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
