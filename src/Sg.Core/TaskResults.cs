namespace Sg.Core;

/// <summary>A returned value may describe a stopped replay or a partial push, rather than success.</summary>
public static class TaskResults
{
    public static (TaskState State, string Detail) Describe(object? result) => result switch
    {
        ImportResult r => Import(r),
        RestoreResult r => Restore(r),
        RebaseResult r => (r.Ok && !r.Conflict ? TaskState.Succeeded : TaskState.NeedsAttention, r.Branch + (r.Ok ? ": replay completed." : ": replay paused. Open Resolve conflicts.") + Note(r.Output)),
        ResolveResult r => r.Backup != null ? Describe(r.Backup) : r.Operation != null ? Describe(r.Operation)
            : (r.Ok && !r.Conflict ? TaskState.Succeeded : TaskState.NeedsAttention, r.Branch + (r.Ok ? ": replay completed." : ": replay paused. Open Resolve conflicts.") + Note(r.Output)),
        OperationRecord r => (r.Phase == OperationPhase.Completed ? TaskState.Succeeded : r.Phase == OperationPhase.Cancelled ? TaskState.Cancelled : TaskState.NeedsAttention,
            r.Branch + ": " + r.PhaseLabel + Note(r.Detail) + (r.Terminal ? "" : "\nOpen Activity to resume or review saved edits.")),
        BackupResult r => (r.Error != null ? TaskState.Failed : !r.Ok || r.Behind > 0 ? TaskState.NeedsAttention : TaskState.Succeeded,
            (r.Worktree == null ? "" : r.Worktree + ": ") + $"{r.Pushed} sent, {r.Rejected} rejected, {r.Behind} newer on backup, {r.Items.Count(i => i.Failed)} failed." + Note(r.Error)
            + string.Concat(r.Items.Where(i => i.Failed || i.Rejected || i.Behind).Select(i => Note(i.Name + ": " + i.Why)))),
        PushResult r => (r.AllCommitted && r.Warnings.Count == 0 || r.AppliedOnly ? TaskState.Succeeded : TaskState.NeedsAttention,
            r.AppliedOnly ? "Changes applied to the checkout; nothing committed to SVN."
            : (r.AllCommitted ? $"{r.Branch}: committed to SVN at r{r.Revision}." : $"{r.Branch}: push stopped before every commit was sent. Review Push before continuing.") + Note(r.BranchState) + Note(string.Join("\n", r.Warnings))),
        SyncResult r => (r.Conflicts + r.Warnings.Count > 0 ? TaskState.NeedsAttention : TaskState.Succeeded,
            $"{r.Checkout}: r{r.Revision}, {r.Conflicts} conflicts." + Note(string.Join("\n", r.Warnings))),
        BranchResult r => (TaskState.Succeeded, $"Worktree {r.Branch} is ready.\n{r.Path}"),
        CheckoutTransferResult r => (TaskState.Succeeded, $"{r.Files} file(s) {(r.Moved ? "moved" : "copied")} to {r.Path}. Recovery shelves retained."),
        WorktreeRenamePlan r => (TaskState.Succeeded, $"Renamed {r.Branch} to {r.Name}.\n{r.NewPath}"),
        ReviewRecord r => (r.Checks.Any(c => c.ExitCode != 0) ? TaskState.NeedsAttention : TaskState.Succeeded,
            string.Join("\n", r.Checks.Select(c => $"{c.Name}: {(c.ExitCode == 0 ? "passed" : "failed (exit " + c.ExitCode + ")")}"))),
        AutoResolveResult r => (r.AllResolved ? TaskState.Succeeded : TaskState.NeedsAttention,
            $"{r.Resolved.Count} files resolved, {r.Left.Count} still need attention." + Note(r.Output)),
        AutoResolveRun r => r.Finished != null ? Describe(r.Finished) : (r.Ok ? TaskState.Succeeded : TaskState.NeedsAttention,
            $"{r.Branch}: {r.FilesResolved} files resolved." + Note(r.Why)),
        Ops.SvnCommitResult r => (r.AllCommitted && (r.Sync == null || r.Sync.Conflicts + r.Sync.Warnings.Count == 0) ? TaskState.Succeeded : TaskState.NeedsAttention,
            r.AllCommitted ? "Changes committed to SVN." + (r.Sync == null ? "" : Note(Describe(r.Sync).Detail)) : "Some changes were not committed. Review the SVN commit result before continuing."),
        HandoffReceipt r => (r.RestorePath != null && r.RestoreTested == null || r.Coverage.Any(c => c.State != "Remote refs checked") ? TaskState.NeedsAttention : TaskState.Succeeded,
            r.Branch + ": " + (r.RestorePath != null ? r.RestoreTested != null ? "restore tested." : "restore needs review; verification not granted." : "coverage checked.")
            + Note(r.RestorePath) + Note(string.Join("\n", r.Coverage.Select(c => c.Kind + "/" + c.Name + ": " + c.State)))),
        CheckoutResult r => (r.Snapshot.Warnings.Count > 0 ? TaskState.NeedsAttention : TaskState.Succeeded,
            $"Checkout {r.Checkout.Name} is ready at r{r.Snapshot.Revision}.\n{r.Checkout.Path}" + Note(string.Join("\n", r.Snapshot.Warnings))),
        ShelfSaveResult r => (r.LeftBehind.Count == 0 ? TaskState.Succeeded : TaskState.NeedsAttention,
            "Changes saved on shelf " + r.Shelf.Id + (r.LeftBehind.Count == 0 ? "." : ". Files left behind:\n" + string.Join("\n", r.LeftBehind))),
        ShelfRestoreResult r => (r.Conflicted.Count == 0 ? TaskState.Succeeded : TaskState.NeedsAttention,
            $"{r.Written.Count} files restored, {r.Conflicted.Count} conflicts."),
        MergeResult r => (r.Clean ? TaskState.Succeeded : TaskState.NeedsAttention, $"{r.Changed.Count} files changed, {r.Conflicts.Count} conflicts." + Note(r.Failure)),
        string s when s != "ok" => (TaskState.Succeeded, s),
        _ => (TaskState.Succeeded, "Completed. See task output for details.")
    };
    public static TaskFollowUp? FollowUp(object? result) => result switch
    {
        ImportResult r => new(r.Waiting ? TaskTargetKind.Replay : TaskTargetKind.Folder, r.Path),
        RestoreResult r => new(r.Waiting ? TaskTargetKind.Replay : TaskTargetKind.Folder, r.Path),
        BranchResult r => new(TaskTargetKind.Folder, r.Path),
        CheckoutTransferResult r => new(TaskTargetKind.Folder, r.Path),
        WorktreeRenamePlan r => new(TaskTargetKind.Folder, r.NewPath),
        CheckoutResult r => new(TaskTargetKind.Folder, r.Checkout.Path),
        OperationRecord r => new(r.Terminal ? TaskTargetKind.Folder : TaskTargetKind.Update, r.Path),
        BackupResult r => new(TaskTargetKind.Backup, Worktree: r.Worktree),
        ResolveResult r when r.Backup != null => FollowUp(r.Backup),
        ResolveResult r when r.Operation != null => FollowUp(r.Operation),
        ResolveResult r when !r.Ok => new(TaskTargetKind.Activity),
        RebaseResult r when !r.Ok => new(TaskTargetKind.Activity),
        _ => null
    };

    static (TaskState State, string Detail) Import(ImportResult r)
    {
        var lines = new List<string> { $"{r.Branch}: {r.Applied}/{r.Commits} commits imported. {r.Path}" };
        lines.AddRange(ReplayGuidance(r.Waiting, r.Stopped, r.Why));
        if (r.Drift.Count > 0) lines.Add($"Commits were merged across {r.Drift.Count} changed revision(s).");
        if (r.CheckoutAppearanceRestored) lines.Add("Checkout appearance restored.");
        if (r.CheckoutAppearanceWarning != null) lines.Add(r.CheckoutAppearanceWarning);
        return (r.Ok && !r.Waiting && r.CheckoutAppearanceWarning == null ? TaskState.Succeeded : TaskState.NeedsAttention, string.Join("\n", lines));
    }

    static IEnumerable<string> ReplayGuidance(bool waiting, string? stopped, string? why)
    {
        if (waiting) yield return $"Replay paused on \"{stopped}\". The remaining commits are queued; resume to resolve, skip, or cancel.";
        else if (stopped != null) yield return $"\"{stopped}\" could not be applied.";
        if (!string.IsNullOrWhiteSpace(why)) yield return why;
    }

    static (TaskState State, string Detail) Restore(RestoreResult r)
    {
        var savedEdits = r.WipShelf != null && !r.WipWritten;
        var complete = r.Ok && !r.Waiting && r.WipWhy == null && r.WipConflicted.Count == 0 && !savedEdits && r.CheckoutAppearanceWarning == null;
        var lines = new List<string> { $"{r.Branch}: {r.Applied}/{r.Commits} commits recovered. {r.Path}" };
        lines.AddRange(ReplayGuidance(r.Waiting, r.Stopped, r.Why));
        if (r.Relinked) lines.Add("The store still had the commits, so nothing was replayed.");
        if (r.CheckoutAppearanceRestored) lines.Add("Checkout appearance restored.");
        if (r.CheckoutAppearanceWarning != null) lines.Add(r.CheckoutAppearanceWarning);
        if (r.Drift.Count > 0) lines.Add($"Commits were merged across {r.Drift.Count} changed revision(s).");
        if (r.Replaced) lines.Add("Original work is preserved as " + r.RecoveryBranch
            + (r.RecoveryPath == null ? "." : " in " + r.RecoveryPath + "."));
        if (r.WipAlreadyHere) lines.Add("The uncommitted changes in the backup were here already.");
        else if (savedEdits) lines.Add($"Uncommitted changes are saved on shelf {r.WipShelf}. Open Shelved changes to review and restore them."
            + (r.WipConflicted.Count > 0 ? $" {r.WipConflicted.Count} file(s) need conflict resolution." : ""));
        else if (r.WipWritten) lines.Add("Uncommitted changes are written into the worktree."
            + (r.WipConflicted.Count > 0 ? $" {r.WipConflicted.Count} file(s) contain conflict markers." : ""));
        if (r.WipWhy != null) lines.Add(r.WipWhy);
        if (r.Shelves.Count > 0) lines.Add("Shelves recovered: " + string.Join(", ", r.Shelves) + ".");
        return (complete ? TaskState.Succeeded : TaskState.NeedsAttention, string.Join("\n", lines));
    }

    static string Note(string? text) => string.IsNullOrWhiteSpace(text) ? "" : "\n" + text.Trim();
}
