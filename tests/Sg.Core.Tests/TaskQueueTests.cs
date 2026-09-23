using Sg.Core;

namespace Sg.Core.Tests;

public class TaskQueueTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Log_bursts_do_not_refresh_ownership_observers_but_lifecycle_changes_do()
    {
        var queue = new TaskQueue();
        var states = new List<TaskSnapshot?>();
        var updates = 0;
        queue.StateChanged += () => states.Add(queue.Snapshot().LastOrDefault());
        queue.Changed += () => updates++;
        var task = queue.TryStart("Pull from SVN", "root")!;
        task.Running();
        for (var i = 0; i < 1000; i++) { task.Append("Applied commit " + i); task.Progress("Replaying", i / 10.0); }
        Assert.Equal(2, states.Count);
        Assert.Equal(2002, updates);
        Assert.Contains("Applied commit 999", task.Snapshot().Log);
        task.Cancel();
        Assert.True(states[^1]!.Stopping);
        Assert.NotNull(queue.Blocking("root"));
        task.Finish(TaskState.Cancelled, "Stopped");
        Assert.Equal(TaskState.Cancelled, states[^1]!.State);
        Assert.Null(queue.Blocking("root"));
        queue.ClearFinished();
        Assert.Null(states[^1]);
        Assert.Equal(5, states.Count);
    }

    [Fact]
    public void Repeated_collision_checks_do_not_allocate_history_snapshots()
    {
        var queue = new TaskQueue();
        for (var i = 0; i < 99; i++) queue.TryStart("finished", "other")!.Finish(TaskState.Succeeded, "Done");
        var active = queue.TryStart("active", "ROOT/")!;
        Assert.Same(active.Snapshot(), queue.Blocking("root\\"));
        for (var i = 0; i < 100; i++) queue.Blocking("root\\");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) queue.Blocking("root\\");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"1,000 collision checks with 100 retained tasks allocated {allocated} bytes.");
        Assert.InRange(allocated, 0, 1024);
        active.Cancel();
        Assert.NotNull(queue.Blocking("root"));
        active.Finish(TaskState.Cancelled, "Stopped");
        Assert.Null(queue.Blocking("root"));
    }

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
        Assert.Equal(new TaskFollowUp(TaskTargetKind.Backup, Worktree: "selected"), TaskResults.FollowUp(new BackupResult { Worktree = "selected" }));
        Assert.Null(TaskResults.FollowUp("unstructured output mentioning a path"));
        var queue = new TaskQueue();
        var task = queue.TryStart("import", "root")!;
        task.Finish(TaskState.NeedsAttention, "paused", replay);
        task.Progress("late callback", 100);
        task.Finish(TaskState.Succeeded, "late completion", new(TaskTargetKind.Folder, "wrong"));
        Assert.Equal(replay, Assert.Single(queue.Snapshot()).FollowUp);
    }

    [Fact]
    public void Restore_receipt_preserves_recovery_and_shelved_edits_guidance_after_navigation()
    {
        var result = new RestoreResult
        {
            Branch = "feature", Path = "worktree", Applied = 2, Commits = 2,
            Replaced = true, RecoveryBranch = "feature-before-restore", RecoveryPath = "original-worktree",
            WipShelf = "saved-edits", WipWhy = "Local edits differ from the backup."
        };
        var outcome = TaskResults.Describe(result);
        Assert.Equal(TaskState.NeedsAttention, outcome.State);
        Assert.Contains("feature-before-restore", outcome.Detail);
        Assert.Contains("original-worktree", outcome.Detail);
        Assert.Contains("saved-edits", outcome.Detail);
        Assert.Contains("Open Shelved changes", outcome.Detail);
        Assert.Contains(result.WipWhy, outcome.Detail);
        Assert.Equal(outcome, TaskResults.Describe(new ResolveResult { Backup = result }));
        result.WipWhy = null;
        Assert.Equal(TaskState.NeedsAttention, TaskResults.Describe(result).State);
        result.WipWritten = true;
        var completed = TaskResults.Describe(result);
        Assert.Equal(TaskState.Succeeded, completed.State);
        Assert.DoesNotContain("Open Shelved changes", completed.Detail);
        result.WipConflicted.Add("file.txt");
        var conflicted = TaskResults.Describe(result);
        Assert.Equal(TaskState.NeedsAttention, conflicted.State);
        Assert.Contains("conflict markers", conflicted.Detail);
    }

    [Fact]
    public void Restore_only_offers_replay_guidance_when_commits_remain_queued()
    {
        var result = new RestoreResult { Branch = "feature", Stopped = "subject", Why = "Patch rejected." };
        var failed = TaskResults.Describe(result);
        Assert.Equal(TaskState.NeedsAttention, failed.State);
        Assert.Contains("could not be applied", failed.Detail);
        Assert.DoesNotContain("queued", failed.Detail);
        result.Waiting = true;
        Assert.Contains("remaining commits are queued", TaskResults.Describe(result).Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_and_restore_agree_on_replay_guidance_and_keep_the_original_destination(bool waiting)
    {
        var imported = new ImportResult { Branch = "original", Checkout = "trunk", Path = "original-path",
            Applied = 1, Commits = 3, Stopped = "patch", Why = "Cannot apply patch.", Waiting = waiting };
        var restored = new RestoreResult { Stopped = imported.Stopped, Why = imported.Why, Waiting = waiting };
        var importOutcome = TaskResults.Describe(imported);
        var restoreOutcome = TaskResults.Describe(restored);
        Assert.Equal(TaskState.NeedsAttention, importOutcome.State);
        Assert.Contains("1/3 commits imported", importOutcome.Detail);
        Assert.Contains(imported.Path, importOutcome.Detail);
        Assert.Equal(restoreOutcome.Detail.Split('\n').Skip(1), importOutcome.Detail.Split('\n').Skip(1));
        Assert.Equal(waiting, importOutcome.Detail.Contains("remaining commits are queued"));
        Assert.Equal(new TaskFollowUp(waiting ? TaskTargetKind.Replay : TaskTargetKind.Folder, imported.Path), TaskResults.FollowUp(imported));
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
