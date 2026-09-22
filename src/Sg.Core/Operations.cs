using System.Text.Json;

namespace Sg.Core;

public enum OperationPhase { Planned, SavingBranch, SavingCheckout, Syncing, Replaying, RestoringCheckout, RestoringBranch, NeedsReview, Completed, Cancelled }

public sealed class OperationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Update from SVN";
    public string Branch { get; set; } = "";
    public string Checkout { get; set; } = "";
    public string Path { get; set; } = "";
    public string Before { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string? BranchShelf { get; set; }
    public string? CheckoutShelf { get; set; }
    public OperationPhase Phase { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Steps { get; set; } = new();
    public List<string> ReviewShelves { get; set; } = new();
    public List<string> RestoredShelves { get; set; } = new();
    public string? Detail { get; set; }
    public string PhaseLabel => Phase switch
    {
        OperationPhase.Planned => "Ready to start",
        OperationPhase.SavingBranch => "Saving branch edits",
        OperationPhase.SavingCheckout => "Saving checkout edits",
        OperationPhase.Syncing => "Syncing SVN",
        OperationPhase.Replaying => "Replaying commits",
        OperationPhase.RestoringCheckout => "Recovering checkout edits",
        OperationPhase.RestoringBranch => "Recovering branch edits",
        OperationPhase.NeedsReview => "Needs review",
        OperationPhase.Completed => "Complete",
        _ => "Closed; preserved work retained"
    };
    public bool Terminal => Phase is OperationPhase.Completed or OperationPhase.Cancelled;
    public string Checkpoint => "refs/sg/operations/" + Id;
    public string Action => Phase == OperationPhase.NeedsReview ? "Review saved edits" : Terminal ? "View result" : "Resume";
}

public sealed class UpdateRevision
{
    public string WorkingCopy { get; set; } = "";
    public long From { get; set; }
    public long To { get; set; }
}

public sealed class BranchUpdatePlan
{
    public string Path { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Checkout { get; set; } = "";
    public string Head { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string BranchVersion { get; set; } = "";
    public string CheckoutVersion { get; set; } = "";
    public List<UpdateRevision> Revisions { get; set; } = new();
    public int Commits { get; set; }
    public List<string> BranchEdits { get; set; } = new();
    public List<string> CheckoutEdits { get; set; } = new();
    public List<string> Blockers { get; set; } = new();
    public bool Ready => Blockers.Count == 0;
}

/// <summary>Durable branch updates and receipts. Resumption reconciles external state before another mutation.</summary>
public static class Operations
{
    static string Folder(SgRoot root) => System.IO.Path.Combine(root.StorePath, "operations");
    static string FileFor(SgRoot root, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new SgException("invalid operation id");
        return System.IO.Path.Combine(Folder(root), id + ".json");
    }
    public static OperationRecord Read(SgRoot root, string id) => JsonSerializer.Deserialize<OperationRecord>(File.ReadAllText(FileFor(root, id)), SgConfig.JsonOptions)
        ?? throw new SgException("invalid operation record: " + id);
    public static List<OperationRecord> List(SgRoot root) => !Directory.Exists(Folder(root)) ? [] : Directory.EnumerateFiles(Folder(root), "*.json")
        .Select(p => Read(root, System.IO.Path.GetFileNameWithoutExtension(p))).OrderByDescending(x => x.Updated).ToList();
    public static OperationRecord? Pending(SgRoot root, string path) => List(root).FirstOrDefault(x => !x.Terminal && !string.IsNullOrEmpty(x.Path) && System.IO.Path.GetFullPath(x.Path).Equals(System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    public static bool ProtectsShelf(SgRoot root, string id) => List(root).Any(x => !x.Terminal && (x.BranchShelf == id || x.CheckoutShelf == id || x.ReviewShelves.Contains(id)));
    static void Save(SgRoot root, OperationRecord record)
    {
        record.Updated = DateTimeOffset.UtcNow;
        AtomicFile.WriteAllText(FileFor(root, record.Id), JsonSerializer.Serialize(record, SgConfig.JsonOptions));
    }
    static void Move(SgRoot root, OperationRecord record, OperationPhase phase, string? completed = null)
    {
        if (completed != null) { record.Steps.Add(completed); root.Log.Info(completed); }
        record.Phase = phase;
        record.Detail = null;
        Save(root, record);
    }

    public static BranchUpdatePlan Plan(SgRoot root, string path, bool checkRemote = true)
    {
        using var operation = root.Lock();
        var git = root.Git;
        path = git.Toplevel(path);
        var branch = git.BranchOrRebaseHead(path);
        var co = Ops.BaseCheckout(root, branch);
        var plan = new BranchUpdatePlan { Path = path, Branch = branch, Checkout = co.Name, Head = git.HeadSha(path), Snapshot = git.RefSha(root.SnapshotRef(co)) ?? "" };
        if (Pending(root, path) != null || Conflicts.HasPending(git, path)) plan.Blockers.Add("Resume the existing operation first.");
        if (List(root).Any(x => !x.Terminal && x.Checkout == co.Name)) plan.Blockers.Add("Another update owns this checkout. Finish it in Activity first.");
        var branchEdits = git.StatusEntries(path, untracked: true);
        var checkoutEdits = Ops.CheckoutChanges(root, co);
        plan.BranchEdits = branchEdits.Select(x => x.Path).ToList();
        plan.CheckoutEdits = checkoutEdits.Select(x => x.Path).ToList();
        foreach (var e in branchEdits)
            if (PathUtil.IsReparsePoint(PathUtil.Join(path, e.Path))) plan.Blockers.Add(e.Path + ": linked content cannot be put on a recovery shelf.");
            else if (e.X == "U" || e.Y == "U" || (e.Staged && e.HasUnstaged))
                plan.Blockers.Add(e.Path + ": resolve conflicts or finish partial staging before updating; a shelf cannot preserve that index split.");
        foreach (var e in checkoutEdits)
            if (Directory.Exists(PathUtil.Join(co.Path, e.Path)) || PathUtil.IsReparsePoint(PathUtil.Join(co.Path, e.Path)) || e.Props is "modified" or "conflicted" || e.Item is "normal" or "conflicted" or "obstructed" || co.Skip.Any(p => PathUtil.IsUnder(e.Path, PathUtil.Rel(p))) || PathUtil.HasReservedName(e.Path))
                plan.Blockers.Add(e.Path + ": this checkout edit cannot be safely shelved. Finish it first.");
        if (plan.Snapshot.Length == 0) plan.Blockers.Add("The checkout has no local snapshot yet.");
        else plan.Commits = git.CountCommits(plan.Snapshot, plan.Head);
        if (plan.Ready && checkRemote)
            plan.Revisions = Ops.RemoteCheck(root, co).Entries.Select(x => new UpdateRevision { WorkingCopy = x.Rel.Length == 0 ? co.Name : x.Rel, From = x.Snapshot, To = x.Server }).ToList();
        if (plan.Ready)
        {
            plan.BranchVersion = WorkspaceVersion.Of(root, path);
            plan.CheckoutVersion = WorkspaceVersion.Of(root, co.Path, checkout: true);
        }
        return plan;
    }

    public static OperationRecord Run(SgRoot root, BranchUpdatePlan plan, Func<bool>? stopAfterStep = null)
    {
        using var operation = root.Lock();
        var current = Plan(root, plan.Path, checkRemote: false);
        if (!current.Ready) throw new SgException(string.Join("\n", current.Blockers));
        if (current.Branch != plan.Branch || current.Checkout != plan.Checkout || current.BranchVersion != plan.BranchVersion || current.CheckoutVersion != plan.CheckoutVersion || current.Snapshot != plan.Snapshot)
            throw new SgException("The branch or checkout changed. Refresh the plan before updating.");
        var record = new OperationRecord { Path = plan.Path, Branch = plan.Branch, Checkout = plan.Checkout, Before = plan.Head, Snapshot = plan.Snapshot };
        root.Git.UpdateRef(record.Checkpoint, record.Before);
        Save(root, record);
        return Resume(root, record.Id, stopAfterStep);
    }

    public static OperationRecord Resume(SgRoot root, string id, Func<bool>? stopAfterStep = null)
    {
        using var operation = root.Lock();
        var record = Read(root, id);
        if (record.Terminal || record.Phase == OperationPhase.NeedsReview) return record;
        var git = root.Git;
        if (record.Kind != "Update from SVN")
        {
            if (Conflicts.HasPending(git, record.Path)) { record.Detail = "Open the replay to review, continue, or skip its current step."; Save(root, record); return record; }
            return Review(root, record, "The replay is no longer active. Review the current branch before closing this record; no command was repeated.");
        }
        var co = root.Checkout(record.Checkout);
        try
        {
            // Cancellation is observed between durable steps. Children finish their current step.
            var cancellation = Cancellation.Current;
            while (!record.Terminal && record.Phase != OperationPhase.NeedsReview)
            {
                if (cancellation.IsCancellationRequested || stopAfterStep?.Invoke() == true) { record.Detail = "Stopped after a completed step. Resume when ready."; Save(root, record); return record; }
                using var finishStep = Cancellation.Use(CancellationToken.None);
                switch (record.Phase)
                {
                    case OperationPhase.Planned:
                        Move(root, record, OperationPhase.SavingBranch); break;
                    case OperationPhase.SavingBranch:
                        if (!SaveEdits(root, record, record.Path, false)) return record;
                        Move(root, record, OperationPhase.SavingCheckout, "Branch edits saved; ignored files left untouched."); break;
                    case OperationPhase.SavingCheckout:
                        if (!SaveEdits(root, record, co.Path, true)) return record;
                        Move(root, record, OperationPhase.Syncing, "Checkout edits saved."); break;
                    case OperationPhase.Syncing:
                        if (root.Svn.Status(co.Path, noIgnore: false).Any(x => x.Item is "conflicted" or "obstructed"))
                            return Review(root, record, "Resolve the checkout's SVN conflicts before resuming.");
                        if (Ops.CheckoutChanges(root, co).Count > 0) return Review(root, record, "Checkout edits appeared after saving. Review them before starting another update.");
                        var sync = Ops.Sync(root, co);
                        record.Snapshot = sync.Sha;
                        if (sync.Conflicts > 0) return Review(root, record, "SVN sync left checkout conflicts. Saved edits remain on shelves.");
                        Move(root, record, OperationPhase.Replaying, "SVN synced: " + sync.Sha); break;
                    case OperationPhase.Replaying:
                        if (git.ReplayInProgress(record.Path) != Replay.None) { record.Detail = "Replay is paused. Review conflicts or the empty step."; Save(root, record); return record; }
                        var head = git.HeadSha(record.Path);
                        if (!git.IsAncestor(record.Snapshot, head))
                        {
                            if (head != record.Before) return Review(root, record, "The branch changed outside this operation. Review the checkpoint before continuing.");
                            if (git.RefSha(root.SnapshotRef(co)) != record.Snapshot) return Review(root, record, "The checkout snapshot changed while this update was paused. Close this operation after recovering its shelves, then plan another update.");
                            var replay = Ops.Rebase(root, record.Path, refreshShared: false);
                            if (!replay.Ok) { record.Detail = "Replay is paused. Open Resolve conflicts."; Save(root, record); return record; }
                        }
                        Move(root, record, OperationPhase.RestoringCheckout, "Branch commits replayed onto the recorded snapshot."); break;
                    case OperationPhase.RestoringCheckout:
                        RestoreEdits(root, record, record.CheckoutShelf);
                        if (record.Phase == OperationPhase.NeedsReview) return record;
                        Move(root, record, OperationPhase.RestoringBranch, "Checkout edits recovered."); break;
                    case OperationPhase.RestoringBranch:
                        RestoreEdits(root, record, record.BranchShelf);
                        if (record.Phase == OperationPhase.NeedsReview) return record;
                        Move(root, record, OperationPhase.Completed, "Branch edits recovered. Original commits retained as a checkpoint."); break;
                }
            }
        }
        catch (Exception ex)
        {
            record.Detail = ex.Message;
            Save(root, record);
            throw;
        }
        return record;
    }

    static bool SaveEdits(SgRoot root, OperationRecord record, string path, bool checkout)
    {
        var shelf = checkout ? record.CheckoutShelf : record.BranchShelf;
        var dirty = checkout ? Ops.CheckoutChanges(root, root.Checkout(record.Checkout)).Count > 0 : root.Git.StatusEntries(path, untracked: true).Count > 0;
        if (!dirty) return true;
        if (shelf != null) { Review(root, record, "Saving edits was interrupted. Compare the saved shelf with the current files before starting another update."); return false; }
        Shelf.Save(root, path, null, "update " + record.Id, saved =>
        {
            if (checkout) record.CheckoutShelf = saved.Id; else record.BranchShelf = saved.Id;
            Save(root, record); // Must precede the shelf's file cleanup.
        });
        var remaining = checkout ? Ops.CheckoutChanges(root, root.Checkout(record.Checkout)).Count : root.Git.StatusEntries(path, untracked: true).Count;
        if (remaining > 0) { Review(root, record, "Some edits could not be put aside. Saved shelves and remaining files are retained."); return false; }
        return true;
    }

    static void RestoreEdits(SgRoot root, OperationRecord record, string? id)
    {
        if (id == null || record.RestoredShelves.Contains(id)) return;
        if (record.ReviewShelves.Contains(id)) { Review(root, record, "Restoring edits was interrupted. Review the saved shelf; current files have been left intact."); return; }
        record.ReviewShelves.Add(id);
        Save(root, record); // A crash after this point must never automatically apply a second time.
        var result = Shelf.Restore(root, id, keep: true);
        if (result.Conflicted.Count > 0) { Review(root, record, "Saved edits need review: " + string.Join(", ", result.Conflicted)); return; }
        record.RestoredShelves.Add(id);
        record.ReviewShelves.Remove(id);
        Save(root, record);
    }

    static OperationRecord Review(SgRoot root, OperationRecord record, string detail)
    {
        record.Phase = OperationPhase.NeedsReview; record.Detail = detail; Save(root, record); return record;
    }

    public static OperationRecord FinishReview(SgRoot root, string id)
    {
        using var operation = root.Lock();
        var record = Read(root, id);
        if (Conflicts.HasPending(root.Git, record.Path)) throw new SgException("Finish or cancel the replay first.");
        // Explicit acknowledgement keeps every shelf, including ones not restored yet.
        Move(root, record, OperationPhase.Cancelled, "Closed by the user; current files and all saved shelves retained.");
        return record;
    }

    public static OperationRecord? AfterReplay(SgRoot root, string path, bool aborted = false)
    {
        var record = Pending(root, path);
        if (record == null || record.Phase != OperationPhase.Replaying) return null;
        if (record.Kind != "Update from SVN")
        {
            Move(root, record, aborted ? OperationPhase.Cancelled : OperationPhase.Completed, aborted ? "Replay cancelled. Current files and existing recovery shelves retained." : "Replay finished. Inspect saved edits in Shelves if recovery required review.");
            return record;
        }
        if (aborted) return Review(root, record, "Replay cancelled. SVN remains synced; recover saved edits from the shelves, then close this operation.");
        return Resume(root, record.Id);
    }

    public static string RestoreCheckpoint(SgRoot root, string id, string? branchName = null)
    {
        using var operation = root.Lock();
        var record = Read(root, id);
        if (root.Git.RefSha(record.Checkpoint) == null) throw new SgException("This operation has no branch checkpoint.");
        var name = branchName ?? record.Branch + "-recovered-" + Guid.NewGuid().ToString("N")[..8];
        var made = Ops.Branch(root, name, root.Checkout(record.Checkout));
        root.Git.ResetHard(made.Path, record.Checkpoint);
        return made.Path;
    }

    public static OperationRecord TrackReplay(SgRoot root, string kind, string path, string detail)
    {
        var existing = Pending(root, path);
        if (existing != null) return existing;
        var branch = root.Git.BranchOrRebaseHead(path);
        var co = Ops.BaseCheckout(root, branch);
        var record = new OperationRecord { Kind = kind, Path = path, Branch = branch, Checkout = co.Name,
            Before = root.Git.HeadSha(path), Snapshot = root.Git.RefSha(root.SnapshotRef(co))!, Phase = OperationPhase.Replaying, Detail = detail };
        root.Git.UpdateRef(record.Checkpoint, record.Before);
        Save(root, record);
        return record;
    }

    public static OperationRecord ArchiveCheckpoint(SgRoot root, string branch, string path, string checkout, string head)
    {
        var record = new OperationRecord { Kind = "Archive checkpoint", Branch = branch, Path = path, Checkout = checkout, Before = head, Phase = OperationPhase.Completed,
            Steps = ["Branch commits preserved. Worktree removal may still be pending; this checkpoint does not include ignored files or local edits."] };
        root.Git.UpdateRef(record.Checkpoint, head);
        Save(root, record);
        return record;
    }

    public static void Receipt(SgRoot root, string kind, string path, IEnumerable<string> effects, bool required = false)
    {
        try
        {
            using var operation = root.Lock();
            var record = new OperationRecord { Kind = kind, Path = path, Phase = OperationPhase.Completed, Steps = effects.ToList() };
            if (Directory.Exists(path))
            {
                record.Branch = root.Git.BranchOrRebaseHead(path);
                try { record.Checkout = Ops.BaseCheckout(root, record.Branch).Name; } catch (SgException) { }
            }
            Save(root, record);
        }
        catch (Exception ex) when (!required && ex is IOException or UnauthorizedAccessException or SgException)
        {
            // A completed SVN publication must never be rolled back or retried because its UI receipt could not be written.
            root.Log.Warn("Operation completed, but its activity receipt could not be saved: " + ex.Message);
        }
    }
}
