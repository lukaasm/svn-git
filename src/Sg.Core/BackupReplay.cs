using System.Text.Json;

namespace Sg.Core;

public static partial class Backup
{
    // Stored beside Git's replay state, so restarting the app does not lose the source or local edits.
    sealed class PendingReplay
    {
        public PendingReplay() { }
        public string Name { get; set; } = "";
        public string Branch { get; set; } = "";
        public string Checkout { get; set; } = "";
        public string? Wip { get; set; }
        public bool Pull { get; set; }
        public bool RestoringEdits { get; set; }
        public string Token { get; set; } = "";
        public string Start { get; set; } = "";
        public int Commits { get; set; }
    }

    static PendingReplay? Pending(Git git, string path)
    {
        var file = git.PrivateFile(path, "backup-replay.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<PendingReplay>(File.ReadAllText(file)) : null;
    }

    public static string? ReplayName(Git git, string path) => Pending(git, path)?.Name;

    internal static void ClearReplay(Git git, string path)
    {
        if (Pending(git, path) is { } pending && Guid.TryParseExact(pending.Token, "N", out _))
        {
            git.DeleteRef("refs/sg/replay/" + pending.Token + "/branch");
            git.DeleteRef("refs/sg/replay/" + pending.Token + "/wip");
        }
        File.Delete(git.PrivateFile(path, "backup-replay.json"));
    }

    /// <summary>On a conflict, replay the whole incoming series through Git's persistent mailbox queue.</summary>
    static void BeginReplay(SgRoot root, RestoreResult result, List<ThinCommit> changes, string? wip, bool pull)
    {
        var git = root.Git;
        if (git.StatusEntries(result.Path, untracked: true).Count != 0)
            throw new SgException("The incoming backup needs conflict resolution. Commit or shelve local changes, then get changes from backup again. Nothing was applied.");
        var temp = root.NewStoreTempDir();
        var start = git.HeadSha(result.Path);
        try
        {
            var patches = new List<string>();
            foreach (var c in changes)
            {
                var parent = git.ParentOf(c.Sha) ?? throw new SgException("backup commit has no base: " + c.Sha);
                var tree = git.TreeOf(c.Sha);
                var original = git.CommitTreeAs(tree, parent, Thin.Original(c.Body), c.Author);
                patches.AddRange(git.FormatPatch(result.Path, parent + ".." + original, Path.Combine(temp, patches.Count.ToString("D6"))));
            }
            var pending = new PendingReplay
            {
                Name = result.Name, Branch = result.Branch, Checkout = result.Checkout, Wip = wip, Pull = pull,
                Token = Guid.NewGuid().ToString("N"), Start = start, Commits = changes.Count,
            };
            AtomicFile.WriteAllText(git.PrivateFile(result.Path, "backup-replay.json"), JsonSerializer.Serialize(pending));
            // Keep the original objects even if a background fetch replaces the fetched backup refs.
            git.UpdateRef("refs/sg/replay/" + pending.Token + "/branch", changes[^1].Sha);
            if (wip != null) git.UpdateRef("refs/sg/replay/" + pending.Token + "/wip", wip);
            var run = git.ApplyMailbox(result.Path, patches);
            result.Applied = git.CountCommits(start, git.HeadSha(result.Path));
            result.Waiting = git.ReplayInProgress(result.Path) != Replay.None;
            if (result.Waiting)
            {
                result.Stopped = git.Progress(result.Path).Subject;
                result.Conflicted = git.ConflictedFiles(result.Path);
                result.Why = (run.StdOut + run.StdErr).Trim();
                return;
            }
            run.EnsureOk();
            result.Stopped = null;
            result.Conflicted.Clear();
            ClearReplay(git, result.Path);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
            if (git.ReplayInProgress(result.Path) == Replay.None) ClearReplay(git, result.Path);
        }
    }

    /// <summary>Restore edits only after the queued commits finish; a conflict keeps them on a shelf.</summary>
    internal static RestoreResult? FinishReplay(SgRoot root, string path)
    {
        var git = root.Git;
        var pending = Pending(git, path);
        if (pending == null) return null;
        var result = new RestoreResult
        {
            Name = pending.Name, Branch = pending.Branch, Checkout = pending.Checkout, Path = path,
            Commits = pending.Commits, Applied = git.CountCommits(pending.Start, git.HeadSha(path)),
        };
        if (pending.Wip != null)
        {
            var co = root.Checkout(pending.Checkout);
            var interrupted = pending.RestoringEdits;
            pending.RestoringEdits = true;
            AtomicFile.WriteAllText(git.PrivateFile(path, "backup-replay.json"), JsonSerializer.Serialize(pending));
            // An interrupted write may have applied some or all edits. Never apply them twice.
            // Present a shelf for comparison instead, while preserving the current working files.
            if (interrupted)
            {
                Adopt(root, result, pending.Wip, git.HeadSha(path), path, co, pending.Branch, write: false);
                result.WipWhy = "Recovery was interrupted while restoring edits. Review the saved edits against the working files.";
            }
            else if (pending.Pull)
                PullChanges(root, root.Config.Backup ?? new BackupConfig(), result, pending.Wip, path, co, pending.Branch, PushedRef("wip", pending.Name));
            else Adopt(root, result, pending.Wip, git.HeadSha(path), path, co, pending.Branch, write: true);
        }
        if (pending.Branch == pending.Name) git.ConfigUnset(KeyRemote(pending.Name));
        ClearReplay(git, path);
        return result;
    }
}
