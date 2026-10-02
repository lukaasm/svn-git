namespace Sg.Core;

/// <summary>One thing that needs attention. <see cref="Operations"/> names the records behind it, which Dismiss closes; a paused replay with no record has none.</summary>
public sealed record RecoveryItem(string Title, string Path, string Detail, string Action, bool Replay, IReadOnlyList<string>? Operations = null, bool Gone = false);

/// <summary>Recovery navigation from durable records and current Git state; never replays a command.</summary>
public static class Recovery
{
    /// <param name="gone">Whether a folder no listed worktree has is no worktree at all. A listed worktree with no folder is gone without asking.</param>
    public static IReadOnlyList<RecoveryItem> Find(IEnumerable<OperationRecord> records, IEnumerable<WorktreeStatus> worktrees, Func<string, bool>? gone = null)
    {
        var pending = records.Where(r => !r.Terminal).OrderByDescending(r => r.Updated).ToList();
        var trees = worktrees.ToList();
        var items = new List<RecoveryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string Key(string path) => path.Replace('\\', '/').TrimEnd('/');
        foreach (var record in pending)
        {
            if (!seen.Add(Key(record.Path))) continue;
            var tree = trees.FirstOrDefault(w => Key(w.Path).Equals(Key(record.Path), StringComparison.OrdinalIgnoreCase));
            var replay = tree != null && (tree.Stopped != Sg.Core.Replay.None || tree.BackupFinalizing);
            var action = replay ? "Review replay" : record.Phase == OperationPhase.NeedsReview ? "Review saved edits" : "Review and resume";
            var detail = record.Detail ?? $"Last recorded step: {record.PhaseLabel}. Review the saved state before continuing.";
            var isGone = tree?.Missing == true || tree == null && gone?.Invoke(record.Path) == true;
            if (isGone)
            {
                detail = Sg.Core.Operations.GoneDetail;
                action = "View recovery checkpoint";
                replay = false;
            }
            var ids = pending.Where(r => Key(r.Path).Equals(Key(record.Path), StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToList();
            items.Add(new(record.Kind + " · " + record.Branch, record.Path, detail, action, replay, ids, isGone));
        }
        foreach (var tree in trees.Where(w => w.Stopped != Sg.Core.Replay.None || w.BackupFinalizing))
        {
            if (!seen.Add(Key(tree.Path))) continue;
            items.Add(new("Unfinished replay · " + tree.Branch, tree.Path,
                tree.BackupFinalizing ? "The commits have replayed; backup finalization still needs attention."
                : tree.Conflicts > 0 ? $"{tree.Conflicts} files need resolution before replay can continue."
                : "Replay is paused with no unresolved files. Review the current step to continue or skip an empty commit.",
                "Review replay", true));
        }
        return items;
    }
}
