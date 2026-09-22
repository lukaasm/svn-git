namespace Sg.Core;

public enum TaskTargetKind { Folder, Replay, Update, Backup, Activity }
public sealed record TaskFollowUp(TaskTargetKind Kind, string Path = "")
{
    public string Label => Kind switch
    {
        TaskTargetKind.Folder => "Open folder", TaskTargetKind.Replay => "Review replay",
        TaskTargetKind.Update => "Review update", TaskTargetKind.Backup => "Review backup", _ => "Open Activity"
    };
}

public enum TaskState { Waiting, Running, Succeeded, NeedsAttention, Failed, Cancelled }
public sealed record PendingWorktree(string Checkout, string Branch, string Path);
public sealed record TaskSnapshot(Guid Id, string Title, string Root, TaskState State, string Detail,
    double? Percent, DateTimeOffset Started, DateTimeOffset? Finished, bool StopRequested,
    bool StopAtBoundary, PendingWorktree? Worktree, string Log)
{
    public TaskFollowUp? FollowUp { get; init; }
    public bool Active => State is TaskState.Waiting or TaskState.Running;
    public bool Stopping => Active && StopRequested;
    public string StatusLabel => Stopping
        ? State == TaskState.Waiting ? "Cancelling queued task" : StopAtBoundary ? "Stopping after current step" : "Cancelling"
        : State switch
        {
            TaskState.Waiting => "Waiting", TaskState.Running => "Running", TaskState.Succeeded => "Completed",
            TaskState.NeedsAttention => "Needs attention", TaskState.Failed => "Failed", _ => "Cancelled"
        };
    public string CancellationExplanation => State == TaskState.Waiting
        ? StopRequested ? "No work has started. Waiting for cancellation to finish." : "Cancel before this task starts work."
        : StopRequested
            ? StopAtBoundary ? "The current step will finish before stopping. Completed steps are kept."
                : "Cancellation requested. Waiting for the operation to stop; completed steps are kept."
            : StopAtBoundary ? "Finish the current step, then stop. Completed steps are kept."
                : "Request cancellation. Completed steps are kept; stopping may take a moment.";
    public string BlockingExplanation => Stopping
        ? $"{Title} is stopping. {CancellationExplanation} Repository actions unlock after it stops."
        : $"Unavailable while {Title} is in progress. Wait for it to finish, or cancel it in Tasks. Repository actions unlock after it stops.";

}

/// <summary>Process-wide operation ownership, independent of windows. Mutations share a root's Git store.</summary>
public sealed class TaskQueue
{
    readonly object _gate = new();
    readonly List<OperationTask> _tasks = [];
    public event Action? Changed;
    public IReadOnlyList<TaskSnapshot> Snapshot()
    {
        lock (_gate) return _tasks.Select(t => t.Snapshot()).ToArray();
    }
    public TaskSnapshot? Blocking(string root) => Snapshot().FirstOrDefault(t => t.Active && SameRoot(t.Root, root));
    static bool SameRoot(string a, string b) => string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
    public OperationTask? TryStart(string title, string root, bool boundary = false, PendingWorktree? worktree = null)
    {
        OperationTask task;
        lock (_gate)
        {
            if (_tasks.Any(t => t.Snapshot().Active && SameRoot(t.Snapshot().Root, root))) return null;
            task = new OperationTask(title, root, boundary, worktree, () => Changed?.Invoke());
            _tasks.Add(task);
            // Retain recent receipts without retaining page objects or unbounded output.
            while (_tasks.Count > 100 && _tasks.FirstOrDefault(t => !t.Snapshot().Active) is { } old) _tasks.Remove(old);
        }
        Changed?.Invoke();
        return task;
    }
    public void Cancel(Guid id)
    {
        OperationTask? task;
        lock (_gate) task = _tasks.FirstOrDefault(t => t.Snapshot().Id == id);
        task?.Cancel();
    }
    public void ClearFinished()
    {
        lock (_gate) _tasks.RemoveAll(t => !t.Snapshot().Active);
        Changed?.Invoke();
    }
}

public sealed class OperationTask
{
    readonly object _gate = new();
    readonly CancellationTokenSource _cancel = new();
    readonly Action _changed;
    TaskSnapshot _state;
    internal OperationTask(string title, string root, bool boundary, PendingWorktree? worktree, Action changed)
    {
        _changed = changed;
        _state = new(Guid.NewGuid(), title, root, TaskState.Waiting, "Waiting for repository access", null,
            DateTimeOffset.Now, null, false, boundary, worktree, "");
    }
    public CancellationToken Token => _cancel.Token;
    public TaskSnapshot Snapshot() { lock (_gate) return _state; }
    public void Running() => Change(s => s.Active ? s with { State = TaskState.Running, Detail = "Starting…" } : s);
    public void Progress(string detail, double? percent = null) => Change(s => s.Active ? s with { Detail = detail, Percent = percent } : s);
    public void Append(string line) => Change(s => s with { Log = Tail(s.Log + line + Environment.NewLine) });
    static string Tail(string text) => text.Length <= 24000 ? text : "[Earlier output omitted]\n" + text[^23000..];
    public void Finish(TaskState state, string detail, TaskFollowUp? followUp = null)
    {
        if (state is TaskState.Waiting or TaskState.Running) throw new ArgumentException("A result must be terminal.", nameof(state));
        Change(s => s.Active ? s with { State = state, Detail = detail, Percent = null, Finished = DateTimeOffset.Now, FollowUp = followUp } : s);
    }
    public void Cancel()
    {
        lock (_gate)
        {
            if (!_state.Active || _state.StopRequested) return;
            _state = _state with { StopRequested = true };
        }
        _cancel.Cancel();
        _changed();
    }
    void Change(Func<TaskSnapshot, TaskSnapshot> change)
    {
        lock (_gate) _state = change(_state);
        _changed();
    }
}
