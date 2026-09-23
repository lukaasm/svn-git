using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>Shared schedule and durable success history, without contacting the backup remote.</summary>
public sealed partial class BackupStatusView : UserControl
{
    readonly UiRefresh _refresh;
    string? _historyKey;
    int _generation;

    public BackupStatusView()
    {
        InitializeComponent();
        _refresh = new(DispatcherQueue, () => { if (IsLoaded) Refresh(); }, TimeSpan.FromMilliseconds(100));
        Loaded += (_, _) =>
        {
            _historyKey = null;
            Session.BackupScheduleChanged += Changed;
            Session.Settings.Saved += Changed;
            Session.RootChanged += Changed;
            Session.Tasks.StateChanged += Changed;
            Changed();
        };
        Unloaded += (_, _) =>
        {
            _generation++;
            Session.BackupScheduleChanged -= Changed;
            Session.Settings.Saved -= Changed;
            Session.RootChanged -= Changed;
            Session.Tasks.StateChanged -= Changed;
        };
    }

    void Changed() => _refresh.Request();

    void Refresh()
    {
        var root = Session.Root;
        var configured = root != null && Backup.Configured(root);
        var next = configured && Session.Settings.BackupMinutes > 0 ? Session.NextBackup : null;
        NextRun.Text = !configured ? "Set a backup repository in Settings"
            : Session.Settings.BackupMinutes <= 0 ? "Automatic backups are off"
            : next.HasValue ? $"{Format(next.Value)} · every {Session.Settings.BackupMinutes} min"
            : "Open the overview to start the backup timer";
        AutomationProperties.SetHelpText(NextRun, next?.ToString("O") ?? "");
        ScheduleChip.Severity = next.HasValue ? ChipSeverity.Attention : ChipSeverity.Neutral;

        var skip = Session.LastSkippedBackup;
        var relevant = configured && skip?.Root == root!.RootPath && skip.Url == root.Config.Backup!.Url && skip.Prefix == root.Config.Backup.Prefix;
        SkippedNotice.IsOpen = relevant;
        if (relevant)
        {
            SkippedReason.Text = $"{Format(skip!.When)} · {skip.Reason} No backup started during that interval.";
            BlockingTask.Tag = skip.TaskId;
            var task = Session.Tasks.Snapshot().FirstOrDefault(t => t.Id == skip.TaskId);
            BlockingTask.Visibility = task != null ? Visibility.Visible : Visibility.Collapsed;
            BlockingTask.Text = task?.Active == true ? "View blocking task" : "View task result";
        }

        // Task progress can arrive many times a second. Read the small receipt only on load,
        // destination changes, or task completion, and keep even that I/O off the UI thread.
        var finished = Session.Tasks.Snapshot().Where(t => t.Root == root?.RootPath && !t.Active).Select(t => t.Id);
        var key = $"{root?.RootPath}\n{root?.Config.Backup?.Url}\n{root?.Config.Backup?.Prefix}\n{string.Join(',', finished)}";
        if (key == _historyKey) return;
        _historyKey = key;
        _ = ReadSuccessAsync(root, ++_generation);
    }

    void BlockingTask_Click(object sender, RoutedEventArgs e)
    {
        if (BlockingTask.Tag is not Guid id) return;
        for (DependencyObject? parent = this; parent != null; parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent))
            if (parent is SgPage page) { page.Host?.Tasks?.ShowTask(id); return; }
    }

    async Task ReadSuccessAsync(SgRoot? root, int generation)
    {
        LastSuccess.Text = "Reading backup history…";
        SuccessChip.Severity = ChipSeverity.Neutral;
        AutomationProperties.SetHelpText(LastSuccess, "");
        var when = root == null ? null : await Task.Run(() => Backup.LastSuccess(root));
        if (!IsLoaded || generation != _generation) return;
        LastSuccess.Text = when.HasValue ? Format(when.Value) : "No successful backup recorded for this destination";
        SuccessChip.Glyph = when.HasValue ? "\uE73E" : "\uE74E";
        SuccessChip.Severity = when.HasValue ? ChipSeverity.Success : ChipSeverity.Neutral;
        AutomationProperties.SetHelpText(LastSuccess, when?.ToString("O") ?? "");
    }

    static string Format(DateTimeOffset when) => when.ToLocalTime().ToString("ddd, d MMM yyyy · HH:mm:ss");
}
