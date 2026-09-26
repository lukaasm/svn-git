using System.Text.Json;

namespace Sg.Core;

public sealed record TransferFile(string Path, string Status, string Action);
public sealed record TransferNotice(string Path, string Reason);
public sealed class CheckoutTransferPlan
{
    public string Checkout { get; init; } = "";
    public string Target { get; init; } = "";
    public bool NewWorktree { get; init; }
    public bool Move { get; init; }
    public string[]? Paths { get; init; }
    public string Token { get; internal set; } = "";
    public List<TransferFile> Files { get; } = [];
    public List<TransferNotice> LeftBehind { get; } = [];
    public List<TransferNotice> Conflicts { get; } = [];
    public bool CanApply => Files.Count > 0 && Conflicts.Count == 0;
    internal List<TransferContent> Contents { get; } = [];
    internal string SourceHead = "", TargetHead = "", Index = "", Destination = "";
    internal List<string> UnversionedDirectories { get; } = [];
}
internal sealed record TransferContent(string Path, string Item, string? Base, string? Source, string? Before, string? After);
public sealed record CheckoutTransferResult(string Path, int Files, bool Moved, string SourceShelf, string DestinationShelf, IReadOnlyList<TransferNotice> LeftBehind);

/// <summary>What a new worktree does with the edits made directly in its checkout.</summary>
public enum CheckoutEdits { Stay, Copy, Move }

/// <summary>A new worktree, and what became of the checkout edits it was asked to take: carried, or why they stayed.</summary>
public sealed record NewWorktreeResult(BranchResult Branch, CheckoutTransferResult? Carried, string? Stayed);

/// <summary>Preview and transfer checkout edits without moving either branch tip or the destination index.</summary>
public static class CheckoutTransfer
{
    public static CheckoutTransferPlan Preview(SgRoot root, CheckoutConfig checkout, string target,
        bool newWorktree = false, bool move = false, IEnumerable<string>? paths = null, Action<int, int>? progress = null)
    {
        using var gate = root.Lock();
        var co = root.Checkout(checkout.Name);
        if (Operations.List(root).Any(o => !o.Terminal && o.Checkout.Equals(co.Name, StringComparison.OrdinalIgnoreCase)))
            throw new SgException("Finish or close the pending checkout operation in Activity before transferring edits.");
        var git = root.Git;
        var plan = new CheckoutTransferPlan { Checkout = co.Name, Target = target, NewWorktree = newWorktree, Move = move,
            Paths = paths?.Select(PathUtil.Rel).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray() };
        plan.SourceHead = git.HeadSha(co.Path);
        if (newWorktree)
        {
            git.CheckBranchName(target);
            plan.Destination = root.WorktreePathFor(target);
            if (git.RefSha("refs/heads/" + target) != null || Directory.Exists(plan.Destination) || File.Exists(plan.Destination))
                throw new SgException("That branch or worktree already exists. Choose Existing worktree or another name.");
            plan.TargetHead = git.RefSha(root.SnapshotRef(co)) ?? throw new SgException("Create a snapshot of this checkout first.");
        }
        else
        {
            plan.Destination = CodeReview.Worktree(root, target);
            var branch = git.CurrentBranch(plan.Destination);
            if (!Ops.BaseCheckout(root, branch).Name.Equals(co.Name, StringComparison.OrdinalIgnoreCase))
                throw new SgException("Choose a worktree belonging to this checkout.");
            if (Conflicts.HasPending(git, plan.Destination)) throw new SgException("Finish the worktree's pending merge or rebase first.");
            if (git.Run(plan.Destination, "config", "--bool", "core.sparseCheckout").StdOut.Trim() == "true")
                throw new SgException("Choose a full worktree. A sparse worktree may leave transferred files hidden.");
            plan.TargetHead = git.HeadSha(plan.Destination);
            plan.Index = git.WriteTree(plan.Destination);
        }

        var changes = Ops.CheckoutChanges(root, co).Where(c => plan.Paths == null || plan.Paths.Any(p => PathUtil.IsUnder(c.Path, p)
            || c.Item == "unversioned" && PathUtil.IsUnder(p, c.Path))).ToList();
        var versioned = changes.Where(c => c.Versioned).Select(c => c.Path).ToArray();
        // git lists files and never a folder, so every versioned change of a git clone is a file.
        var kinds = versioned.Length == 0 ? []
            : co.IsGit ? versioned.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, _ => "file", StringComparer.OrdinalIgnoreCase)
            : root.Svn.InfoMany(co.Path, versioned, recursive: false).ToDictionary(i => i.Path, i => i.Kind, StringComparer.OrdinalIgnoreCase);
        var files = new List<(string Path, string Item)>();
        foreach (var c in changes)
        {
            var why = Excluded(co, c.Path) ?? UnsafePath(co.Path, c.Path, allowDirectory: true);
            if (why == null && (c.Props is "modified" or "conflicted" || c.Item == "normal")) why = "SVN property changes stay in the checkout, together with this path's contents.";
            if (why == null && c.Item is not ("modified" or "added" or "deleted" or "missing" or "unversioned"))
                why = $"Resolve this {(co.IsGit ? "" : "SVN ")}state before transferring: " + c.Item;
            if (why == null && c.Versioned && kinds.GetValueOrDefault(c.Path) != "file") why = "SVN directory changes stay in the checkout; individual file changes can transfer.";
            if (why != null) { plan.LeftBehind.Add(new(c.Path, why)); continue; }
            if (c.Item == "unversioned" && Directory.Exists(PathUtil.Join(co.Path, c.Path))) Walk(c.Path);
            else files.Add((c.Path, c.Item));
        }
        files = files.Where(f => plan.Paths == null || plan.Paths.Any(p => PathUtil.IsUnder(f.Path, p)))
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var checkedFiles = 0;
        progress?.Invoke(0, files.Count);
        foreach (var batch in files.Chunk(64))
        {
            Cancellation.ThrowIfRequested();
            root.Log.Progress("Checking transfer", checkedFiles, files.Count, "files", "Reading file contents");
            var pathsInBatch = batch.Select(f => f.Path).ToArray();
            var sources = HashBatch(root, co.Path, pathsInBatch);
            var bases = ReadBases(root, co, plan.SourceHead, batch.Where(f => f.Item is not ("added" or "unversioned")).Select(f => f.Path).ToArray());
            var unsafeTargets = pathsInBatch.Select(p => (Path: p, Reason: UnsafePath(plan.Destination, p)))
                .Where(p => p.Reason != null).ToDictionary(p => p.Path, p => p.Reason!, StringComparer.Ordinal);
            var targets = newWorktree ? [] : HashBatch(root, plan.Destination, pathsInBatch.Where(p => !unsafeTargets.ContainsKey(p)));
            var entries = newWorktree ? git.EntriesAt(plan.TargetHead, pathsInBatch).ToDictionary(e => e.Path, StringComparer.Ordinal) : [];
            foreach (var (path, item) in batch)
            {
                Cancellation.ThrowIfRequested();
                root.Log.Progress("Checking transfer", ++checkedFiles, files.Count, "files", path);
                var source = sources.GetValueOrDefault(path);
                var basis = bases.GetValueOrDefault(path);
                if (unsafeTargets.TryGetValue(path, out var unsafeTarget)) { plan.Conflicts.Add(new(path, unsafeTarget)); continue; }
                string? before;
                if (newWorktree)
                {
                    var entry = entries.GetValueOrDefault(path);
                    if (entry != null && entry.Mode != "100644" && entry.Mode != "100755")
                    { plan.Conflicts.Add(new(path, "The destination is not a regular file.")); continue; }
                    before = entry?.Sha;
                }
                else before = targets.GetValueOrDefault(path);
                var after = source;
                var action = source == null ? "Delete" : before == null ? "Add" : "Copy";
                if (source == before) action = "Already present";
                else if (before != basis)
                {
                    if (before == null || basis == null || source == null || !Merge(root, path, basis, before, source, out after))
                    { plan.Conflicts.Add(new(path, "The changes overlap with the destination. Resolve them or choose another worktree.")); continue; }
                    action = "Merge edits";
                }
                plan.Contents.Add(new(path, item, basis, source, before, after));
                plan.Files.Add(new(path, item, action));
            }
            progress?.Invoke(checkedFiles, files.Count);
        }
        root.Log.ProgressEnd("Checking transfer", null);
        plan.Token = WorkspaceVersion.Hash(JsonSerializer.Serialize(new { plan.Checkout, plan.Target, plan.NewWorktree, plan.Move,
            plan.Paths, plan.SourceHead, plan.TargetHead, plan.Index, plan.Destination, plan.Contents, plan.LeftBehind, plan.Conflicts }));
        return plan;

        void Walk(string rel)
        {
            Cancellation.ThrowIfRequested();
            plan.UnversionedDirectories.Add(rel);
            foreach (var full in Directory.EnumerateFileSystemEntries(PathUtil.Join(co.Path, rel)).Order(StringComparer.Ordinal))
            {
                Cancellation.ThrowIfRequested();
                var p = PathUtil.RelativeTo(co.Path, full);
                var why = Excluded(co, p) ?? UnsafePath(co.Path, p, allowDirectory: true);
                if (why != null) { plan.LeftBehind.Add(new(p, why)); continue; }
                if (Directory.Exists(full)) Walk(p); else files.Add((p, "unversioned"));
            }
        }
    }

    /// <summary>
    /// A new worktree, then the checkout's edits into it when they were asked for, in one step. The edits
    /// go only when the preview is clean; otherwise they stay, the worktree is kept, and the result says why.
    /// </summary>
    public static NewWorktreeResult NewWorktree(SgRoot root, string name, CheckoutConfig co, CheckoutEdits edits,
        IEnumerable<string>? without = null, bool minimal = false, SharedMode? shared = null)
    {
        var branch = Ops.Branch(root, name, co, without, minimal, shared);
        if (edits == CheckoutEdits.Stay) return new(branch, null, null);
        try
        {
            var plan = Preview(root, co, name, move: edits == CheckoutEdits.Move);
            if (plan.CanApply) return new(branch, Apply(root, plan), null);
            return new(branch, null, plan.Files.Count == 0 && plan.Conflicts.Count == 0
                ? "The checkout has no edits that can go to a worktree."
                : $"{plan.Conflicts.Count} of the edits overlap with the worktree's files, so none were taken.");
        }
        catch (SgException e) { return new(branch, null, e.Message); }
    }

    public static CheckoutTransferResult Apply(SgRoot root, CheckoutTransferPlan preview)
    {
        using var gate = root.Lock();
        var co = root.Checkout(preview.Checkout);
        var plan = Preview(root, co, preview.Target, preview.NewWorktree, preview.Move, preview.Paths);
        if (preview.Token != plan.Token) throw new SgException("The checkout or destination changed. Preview the transfer again before continuing.");
        if (!plan.CanApply) throw new SgException("There are no transferable files, or the preview has conflicts.");
        // Keep both original versions under ordinary shelf refs before the first working file is changed.
        var sourceBase = Commit(root, plan.SourceHead, plan.Contents.Select(c => (c.Path, c.Base)));
        var source = Keep(root, co, co.Path, null, sourceBase, plan.Contents.Select(c => (c.Path, c.Source)), plan.Contents, "Before transfer from " + co.Name);
        var destinationBase = Commit(root, plan.TargetHead, plan.Contents.Select(c => (c.Path, c.After)));
        var branch = plan.NewWorktree ? plan.Target : root.Git.CurrentBranch(plan.Destination);
        var destination = Keep(root, co, plan.Destination, branch, destinationBase, plan.Contents.Select(c => (c.Path, c.Before)), plan.Contents, "Before transfer to " + branch);
        try
        {
            if (plan.NewWorktree) Ops.Branch(root, plan.Target, co);
            foreach (var c in plan.Contents)
            {
                Cancellation.ThrowIfRequested();
                var why = UnsafePath(plan.Destination, c.Path);
                if (why != null || Hash(root, plan.Destination, c.Path) != c.Before)
                    throw new SgException("Destination changed during transfer: " + c.Path + (why == null ? "" : ". " + why));
                Write(root, plan.Destination, c.Path, c.After);
            }
            if (plan.Move)
            {
                // Re-read the checkout's states and actual bytes before cleanup, after all destination writes succeed.
                var current = Preview(root, co, branch, false, true, plan.Paths);
                if (current.SourceHead != plan.SourceHead || !SameSource(plan, current)
                    || plan.Contents.Any(c => UnsafePath(plan.Destination, c.Path) != null || Hash(root, plan.Destination, c.Path) != c.After))
                    throw new SgException("Files changed during transfer. The copy is kept; source cleanup stopped.");
                foreach (var c in plan.Contents)
                {
                    Cancellation.ThrowIfRequested();
                    if (UnsafePath(co.Path, c.Path) != null || Hash(root, co.Path, c.Path) != c.Source)
                        throw new SgException("Source changed during cleanup: " + c.Path);
                    if (c.Item != "unversioned" && root.Vcs(co).Revert(root, co, [c.Path]) is { } said)
                        throw new SgException("revert failed: " + said);
                    if (c.Item is "added" or "unversioned") File.Delete(PathUtil.Join(co.Path, c.Path));
                }
                foreach (var rel in plan.UnversionedDirectories.OrderByDescending(p => p.Length))
                {
                    var full = PathUtil.Join(co.Path, rel);
                    if (UnsafePath(co.Path, rel, allowDirectory: true) == null && Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                        Directory.Delete(full); // Only empty, originally unversioned folders; never recursive cleanup.
                }
            }
        }
        catch (Exception e) when (e is SgException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            throw new SgException(e.Message + "\nRecovery shelves: " + source.Id + " (checkout), " + destination.Id + " (destination). Review both folders before retrying.");
        }
        root.Log.Info($"{(plan.Move ? "Moved" : "Copied")} {plan.Files.Count} file(s) to {branch}. Recovery shelves retained.");
        return new(plan.Destination, plan.Files.Count, plan.Move, source.Id, destination.Id, plan.LeftBehind);
    }

    static bool SameSource(CheckoutTransferPlan a, CheckoutTransferPlan b) =>
        a.Contents.Select(c => (c.Path, c.Item, c.Base, c.Source)).SequenceEqual(b.Contents.Select(c => (c.Path, c.Item, c.Base, c.Source)))
        && a.LeftBehind.SequenceEqual(b.LeftBehind);

    static string? Excluded(CheckoutConfig co, string path) =>
        co.Skip.Concat(co.Junctions).Any(p => PathUtil.IsUnder(path, PathUtil.Rel(p))) ? "Shared or excluded content stays in the checkout." : null;

    static string? UnsafePath(string folder, string path, bool allowDirectory = false)
    {
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.Equals(".svn", StringComparison.OrdinalIgnoreCase) || p.Equals(".sg", StringComparison.OrdinalIgnoreCase)) || PathUtil.HasReservedName(path))
            return "This path cannot be transferred.";
        var current = Path.GetFullPath(folder);
        if (PathUtil.IsReparsePoint(current)) return "Linked folders cannot be transferred.";
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (PathUtil.IsReparsePoint(current)) return "Linked files and folders stay in place.";
            if (i < parts.Length - 1 && File.Exists(current)) return "A file occupies a parent folder.";
        }
        return !allowDirectory && Directory.Exists(current) ? "A directory occupies this file path." : null;
    }

    // A git clone's files are hashed through its line-ending rules, so they compare with the snapshot's blobs.
    static string? Hash(SgRoot root, string folder, string path) => File.Exists(PathUtil.Join(folder, path))
        ? root.Git.HashObjects([PathUtil.Join(folder, path)], folder)[0] : null;

    static Dictionary<string, string> HashBatch(SgRoot root, string folder, IEnumerable<string> paths)
    {
        var present = paths.Where(p => File.Exists(PathUtil.Join(folder, p))).ToArray();
        var hashes = root.Git.HashObjects(present.Select(p => PathUtil.Join(folder, p)), folder);
        return present.Select((p, i) => (Path: p, Sha: hashes[i])).ToDictionary(p => p.Path, p => p.Sha, StringComparer.Ordinal);
    }

    /// <summary>
    /// What each file was before the checkout's edit: svn's BASE, or for a git clone the snapshot's blob,
    /// which is the server's version of it in the clone and in each submodule.
    /// </summary>
    static Dictionary<string, string> ReadBases(SgRoot root, CheckoutConfig co, string snapshot, string[] paths)
    {
        if (paths.Length == 0) return [];
        if (co.IsGit)
            return root.Git.EntriesAt(snapshot, paths).Where(e => e.Type == "blob")
                .ToDictionary(e => e.Path, e => e.Sha, StringComparer.Ordinal);
        var folder = co.Path;
        var temps = paths.Select(_ => root.NewTempFile(".base")).ToArray();
        try
        {
            Fan.Map(Enumerable.Range(0, paths.Length).ToArray(), i =>
            {
                Cancellation.ThrowIfRequested();
                return root.Svn.CatToFile(folder, paths[i], "BASE", temps[i]).EnsureOk();
            });
            var hashes = root.Git.HashObjects(temps);
            return paths.Select((p, i) => (Path: p, Sha: hashes[i])).ToDictionary(p => p.Path, p => p.Sha, StringComparer.Ordinal);
        }
        finally { foreach (var temp in temps) File.Delete(temp); }
    }

    static bool Merge(SgRoot root, string path, string basis, string before, string source, out string? after)
    {
        var a = root.NewTempFile(".base"); var b = root.NewTempFile(".ours"); var c = root.NewTempFile(".theirs");
        try
        {
            root.Git.BlobToFile(basis, a); root.Git.BlobToFile(before, b); root.Git.BlobToFile(source, c);
            if (root.Git.MergeFile(b, a, c, path + " in worktree", path + " in checkout") != 0) { after = null; return false; }
            after = root.Git.HashObjects([b])[0]; return true;
        }
        finally { File.Delete(a); File.Delete(b); File.Delete(c); }
    }

    static string Commit(SgRoot root, string parent, IEnumerable<(string Path, string? Sha)> files)
    {
        var index = root.NewTempFile(".index");
        try
        {
            root.Git.ReadTree(root.Git.Store, parent, index);
            root.Git.UpdateIndexInfo(root.Git.Store, files.Select(f => (f.Sha == null ? "0" : "100644", f.Sha ?? new string('0', 40), f.Path)), index);
            return root.Git.CommitTree(root.Git.WriteTree(root.Git.Store, index), parent, "Transfer recovery\n");
        }
        finally { File.Delete(index); }
    }

    static ShelfInfo Keep(SgRoot root, CheckoutConfig co, string path, string? branch, string basis,
        IEnumerable<(string Path, string? Sha)> files, List<TransferContent> contents, string title)
    {
        var commit = Commit(root, basis, files);
        var info = new ShelfInfo { Title = title, Kind = branch == null ? "checkout" : "worktree",
            Files = contents.Select(c => new ShelfFile(c.Path, branch == null ? c.Item : "M")).ToList() };
        return Shelf.Adopt(root, root.Git.TreeOf(commit), Shelf.Message(info), path, basis, co, branch);
    }

    static void Write(SgRoot root, string folder, string path, string? sha)
    {
        var full = PathUtil.Join(folder, path);
        if (sha == null) { File.Delete(full); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = Path.Combine(Path.GetDirectoryName(full)!, ".sg-transfer-" + Guid.NewGuid().ToString("N"));
        try
        {
            root.Git.BlobToFile(sha, temp);
            if (!OperatingSystem.IsWindows() && File.Exists(full)) File.SetUnixFileMode(temp, File.GetUnixFileMode(full));
            File.Move(temp, full, overwrite: true);
        }
        finally { File.Delete(temp); }
    }
}
