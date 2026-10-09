using System.Text.Json;

namespace Sg.Core;

/// <summary>A branch born from the checkout, which goes with it, and what removing it loses.</summary>
public sealed record CheckoutRemovalBranch(string Branch, string? Path, int Ahead, int DirtyFiles, bool HalfPushed);

/// <summary>
/// What removing a checkout takes with it. The folder on disk is not on the list: it stays, with its files.
/// Blockers say why it cannot go yet; the token is what the removal checks it is still looking at.
/// </summary>
public sealed record CheckoutRemovalPlan(string Checkout, string Path, CheckoutKind Kind, string Snapshot,
    IReadOnlyList<CheckoutRemovalBranch> Branches, int Shelves, IReadOnlyList<string> Blockers)
{
    public bool Ready => Blockers.Count == 0;
    public string Token => WorkspaceVersion.Hash(JsonSerializer.Serialize(new { Checkout, Path, Snapshot, Branches }));
}

/// <summary>
/// Undoes checkout add, and everything built on the checkout since. Every branch born from it goes too,
/// because sg cannot rebase, push or back up a branch whose checkout is gone. What sg wrote into the folder
/// goes - the .git pointer of an SVN working copy, the sg-root marker of a git clone - and the folder stays.
/// Shelves, finished operation records and backups stay: they are kept by name, and adding the folder again
/// under the same name finds them.
/// </summary>
public static class CheckoutRemoval
{
    public static CheckoutRemovalPlan Preview(SgRoot root, string name)
    {
        var co = root.Checkout(name);
        var git = root.Git;
        var snapRef = root.SnapshotRef(co);
        var snapshot = git.RefSha(snapRef) ?? "";
        var heads = git.RefIndex("refs/heads/");

        // The checkouts' own folders are linked worktrees of the store as well, and are no branch's.
        var coPaths = root.Config.Checkouts.Select(c => c.Path.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in git.WorktreeList())
        {
            if (w.Bare || coPaths.Contains(w.Path.TrimEnd('\\', '/'))) continue;
            var b = w.Branch ?? (Directory.Exists(w.Path) ? git.RebaseHeadName(w.Path) : null);
            if (b != null) folders.TryAdd(b, w.Path);
        }

        var branches = new List<CheckoutRemovalBranch>();
        foreach (var (branch, baseName) in git.BranchBases().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!baseName.Equals(co.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var path = folders.GetValueOrDefault(branch);
            var hasHead = heads.TryGetValue("refs/heads/" + branch, out var head);
            // Config a branch left behind when it went some other way. The removal clears it without a word.
            if (path == null && !hasHead) continue;
            var ahead = hasHead && snapshot.Length > 0 ? git.CountBoth(snapRef, "refs/heads/" + branch).Right : 0;
            var dirty = path != null && Directory.Exists(path) ? git.DirtyCount(path) : 0;
            var halfPushed = hasHead && head!.Subject.StartsWith("not pushed yet", StringComparison.OrdinalIgnoreCase);
            branches.Add(new CheckoutRemovalBranch(branch, path, ahead, dirty, halfPushed));
        }

        var blockers = new List<string>();
        var paths = branches.Where(b => b.Path != null).Select(b => System.IO.Path.GetFullPath(b.Path!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var op in Operations.List(root).Where(x => !x.Terminal))
            if (op.Checkout.Equals(co.Name, StringComparison.OrdinalIgnoreCase) || op.Path.Length > 0 && paths.Contains(System.IO.Path.GetFullPath(op.Path)))
                blockers.Add($"{op.Title} on {(op.Branch.Length > 0 ? op.Branch : co.Name)} is not finished. Finish or dismiss it in Activity first.");

        return new CheckoutRemovalPlan(co.Name, co.Path, co.Kind, snapshot, branches, Shelf.For(root, co.Name, null).Count, blockers);
    }

    public static CheckoutRemovalPlan Apply(SgRoot root, CheckoutRemovalPlan preview)
    {
        using var gate = root.Lock();
        var plan = Preview(root, preview.Checkout);
        if (!plan.Ready) throw new SgException(string.Join("\n", plan.Blockers));
        if (plan.Token != preview.Token)
            throw new SgException("The checkout changed since the preview: a branch, a commit or an edit. Preview the removal again.");
        var co = root.Checkout(plan.Checkout);
        var git = root.Git;

        // The branches go first. A folder held open stops the removal there, with the checkout still
        // registered and every branch not reached yet still in place, so trying again carries on from it.
        foreach (var b in plan.Branches) Ops.Remove(root, b.Branch, force: true);
        foreach (var (branch, baseName) in git.BranchBases())
            if (baseName.Equals(co.Name, StringComparison.OrdinalIgnoreCase))
            {
                git.ConfigUnset($"branch.{branch}.sgBase");
                git.ConfigUnset($"branch.{branch}.sgShared");
            }

        // Before the config entry goes: the store's linked worktree of an SVN checkout is told apart
        // from a branch's only by its folder being a registered checkout.
        root.Vcs(co).Detach(root, co);
        git.DeleteRef(root.SnapshotRef(co));

        var icon = CheckoutAppearance.IconPath(root, co);
        root.Config.Checkouts.Remove(co);
        root.Save();
        root.ForgetIgnores(co);
        // Other checkouts may show the same image.
        if (icon != null && !root.Config.Checkouts.Any(c => c.Icon == co.Icon))
        {
            try { File.Delete(icon); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var left = Leftover(root, co);
        if (left != null) root.Log.Warn($"{left} is still there and could not be deleted. Delete it by hand before adding the folder again.");
        root.Log.Info($"checkout {co.Name} is removed. Its folder stays: {co.Path}");
        return plan;
    }

    /// <summary>What Detach takes out of the folder and keeps quiet about when Windows will not let it.</summary>
    static string? Leftover(SgRoot root, CheckoutConfig co)
    {
        var dotGit = System.IO.Path.Combine(co.Path, ".git");
        if (!co.IsGit) return File.Exists(dotGit) ? dotGit : null;
        return Directory.Exists(dotGit) && GitCheckoutVcs.ReadRootMarker(dotGit) is { } marked
               && marked.Equals(root.RootPath, StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.Combine(dotGit, GitCheckoutVcs.RootMarker) : null;
    }
}
