using System.Globalization;
using System.Text;

namespace Sg.Core;

/// <summary>One file a shelf holds, and what the working copy called it when the shelf was made.</summary>
public sealed record ShelfFile(string Path, string Code);

/// <summary>
/// Local changes taken out of a working copy and kept in the store, to be put back later. A shelf is a
/// commit: its parent is what the working copy was against, and its tree is that tree with the changes
/// in it. So a shelf is a diff between two commits, and everything git can already do with one works on
/// it: read it, show it a file at a time, and merge it back into a file that moved on since.
/// </summary>
public sealed class ShelfInfo
{
    /// <summary>The name under refs/sg/shelf. It starts with the time it was made, so a list sorts itself.</summary>
    public string Id = "";
    public string Sha = "";
    public string Title = "";

    /// <summary>"checkout" for the local edits of an SVN working copy, "worktree" for a branch's own changes.</summary>
    public string Kind = "";
    public string Checkout = "";
    public string Branch = "";

    /// <summary>The folder it came out of, and the only one it goes back into.</summary>
    public string Path = "";

    /// <summary>The commit it was taken against: the snapshot for a checkout, the branch tip for a worktree.</summary>
    public string Base = "";

    /// <summary>
    /// The format its message is written in. A shelf from a newer sg may say things this one does not
    /// read, and half a shelf put back is worse than none, so a higher number is refused rather than guessed at.
    /// </summary>
    public int Version = 1;
    public DateTimeOffset Created;
    public List<ShelfFile> Files = new();

    /// <summary>Paths that were staged when the shelf was made, so putting it back can stage them again.</summary>
    public List<string> Staged = new();

    public bool IsCheckout => Kind == "checkout";
    public string RefName => Shelf.RefPrefix + Id;
    public int Count => Files.Count;

    /// <summary>The checkout or the branch it belongs to. One list shows both, so it needs one word for it.</summary>
    public string Where => IsCheckout ? Checkout : Branch;

    /// <summary>The folder it came from is not there any more, so it has nowhere to go back to.</summary>
    public bool Gone => !Directory.Exists(Path);
}

/// <summary>A shelf, and what could not go into it. Nothing is left behind without being named.</summary>
public sealed record ShelfSaveResult(ShelfInfo Shelf, List<string> LeftBehind);

public sealed class ShelfRestoreResult
{
    public ShelfInfo Shelf = new();
    public List<string> Written = new();
    public List<string> Deleted = new();

    /// <summary>Files that had moved on, so the change was merged into them instead of written over them.</summary>
    public List<string> Merged = new();

    /// <summary>Files the merge could not settle. They hold conflict markers now, and the shelf stays.</summary>
    public List<string> Conflicted = new();

    /// <summary>The shelf is still in the store: it was asked for, or the merge left conflicts.</summary>
    public bool Kept;
}

/// <summary>
/// Put changes aside, and take them back later. Two things need this. A checkout keeps local edits, and
/// a push refuses to write a file that has one, so the edit blocks the push until it is out of the way.
/// A worktree holds work in progress that is in the way of a rebase, or of the next task. Both are the
/// same operation: build a commit out of what is changed, then put the working copy back as it was.
/// </summary>
public static class Shelf
{
    public const string RefPrefix = "refs/sg/shelf/";

    /// <summary>The message format this build writes and is willing to read.</summary>
    public const int Version = 1;

    const string KeyVersion = "sg-shelf";
    const string KeyKind = "sg-shelf-kind";
    const string KeyCheckout = "sg-shelf-checkout";
    const string KeyBranch = "sg-shelf-branch";
    const string KeyPath = "sg-shelf-path";
    const string KeyBase = "sg-shelf-base";
    const string KeyCreated = "sg-shelf-created";
    const string KeyFile = "sg-shelf-file";
    const string KeyStaged = "sg-shelf-staged";

    // ---- reading ----

    /// <summary>Every shelf in the store, newest first.</summary>
    public static List<ShelfInfo> List(SgRoot root) => From(root.Git.RefIndex(RefPrefix.TrimEnd('/')));

    /// <summary>
    /// The shelves among refs that were read already. The overview reads every ref it needs in one git
    /// call, and counting the shelves on a card must not turn that into two.
    /// </summary>
    public static List<ShelfInfo> From(IReadOnlyDictionary<string, RefInfo> refs) =>
        refs.Where(kv => kv.Key.StartsWith(RefPrefix, StringComparison.Ordinal))
            .Select(kv => Parse(kv.Key[RefPrefix.Length..], kv.Value.Sha, kv.Value.Message))
            .OrderByDescending(s => s.Created)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>The shelves of one branch, or of one checkout. The overview asks this per card.</summary>
    public static List<ShelfInfo> For(SgRoot root, string? checkout, string? branch) =>
        List(root).Where(s => branch != null
            ? !s.IsCheckout && s.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase)
            : s.IsCheckout && s.Checkout.Equals(checkout, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>One shelf by its id, or by enough of it to name only one.</summary>
    public static ShelfInfo Read(SgRoot root, string id)
    {
        var name = id.StartsWith(RefPrefix, StringComparison.Ordinal) ? id[RefPrefix.Length..] : id;
        var sha = root.Git.RefSha(RefPrefix + name);
        if (sha != null) return Parse(name, sha, root.Git.Body(sha));
        var all = List(root);
        var hits = all.Where(s => s.Id.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1)
            throw new SgException($"'{name}' names {hits.Count} shelves: " + string.Join(", ", hits.Take(5).Select(s => s.Id)));
        throw new SgException($"no shelf called '{name}'. " + (all.Count == 0 ? "There are none." : $"There are {all.Count}."));
    }

    /// <summary>What the shelf changes, one row per file, with A, M or D. This is the list a window shows.</summary>
    public static List<DiffEntry> Changes(SgRoot root, ShelfInfo shelf) =>
        root.Git.DiffNameStatus(root.Git.Store, shelf.Base, shelf.Sha, renames: false);

    /// <summary>The change as one patch, all of it or one folder of it, for a view that reads it in one piece.</summary>
    public static string PatchText(SgRoot root, ShelfInfo shelf, string? path = null) =>
        root.Git.UnifiedDiff(root.Git.Store, shelf.Base, shelf.Sha, string.IsNullOrEmpty(path) ? null : path);

    /// <summary>One side of one file of a shelf: what it held before, or what the shelf holds.</summary>
    public static string Side(SgRoot root, ShelfInfo shelf, string path, bool shelved) =>
        root.Git.ShowText(shelved ? shelf.Sha : shelf.Base, path);

    // ---- making one ----

    /// <summary>
    /// Takes the named changes out of a working copy and keeps them. paths null means everything that is
    /// changed there; a folder in it means everything under that folder. The folder can be a checkout or
    /// a branch worktree, and which one it is decides how the changes are read and how they go back.
    /// </summary>
    public static ShelfSaveResult Save(SgRoot root, string folder, IEnumerable<string>? paths, string title)
    {
        folder = Path.GetFullPath(folder);
        var co = root.CheckoutContaining(folder);
        using var _ = root.Lock();
        return co != null ? FromCheckout(root, co, paths, title) : FromWorktree(root, folder, paths, title);
    }

    static ShelfSaveResult FromCheckout(SgRoot root, CheckoutConfig co, IEnumerable<string>? paths, string title)
    {
        var git = root.Git;
        var picked = Under(Ops.CheckoutChanges(root, co), paths, c => c.Path);
        if (picked.Count == 0) throw new SgException($"nothing to shelve: {co.Name} has no local change to those paths.");

        var stuck = picked.Where(c => c.Item is "conflicted" or "obstructed").Select(c => c.Path).ToList();
        if (stuck.Count > 0)
            throw new SgException("these are in conflict or obstructed, so what they hold is not one version: " + Names(stuck)
                + "\nSolve that in the checkout first, then shelve them.");
        var skipped = picked.Where(c => co.Skip.Any(s => PathUtil.IsUnder(c.Path, PathUtil.Rel(s)))).Select(c => c.Path).ToList();
        if (skipped.Count > 0)
            throw new SgException($"{co.Name} does not track these, so a shelf cannot hold them: " + Names(skipped));
        var reserved = picked.Where(c => PathUtil.HasReservedName(c.Path)).Select(c => c.Path).ToList();
        if (reserved.Count > 0)
            throw new SgException("Windows reserves these names, so git cannot hold them: " + Names(reserved));

        // A shelf holds file content, and git has nowhere to put an svn property. A file that carries a
        // property change stays where it is, whole: svn revert takes the property with the text, and
        // only the text would come back. The reader is told which, rather than finding them gone.
        var props = picked.Where(HasPropertyEdit).Select(c => c.Path).ToList();
        var files = picked.Where(c => !HasPropertyEdit(c)).ToList();
        if (files.Count == 0)
            throw new SgException("every picked change is an svn property change, and a shelf holds file content: " + Names(props));

        var info = new ShelfInfo
        {
            Title = Title(title),
            Kind = "checkout",
            Checkout = co.Name,
            Path = co.Path,
            Base = git.HeadSha(co.Path),
            Created = DateTimeOffset.Now,
            Files = files.Select(c => new ShelfFile(c.Path, c.Item)).ToList(),
        };
        info.Id = FreeId(root, info.Title);
        info.Sha = Build(root, co.Path, info);
        git.UpdateRef(info.RefName, info.Sha);

        // The checkout goes back to what SVN has. Revert un-schedules an add and leaves the file behind,
        // so the files SVN never had are the ones that have to go by hand.
        var versioned = files.Where(c => c.Versioned).Select(c => c.Path).ToList();
        if (versioned.Count > 0)
        {
            var r = root.Svn.Revert(co.Path, versioned);
            if (!r.Ok) root.Log.Warn("svn revert said: " + r.StdErr.Trim());
        }
        foreach (var c in files.Where(c => c.Item is "unversioned" or "added")) Delete(PathUtil.Join(co.Path, c.Path));
        root.Log.Info($"shelved {info.Count} file(s) from {co.Name} as {info.Id}");
        return new ShelfSaveResult(info, props);
    }

    static ShelfSaveResult FromWorktree(SgRoot root, string folder, IEnumerable<string>? paths, string title)
    {
        var git = root.Git;
        var worktree = git.Toplevel(folder);
        if (git.RebaseInProgress(worktree))
            throw new SgException(Conflicts.Note(git, worktree) + ". " + Conflicts.Where + " Then shelve.");
        var branch = git.CurrentBranch(worktree);
        var picked = Under(git.StatusEntries(worktree, untracked: true), paths, e => e.Path);
        if (picked.Count == 0) throw new SgException($"nothing to shelve: {branch} has no change to those paths.");
        var stuck = picked.Where(e => e.X == "U" || e.Y == "U").Select(e => e.Path).ToList();
        if (stuck.Count > 0) throw new SgException("these are in conflict: " + Names(stuck) + "\nSolve that first, then shelve them.");

        var info = new ShelfInfo
        {
            Title = Title(title),
            Kind = "worktree",
            Checkout = SafeBase(root, branch),
            Branch = branch,
            Path = worktree,
            Base = git.HeadSha(worktree),
            Created = DateTimeOffset.Now,
            // A rename is two paths, and both belong to the shelf: the new one for what it holds, the
            // old one for the deletion. With only the new one, putting the worktree back leaves the old
            // path deleted, and the rebase or the push this was making room for still refuses.
            Files = picked.SelectMany(e => BothPaths(e).Select(p =>
                new ShelfFile(p, p == e.Path ? e.BothCodes.Trim() : "D"))).ToList(),
            Staged = picked.Where(e => e.Staged).SelectMany(BothPaths).ToList(),
        };
        info.Id = FreeId(root, info.Title);
        info.Sha = Build(root, worktree, info);
        git.UpdateRef(info.RefName, info.Sha);

        git.RestoreFromHead(worktree, picked.Where(e => e.Tracked).SelectMany(BothPaths));
        foreach (var e in picked.Where(e => e.Untracked)) Delete(PathUtil.Join(worktree, e.Path));
        root.Log.Info($"shelved {info.Count} file(s) from {branch} as {info.Id}");
        return new ShelfSaveResult(info, new List<string>());
    }

    /// <summary>
    /// The commit itself. It is built in an index of its own: a checkout's index is the snapshot's, kept
    /// warm so the next snapshot costs a walk instead of rehashing 173k files, and a shelf must not disturb it.
    /// </summary>
    static string Build(SgRoot root, string worktree, ShelfInfo info)
    {
        var tree = TreeOf(root, worktree, info)
                   ?? throw new SgException("those paths hold nothing the base does not hold already, so there is nothing to shelve.");
        return root.Git.CommitTree(tree, info.Base, Message(info));
    }

    /// <summary>The tree a shelf would hold: the base with the files written over it. Null when that is the base itself.</summary>
    static string? TreeOf(SgRoot root, string worktree, ShelfInfo info)
    {
        var git = root.Git;
        var index = root.NewTempFile(".index");
        try
        {
            git.ReadTree(worktree, info.Base, index);
            git.AddPathsForced(worktree, info.Files.Select(f => f.Path), index);
            var tree = git.WriteTree(worktree, index);
            return tree == git.TreeOf(info.Base) ? null : tree;
        }
        finally
        {
            try { File.Delete(index); } catch (IOException) { }
        }
    }

    // ---- the same commit, for a backup ----

    /// <summary>
    /// A commit of the changes not yet committed, and nothing else: the working copy is not touched
    /// and no ref points at the commit. Null when there are none. What a shelf refuses - a conflicted
    /// file, a skipped path, a reserved name, a property change - is left out here rather than refused,
    /// because a backup that stops on one such file protects none of the others; what is left out is
    /// named in the log. Its dates are the base commit's, so the same changes make the same commit
    /// twice and a backup can tell "still the same" from "changed since".
    /// </summary>
    public static ShelfInfo? Wip(SgRoot root, string folder)
    {
        folder = Path.GetFullPath(folder);
        var git = root.Git;
        var co = root.CheckoutContaining(folder);
        ShelfInfo info;
        string where;
        if (co != null)
        {
            var all = Ops.CheckoutChanges(root, co);
            var files = all.Where(c => c.Item is not ("conflicted" or "obstructed")
                                       && !co.Skip.Any(sk => PathUtil.IsUnder(c.Path, PathUtil.Rel(sk)))
                                       && !PathUtil.HasReservedName(c.Path)
                                       && !HasPropertyEdit(c)).ToList();
            if (files.Count == 0) return null;
            if (files.Count < all.Count)
                root.Log.Info($"{co.Name}: {all.Count - files.Count} local change(s) a shelf cannot hold are not in the backup");
            info = new ShelfInfo
            {
                Title = "uncommitted changes",
                Kind = "checkout",
                Checkout = co.Name,
                Path = co.Path,
                Base = git.HeadSha(co.Path),
                Files = files.Select(c => new ShelfFile(c.Path, c.Item)).ToList(),
            };
            where = co.Path;
        }
        else
        {
            var worktree = git.Toplevel(folder);
            if (git.RebaseInProgress(worktree)) return null;
            var branch = git.CurrentBranch(worktree);
            var all = git.StatusEntries(worktree, untracked: true);
            var picked = all.Where(e => e.X != "U" && e.Y != "U").ToList();
            if (picked.Count == 0) return null;
            if (picked.Count < all.Count)
                root.Log.Info($"{branch}: {all.Count - picked.Count} conflicted file(s) are not in the backup");
            info = new ShelfInfo
            {
                Title = "uncommitted changes",
                Kind = "worktree",
                Checkout = SafeBase(root, branch),
                Branch = branch,
                Path = worktree,
                Base = git.HeadSha(worktree),
                Files = picked.SelectMany(e => BothPaths(e).Select(p =>
                    new ShelfFile(p, p == e.Path ? e.BothCodes.Trim() : "D"))).ToList(),
                Staged = picked.Where(e => e.Staged).SelectMany(BothPaths).ToList(),
            };
            where = worktree;
        }
        var under = git.IdentityOf(info.Base);
        info.Created = DateTimeOffset.TryParse(under.CommitterDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when : DateTimeOffset.UnixEpoch;
        var tree = TreeOf(root, where, info);
        if (tree == null) return null;
        var who = new CommitIdentity("sg", "sg@localhost", under.CommitterDate, "sg", "sg@localhost", under.CommitterDate, "");
        info.Sha = git.CommitTreeExact(tree, info.Base, Message(info), who);
        return info;
    }

    /// <summary>
    /// A shelf made again from another machine's: the same title, the same files and what svn called
    /// them, on a tree already merged onto what this folder is at. It is registered and answered;
    /// nothing is written into the folder.
    /// </summary>
    public static ShelfInfo Adopt(SgRoot root, string tree, string body, string path, string baseSha, CheckoutConfig? co, string? branch)
    {
        var old = Parse("", "", body);
        var info = new ShelfInfo
        {
            Title = old.Title,
            Kind = old.Kind.Length > 0 ? old.Kind : branch == null ? "checkout" : "worktree",
            Checkout = co?.Name ?? old.Checkout,
            Branch = branch ?? (old.IsCheckout ? "" : old.Branch),
            Path = path,
            Base = baseSha,
            Created = DateTimeOffset.Now,
            Files = old.Files,
            Staged = old.Staged,
        };
        info.Id = FreeId(root, info.Title);
        info.Sha = root.Git.CommitTree(tree, baseSha, Message(info));
        root.Git.UpdateRef(info.RefName, info.Sha);
        return info;
    }

    // ---- putting one back ----

    /// <summary>
    /// Writes a shelf back into the folder it came from. A file that still holds what it held when the
    /// shelf was made is written over. A file that moved on since, which is the normal end of shelve,
    /// push, sync, gets the change merged into it the way an svn update merges: cleanly, or with conflict
    /// markers and the shelf kept, so nothing is lost while the reader sorts it out.
    /// </summary>
    public static ShelfRestoreResult Restore(SgRoot root, string id, bool keep = false)
    {
        var git = root.Git;
        var info = Read(root, id);
        if (info.Gone) throw new SgException("the folder this shelf came from is gone: " + info.Path);
        if (info.Version > Version)
            throw new SgException($"this shelf was written by a newer sg (format {info.Version}, this one reads {Version}). Update sg, then put it back.");
        // The same refusal saving one makes. A stopped rebase holds files at three stages with conflict
        // markers in them, and writing a shelf over that mixes two unfinished things into one.
        if (!info.IsCheckout && root.Git.RebaseInProgress(info.Path))
            throw new SgException(Conflicts.Note(root.Git, info.Path) + ". " + Conflicts.Where + " Then put the shelf back.");
        using var _ = root.Lock();

        var result = new ShelfRestoreResult { Shelf = info };
        var entries = git.DiffNameStatus(git.Store, info.Base, info.Sha, renames: false);
        if (entries.Count == 0) throw new SgException("this shelf holds no change.");
        var all = entries.Select(e => e.Path).ToList();

        // What each file held when the shelf was made, what the shelf holds, and what is on disk now.
        // A file that still holds what it held is written over. One that moved on is merged, because
        // writing over it would drop whatever came in meanwhile.
        var was = git.LsTree(info.Base, all, recursive: true).ToDictionary(t => t.Path, t => t.Sha, StringComparer.Ordinal);
        var want = git.LsTree(info.Sha, all, recursive: true).ToDictionary(t => t.Path, t => t.Sha, StringComparer.Ordinal);
        var onDisk = git.HashFiles(all.Select(p => PathUtil.Join(info.Path, p)).Where(File.Exists));
        var write = new List<string>();
        var remove = new List<string>();
        var merge = new List<string>();
        foreach (var p in all)
        {
            onDisk.TryGetValue(PathUtil.Join(info.Path, p), out var now);
            was.TryGetValue(p, out var before);
            want.TryGetValue(p, out var after);
            if (now == after) continue;                                  // already what the shelf holds
            if (now != before) { merge.Add(p); continue; }               // moved on since
            if (after == null) remove.Add(p);
            else write.Add(p);
        }

        if (write.Count > 0)
        {
            // Into an index of its own: the checkout's own index is the snapshot's, and git checkout
            // would otherwise leave these paths staged in it.
            var index = root.NewTempFile(".index");
            try
            {
                git.ReadTree(info.Path, "HEAD", index);
                git.CheckoutPathsWithIndex(info.Path, info.Sha, write, index);
            }
            finally
            {
                try { File.Delete(index); } catch (IOException) { }
            }
        }
        foreach (var p in remove) Delete(PathUtil.Join(info.Path, p));
        result.Written.AddRange(write);
        result.Deleted.AddRange(remove);
        foreach (var p in merge) MergeOne(root, info, p, was, want, result);

        Register(root, info, result);
        result.Kept = keep || result.Conflicted.Count > 0;
        if (!result.Kept) git.DeleteRef(info.RefName);
        root.Log.Info($"put {result.Written.Count + result.Deleted.Count} file(s) back into {info.Path}");
        return result;
    }

    /// <summary>
    /// One file that moved on since the shelf was made. Three versions go into the merge: the file as it
    /// was when the shelf was made, the file as it is now, and what the shelf holds. This is the merge an
    /// svn update does, so the result reads the way the rest of the day reads. A shelf that adds a file
    /// somebody else has since added too, or deletes one that has since changed, has no such three, and a
    /// binary file has nothing to merge line by line: those are named and left exactly as they are.
    /// </summary>
    static void MergeOne(SgRoot root, ShelfInfo info, string path,
        Dictionary<string, string> was, Dictionary<string, string> want, ShelfRestoreResult result)
    {
        var abs = PathUtil.Join(info.Path, path);
        was.TryGetValue(path, out var before);
        want.TryGetValue(path, out var after);
        if (before == null || after == null || !File.Exists(abs) || LooksBinary(abs))
        {
            result.Conflicted.Add(path);
            return;
        }
        var ancestor = root.NewTempFile(".base");
        var other = root.NewTempFile(".shelved");
        try
        {
            root.Git.BlobToFile(before, ancestor);
            root.Git.BlobToFile(after, other);
            var conflicts = root.Git.MergeFile(abs, ancestor, other, path + " as it is now", path + " as the shelf holds it");
            if (conflicts == 0)
            {
                result.Merged.Add(path);
                result.Written.Add(path);
            }
            else result.Conflicted.Add(path);
        }
        finally
        {
            try { File.Delete(ancestor); } catch (IOException) { }
            try { File.Delete(other); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The bytes are back; now the working copy has to be told. svn has no index, so a file that was
    /// scheduled for adding or for deleting is scheduled again. git has one, so what was staged is staged again.
    /// </summary>
    static void Register(SgRoot root, ShelfInfo info, ShelfRestoreResult result)
    {
        if (!info.IsCheckout)
        {
            // Every path that was staged, whether or not it is on disk: a staged deletion is a file that
            // is not there, and add -A records that as readily as it records a file that is.
            if (info.Staged.Count > 0) root.Git.AddPaths(info.Path, info.Staged);
            return;
        }
        CheckoutConfig co;
        try { co = root.Checkout(info.Checkout); }
        catch (SgException ex) { root.Log.Warn("the files are back, but svn was not told: " + ex.Message); return; }
        var add = info.Files.Where(f => f.Code == "added").Select(f => f.Path)
            .Where(p => File.Exists(PathUtil.Join(co.Path, p)) || Directory.Exists(PathUtil.Join(co.Path, p))).ToList();
        var rm = info.Files.Where(f => f.Code == "deleted").Select(f => f.Path).ToList();
        try
        {
            if (add.Count > 0) root.Svn.Add(co.Path, add);
            if (rm.Count > 0) root.Svn.Rm(co.Path, rm);
        }
        catch (SgException ex)
        {
            root.Log.Warn("the files are back, but svn was not told: " + ex.Message);
            result.Conflicted.Add("svn: " + ex.Message);
        }
    }

    /// <summary>Throws a shelf away. The commit stays in the store until git collects it, so this is not a shredder.</summary>
    public static void Drop(SgRoot root, string id)
    {
        var info = Read(root, id);
        root.Git.DeleteRef(info.RefName);
        root.Log.Info("dropped shelf " + info.Id);
    }

    // ---- the message a shelf is written in ----

    internal static string Message(ShelfInfo s)
    {
        var sb = new StringBuilder();
        sb.Append("shelf: ").Append(s.Title).Append("\n\n");
        sb.Append(s.Count).Append(s.Count == 1 ? " file" : " files").Append(" put aside from ")
          .Append(s.IsCheckout ? "checkout " + s.Checkout : "branch " + s.Branch).Append("\n\n");
        sb.Append(KeyVersion).Append(": ").Append(Version).Append('\n');
        sb.Append(KeyKind).Append(": ").Append(s.Kind).Append('\n');
        if (s.Checkout.Length > 0) sb.Append(KeyCheckout).Append(": ").Append(s.Checkout).Append('\n');
        if (s.Branch.Length > 0) sb.Append(KeyBranch).Append(": ").Append(s.Branch).Append('\n');
        sb.Append(KeyPath).Append(": ").Append(s.Path).Append('\n');
        sb.Append(KeyBase).Append(": ").Append(s.Base).Append('\n');
        sb.Append(KeyCreated).Append(": ").Append(s.Created.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var f in s.Files) sb.Append(KeyFile).Append(": ").Append(f.Code).Append(' ').Append(f.Path).Append('\n');
        foreach (var p in s.Staged) sb.Append(KeyStaged).Append(": ").Append(p).Append('\n');
        return sb.ToString();
    }

    internal static ShelfInfo Parse(string id, string sha, string body)
    {
        var s = new ShelfInfo { Id = id, Sha = sha };
        var first = true;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (first)
            {
                s.Title = line.StartsWith("shelf: ", StringComparison.Ordinal) ? line[7..].Trim() : line.Trim();
                first = false;
                continue;
            }
            // Only the trailers say anything. Everything else in the message is written for a reader.
            if (!line.StartsWith(KeyVersion, StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0) continue;
            var key = line[..colon];
            var value = line[(colon + 2)..].Trim();
            switch (key)
            {
                case KeyVersion:
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)) s.Version = version;
                    break;
                case KeyKind: s.Kind = value; break;
                case KeyCheckout: s.Checkout = value; break;
                case KeyBranch: s.Branch = value; break;
                case KeyPath: s.Path = value; break;
                case KeyBase: s.Base = value; break;
                case KeyCreated:
                    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)) s.Created = when;
                    break;
                case KeyFile:
                    var space = value.IndexOf(' ');
                    if (space > 0) s.Files.Add(new ShelfFile(value[(space + 1)..], value[..space]));
                    break;
                case KeyStaged: s.Staged.Add(value); break;
            }
        }
        if (s.Title.Length == 0) s.Title = "shelf";
        return s;
    }

    // ---- helpers ----

    /// <summary>The rows the caller picked: everything when it named nothing, and a folder means all of it.</summary>
    static List<T> Under<T>(IEnumerable<T> all, IEnumerable<string>? paths, Func<T, string> pathOf)
    {
        var list = all.ToList();
        var picked = (paths ?? Array.Empty<string>()).Select(PathUtil.Rel).Where(p => p.Length > 0).ToList();
        if (picked.Count == 0) return list;
        return list.Where(x => picked.Any(p => PathUtil.IsUnder(pathOf(x), p))).ToList();
    }

    static string SafeBase(SgRoot root, string branch)
    {
        try { return Ops.BaseCheckout(root, branch).Name; }
        catch (SgException) { return ""; }
    }

    static string Title(string? title)
    {
        var line = (title ?? "").Replace('\r', ' ').Split('\n')[0].Trim();
        return line.Length == 0 ? "shelf" : line;
    }

    /// <summary>
    /// An id no shelf has yet. The time is only good to the second, so two shelves saved in the same
    /// second under the same name would land on one ref and the first one's files would be unreachable,
    /// already gone from the working copy. Both callers hold the root lock, so what is free stays free.
    /// </summary>
    static string FreeId(SgRoot root, string title)
    {
        var id = NewId(title);
        if (root.Git.RefSha(RefPrefix + id) == null) return id;
        for (var n = 2; n < 1000; n++)
        {
            var next = id + "-" + n;
            if (root.Git.RefSha(RefPrefix + next) == null) return next;
        }
        throw new SgException("too many shelves already carry this name and second: " + id);
    }

    /// <summary>
    /// The paths one status row is about. A rename is two: the file where it is now, and the one it was,
    /// which git holds as a deletion. Anything that acts on the row has to act on both.
    /// </summary>
    static IEnumerable<string> BothPaths(StatusEntry e) =>
        e.OldPath != null ? new[] { e.OldPath, e.Path } : new[] { e.Path };

    /// <summary>
    /// The row carries an svn property change, on its own or beside a content change. Either way a shelf
    /// cannot hold it: git has no place for a property, and svn revert would take it with the text.
    /// </summary>
    static bool HasPropertyEdit(Ops.SvnChange c) => c.Item == "normal" || c.Props is "modified" or "conflicted";

    /// <summary>
    /// The id is the time, then as much of the title as reads as a name. The time first so a list sorts
    /// itself, and letters and digits only after it, because this ends up as the name of a git ref.
    /// </summary>
    static string NewId(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) && ch < 128) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 24) break;
        }
        var slug = sb.ToString().Trim('-');
        return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (slug.Length == 0 ? "" : "-" + slug);
    }

    /// <summary>A NUL byte near the front is what git itself calls binary. Nothing merges those line by line.</summary>
    static bool LooksBinary(string abs)
    {
        try
        {
            using var s = File.OpenRead(abs);
            var buffer = new byte[8000];
            var read = s.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch (IOException) { return false; }
    }

    static string Names(List<string> paths) =>
        string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? $" and {paths.Count - 5} more" : "");

    static void Delete(string abs)
    {
        if (File.Exists(abs)) File.Delete(abs);
        else if (Directory.Exists(abs)) Directory.Delete(abs, true);
    }
}
