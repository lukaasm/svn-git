namespace Sg.Core.Tests;

public class RecoveryTests
{
    [Fact]
    public void Current_replay_state_overrides_record_and_deduplicates_paths()
    {
        var record = new OperationRecord { Path = @"D:\root\feature", Phase = OperationPhase.Syncing, Branch = "feature" };
        var tree = new WorktreeStatus { Path = "d:/root/feature/", Stopped = Replay.Import, Branch = "feature" };
        var item = Assert.Single(Recovery.Find([record], [tree]));
        Assert.True(item.Replay);
        Assert.Equal("Review replay", item.Action);
    }

    [Fact]
    public void Finished_records_do_not_hide_untracked_empty_replay_or_backup_finalization()
    {
        var records = new[] { new OperationRecord { Path = "feature", Phase = OperationPhase.Completed } };
        var items = Recovery.Find(records, [new() { Path = "feature", Stopped = Replay.Rebase }, new() { Path = "backup", BackupFinalizing = true }]);
        Assert.Equal(2, items.Count);
        Assert.Contains("skip an empty commit", items[0].Detail);
        Assert.Contains("backup finalization", items[1].Detail);
        Assert.All(items, item => Assert.True(item.Replay));
    }

    [Fact]
    public void Saved_edits_and_missing_worktrees_offer_review_instead_of_blind_retry()
    {
        var items = Recovery.Find([
            new() { Path = "review", Phase = OperationPhase.NeedsReview },
            new() { Path = "missing", Phase = OperationPhase.SavingBranch }
        ], [new() { Path = "missing", Missing = true }]);
        Assert.Equal("Review saved edits", items.Single(i => i.Path == "review").Action);
        Assert.Equal("View recovery checkpoint", items.Single(i => i.Path == "missing").Action);
        Assert.All(items, item => Assert.False(item.Replay));
        Assert.Empty(Recovery.Find([new() { Phase = OperationPhase.Cancelled }], []));
    }
}
