using Sg.Core;

namespace Sg.Core.Tests;

public class TaskQueueTests
{
    [Theory]
    [InlineData(false, false, "Cancelling queued task")]
    [InlineData(true, false, "Cancelling")]
    [InlineData(true, true, "Stopping after current step")]
    public void Stop_request_stays_active_until_the_worker_reports_its_actual_result(bool running, bool boundary, string label)
    {
        var queue = new TaskQueue();
        var task = queue.TryStart("Import", "root", boundary)!;
        if (running) task.Running();
        task.Cancel();
        var stopping = task.Snapshot();
        Assert.True(stopping.Stopping);
        Assert.True(stopping.Active);
        Assert.Equal(label, stopping.StatusLabel);
        Assert.Contains("unlock after it stops", stopping.BlockingExplanation);
        Assert.DoesNotContain("cancel it in Tasks", stopping.BlockingExplanation);
        Assert.Null(queue.TryStart("colliding action", "root"));
        // A completed final step can legitimately win the race with cancellation.
        task.Finish(TaskState.Succeeded, "Finished before cancellation took effect.");
        Assert.False(task.Snapshot().Stopping);
        Assert.Equal("Completed", task.Snapshot().StatusLabel);
        Assert.NotNull(queue.TryStart("next action", "root"));
    }

    [Fact]
    public void Result_actions_use_typed_destinations_and_survive_late_updates()
    {
        var replay = TaskResults.FollowUp(new ImportResult { Waiting = true, Path = "imported" });
        Assert.Equal(new TaskFollowUp(TaskTargetKind.Replay, "imported"), replay);
        Assert.Equal(new TaskFollowUp(TaskTargetKind.Update, "feature"),
            TaskResults.FollowUp(new OperationRecord { Phase = OperationPhase.NeedsReview, Path = "feature" }));
        Assert.Equal(new TaskFollowUp(TaskTargetKind.Backup), TaskResults.FollowUp(new BackupResult { Error = "offline" }));
        Assert.Null(TaskResults.FollowUp("unstructured output mentioning a path"));
        var queue = new TaskQueue();
        var task = queue.TryStart("import", "root")!;
        task.Finish(TaskState.NeedsAttention, "paused", replay);
        task.Progress("late callback", 100);
        task.Finish(TaskState.Succeeded, "late completion", new(TaskTargetKind.Folder, "wrong"));
        Assert.Equal(replay, Assert.Single(queue.Snapshot()).FollowUp);
    }

    [Fact]
    public void Reservation_is_atomic_and_released_only_after_completion()
    {
        var queue = new TaskQueue();
        var winners = new System.Collections.Concurrent.ConcurrentBag<OperationTask>();
        Parallel.For(0, 40, i => { if (queue.TryStart("task " + i, @"D:\root") is { } task) winners.Add(task); });
        var winner = Assert.Single(winners);
        Assert.Null(queue.TryStart("collision", @"d:\ROOT\"));
        Assert.NotNull(queue.TryStart("other root", @"D:\other"));
        winner.Cancel();
        Assert.Null(queue.TryStart("still cancelling", @"D:\root"));
        winner.Finish(TaskState.Cancelled, "Cancelled");
        Assert.NotNull(queue.TryStart("next", @"D:\root"));
    }

    [Fact]
    public void Progress_result_and_placeholder_survive_observer_replacement()
    {
        var queue = new TaskQueue();
        var placeholder = new PendingWorktree("trunk", "feature", @"D:\root\feature");
        var task = queue.TryStart("Import feature", @"D:\root", worktree: placeholder)!;
        Assert.Equal(placeholder, Assert.Single(queue.Snapshot()).Worktree);
        task.Running(); task.Progress("Importing", 45); task.Append("commit applied");
        // A newly opened view needs only a snapshot, not the starting page or its event subscription.
        var reopened = Assert.Single(queue.Snapshot());
        Assert.Equal(45, reopened.Percent);
        Assert.Contains("commit applied", reopened.Log);
        task.Finish(TaskState.NeedsAttention, "Resolve conflict");
        task.Progress("late progress", 100);
        var result = Assert.Single(queue.Snapshot());
        Assert.False(result.Active);
        Assert.NotNull(result.Finished);
        Assert.Equal("Resolve conflict", result.Detail);
        Assert.Equal(TaskState.NeedsAttention, result.State);
    }

    [Fact]
    public void Cancelling_a_waiting_task_signals_token_without_releasing_its_reservation()
    {
        var queue = new TaskQueue();
        var task = queue.TryStart("Wait", "root", boundary: true)!;
        queue.Cancel(task.Snapshot().Id);
        queue.Cancel(task.Snapshot().Id);
        Assert.True(task.Token.IsCancellationRequested);
        Assert.True(task.Snapshot().StopRequested);
        Assert.True(task.Snapshot().StopAtBoundary);
        Assert.NotNull(queue.Blocking("root"));
        queue.ClearFinished();
        Assert.Single(queue.Snapshot());
    }

    [Fact]
    public void Clearing_receipts_preserves_active_tasks_and_output_is_bounded()
    {
        var queue = new TaskQueue();
        var completed = queue.TryStart("Old", "root")!;
        completed.Append(new string('a', 30000));
        Assert.InRange(completed.Snapshot().Log.Length, 1, 24000);
        completed.Finish(TaskState.Failed, "Failed");
        var active = queue.TryStart("New", "root")!;
        queue.ClearFinished();
        Assert.Equal(active.Snapshot().Id, Assert.Single(queue.Snapshot()).Id);
        completed.Finish(TaskState.Succeeded, "late completion");
        Assert.Equal(TaskState.Failed, completed.Snapshot().State);
    }

    [Fact]
    public void Retention_never_evicts_an_active_task()
    {
        var queue = new TaskQueue();
        var active = queue.TryStart("Long task", "root")!;
        for (var i = 0; i < 120; ++i) queue.TryStart("short", "other")!.Finish(TaskState.Succeeded, "Done");
        Assert.Equal(100, queue.Snapshot().Count);
        Assert.Contains(queue.Snapshot(), t => t.Id == active.Snapshot().Id);
    }

    [Fact]
    public void Nonthrowing_partial_results_are_not_reported_as_success()
    {
        object[] partial =
        [
            new ImportResult { Waiting = true }, new RestoreResult { Waiting = true },
            new RestoreResult { WipWhy = "Saved on shelf" }, new RebaseResult { Conflict = true },
            new ResolveResult { Conflict = true }, new OperationRecord { Phase = OperationPhase.NeedsReview },
            new PushResult { AllCommitted = false }, new BackupResult { Items = [new BackupItem { State = "behind" }] },
            new SyncResult { Conflicts = 1 }, new ShelfRestoreResult { Conflicted = ["file"] },
            new MergeResult { Conflicts = ["file"] }, new ReviewRecord { Checks = [new() { ExitCode = 1 }] }
        ];
        foreach (var result in partial) Assert.Equal(TaskState.NeedsAttention, TaskResults.Describe(result).State);
        Assert.Equal(TaskState.Failed, TaskResults.Describe(new BackupResult { Error = "No connection" }).State);
        Assert.Equal(TaskState.Succeeded, TaskResults.Describe(new OperationRecord { Phase = OperationPhase.Completed }).State);
        Assert.Equal(TaskState.Succeeded, TaskResults.Describe(new PushResult { AppliedOnly = true }).State);
    }
}
