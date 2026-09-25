using System.Text.Json;

namespace Sg.Core;

public sealed record WorktreeRenamePlan(string Branch, string Name, string Path, string NewPath, string Checkout, string Head)
{
    public string Token => WorkspaceVersion.Hash(JsonSerializer.Serialize(new { Branch, Name, Path, NewPath, Checkout, Head }));
}

/// <summary>A local rename retains worktree identity and edits. Backup copies under the old name remain on the remote.</summary>
public static class WorktreeRename
{
    public static WorktreeRenamePlan Preview(SgRoot root, string worktree, string name)
    {
        var git = root.Git;
        var path = CodeReview.Worktree(root, worktree);
        var branch = git.CurrentBranch(path);
        var co = Ops.BaseCheckout(root, branch);
        name = name.Trim(); git.CheckBranchName(name);
        if (git.RefIndex("refs/heads/").Keys.Any(k => k["refs/heads/".Length..].Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new SgException("That branch name is already in use. Choose a different name.");
        if (Conflicts.HasPending(git, path) || Operations.Pending(root, path) != null)
            throw new SgException("Finish or close the worktree's pending operation before renaming it.");
        if (PathUtil.IsReparsePoint(path)) throw new SgException("A linked worktree folder cannot be renamed here.");
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var newPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent, name.Replace('/', '-')));
        PathUtil.RelativeTo(parent, newPath);
        if (!newPath.Equals(path, StringComparison.OrdinalIgnoreCase) && (Directory.Exists(newPath) || File.Exists(newPath)))
            throw new SgException("The destination folder already exists: " + newPath);
        if (PathUtil.IsReparsePoint(newPath)) throw new SgException("The destination is a linked folder.");
        return new(branch, name, path, newPath, co.Name, git.HeadSha(path));
    }

    public static WorktreeRenamePlan Apply(SgRoot root, WorktreeRenamePlan preview)
    {
        using var gate = root.Lock();
        var plan = Preview(root, preview.Path, preview.Name);
        if (plan != preview) throw new SgException("The worktree changed. Preview the rename again.");
        var git = root.Git;
        // Prepare every metadata update before moving anything, retaining exact originals for rollback.
        var writes = new Dictionary<string, string?>();
        var config = SgConfig.Load(root.ConfigPath);
        if (config.Backup != null)
            config.Backup.Excluded = config.Backup.Excluded.Select(n => n == plan.Branch ? plan.Name : n).Distinct().ToList();
        writes[root.ConfigPath] = JsonSerializer.Serialize(config, SgConfig.JsonOptions) + "\n";
        var reviewPath = Review.FileFor(root, plan.Branch);
        if (File.Exists(reviewPath))
        {
            var review = JsonSerializer.Deserialize<ReviewRecord>(File.ReadAllText(reviewPath), SgConfig.JsonOptions) ?? throw new SgException("Cannot read review state.");
            review.Branch = plan.Name; review.Ready = null; // Checks run in a folder; rerun readiness after changing it.
            writes[Review.FileFor(root, plan.Name)] = JsonSerializer.Serialize(review, SgConfig.JsonOptions);
            writes[reviewPath] = null;
        }
        foreach (var record in Operations.List(root).Where(o => o.Branch == plan.Branch && o.Path.Equals(plan.Path, StringComparison.OrdinalIgnoreCase)))
        {
            record.Branch = plan.Name; record.Path = plan.NewPath;
            writes[Operations.FileFor(root, record.Id)] = JsonSerializer.Serialize(record, SgConfig.JsonOptions);
        }
        var refs = new List<(string Ref, string Before, string After)>();
        foreach (var shelf in Shelf.For(root, null, plan.Branch))
        {
            var before = shelf.Sha;
            shelf.Branch = plan.Name;
            if (shelf.Path.Equals(plan.Path, StringComparison.OrdinalIgnoreCase)) shelf.Path = plan.NewPath;
            refs.Add((shelf.RefName, before, git.CommitTree(git.TreeOf(before), shelf.Base, Shelf.Message(shelf))));
        }
        var originals = writes.Keys.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllText(p) : null);
        var written = new List<string>(); var updated = new List<(string Ref, string Before)>();
        var renamed = false; var moved = false;
        var backupKeys = new[] { "sgBackedUp", Backup.FailedKey, Backup.RemoteKey }.ToDictionary(k => k, k => git.ConfigGet($"branch.{plan.Branch}.{k}"));
        try
        {
            git.Ok(plan.Path, "branch", "-m", plan.Name); renamed = true;
            if (!plan.Path.Equals(plan.NewPath, StringComparison.OrdinalIgnoreCase))
            {
                git.Ok(null, "worktree", "move", plan.Path, plan.NewPath); moved = true;
            }
            foreach (var (path, text) in writes) { Write(path, text); written.Add(path); }
            foreach (var r in refs) { git.UpdateRef(r.Ref, r.After); updated.Add((r.Ref, r.Before)); }
            foreach (var key in backupKeys.Keys) git.ConfigUnset($"branch.{plan.Name}.{key}");
            root.Config.Backup = config.Backup;
        }
        catch (Exception error) when (error is SgException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            using var uncancelled = Cancellation.Use(CancellationToken.None);
            var failures = new List<string>();
            void Undo(Action action) { try { action(); } catch (Exception e) { failures.Add(e.Message); } }
            foreach (var r in updated.AsEnumerable().Reverse()) Undo(() => git.UpdateRef(r.Ref, r.Before));
            foreach (var path in written.AsEnumerable().Reverse()) Undo(() => Write(path, originals[path]));
            var currentPath = moved ? plan.NewPath : plan.Path;
            if (moved) Undo(() => { git.Ok(null, "worktree", "move", plan.NewPath, plan.Path); currentPath = plan.Path; });
            if (renamed) Undo(() => git.Ok(currentPath, "branch", "-m", plan.Branch));
            foreach (var (key, value) in backupKeys) if (value != null) Undo(() => git.Config($"branch.{plan.Branch}.{key}", value));
            throw new SgException(error.Message + (failures.Count == 0 ? "\nRename rolled back; the original worktree is retained."
                : "\nSome rollback steps failed. The worktree files remain at " + currentPath + ":\n" + string.Join("\n", failures)));
        }
        root.Log.Info($"Renamed {plan.Branch} to {plan.Name}: {plan.NewPath}");
        return plan;
    }
    static void Write(string path, string? text)
    {
        if (text == null) File.Delete(path); else AtomicFile.WriteAllText(path, text);
    }
}
