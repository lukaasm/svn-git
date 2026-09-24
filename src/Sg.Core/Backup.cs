using System.Globalization;
using System.Text;

namespace Sg.Core;

/// <summary>What one commit of a thin history is, read off its trailers.</summary>
public enum ThinKind { None, Marker, Base, Change }

/// <summary>One commit of a thin history: its kind, the real commit it stands for, and its whole message.</summary>
public sealed record ThinCommit(string Sha, ThinKind Kind, string? Source, int Version, string Body, Author Author)
{
    /// <summary>The first line of the message the real commit had.</summary>
    public string Subject => Msg.Subject(Thin.Original(Body).Split('\n')[0]);
}

/// <summary>
/// A run of commits above a snapshot, rewritten so that nothing the run never touched is in it. The
/// snapshot becomes a marker with the empty tree and the SVN revisions in its message; each commit
/// keeps only the paths the run has written; and under a commit that first writes a path, a base
/// commit puts in the version that path started from. So the diff of a change commit against what is
/// under it is the real commit's diff, with the blob every hunk starts from present - and a three way
/// merge can put it back on a checkout that has moved on.
///
/// Every field of every commit is taken from the real one, so the same run makes the same objects
/// twice, and a backup pushed on Monday is what Tuesday's is compared against rather than sent again.
/// </summary>
public static class Thin
{
    public const int Version = 1;
    const string KeyKind = "sg-thin";
    const string KeySource = "sg-source";
    const string KeyVersion = "sg-backup";

    public static ThinCommit Read(Git git, string sha)
    {
        var who = git.IdentityOf(sha);
        return Parse(sha, who.Body, who.Author);
    }

    public static ThinCommit Parse(string sha, string body, Author author)
    {
        var kind = ThinKind.None;
        string? source = null;
        var version = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(KeyKind + ": ", StringComparison.Ordinal))
                kind = line[(KeyKind.Length + 2)..].Trim() switch
                {
                    "marker" => ThinKind.Marker,
                    "base" => ThinKind.Base,
                    "change" => ThinKind.Change,
                    _ => ThinKind.None,
                };
            else if (line.StartsWith(KeySource + ": ", StringComparison.Ordinal)) source = line[(KeySource.Length + 2)..].Trim();
            else if (line.StartsWith(KeyVersion + ": ", StringComparison.Ordinal))
                int.TryParse(line[(KeyVersion.Length + 2)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
        }
        return new ThinCommit(sha, kind, source, version, body, author);
    }

    /// <summary>The message a change commit came with, without the lines the rewrite put under it.</summary>
    public static string Original(string body)
    {
        var lines = body.Replace("\r", "").Split('\n').ToList();
        lines.RemoveAll(l => l.StartsWith(KeyKind + ": ", StringComparison.Ordinal) || l.StartsWith(KeySource + ": ", StringComparison.Ordinal));
        return string.Join("\n", lines).TrimEnd() + "\n";
    }

    /// <summary>The whole first-parent history under a thin tip, oldest first, in one git call.</summary>
    public static List<ThinCommit> Chain(Git git, string tip)
    {
        var r = git.Ok(null, "log", "--reverse", "--first-parent", "--format=%H%x1f%an%x1f%ae%x1f%aI%x1f%B%x1e", tip);
        var res = new List<ThinCommit>();
        foreach (var record in r.StdOut.Split('\x1e'))
        {
            var p = record.TrimStart('\n', '\r').Split('\x1f', 5);
            if (p.Length < 5 || p[0].Length == 0) continue;
            res.Add(Parse(p[0], p[4], new Author(p[1], p[2], p[3])));
        }
        return res;
    }

    /// <summary>
    /// The marker for a snapshot: the empty tree, and a message that is the snapshot's first line and
    /// its revision trailers. The SVN log messages the snapshot quotes stay behind. Its author and
    /// dates are the snapshot's, so every branch cut from one snapshot shares one marker.
    /// </summary>
    public static string Marker(Git git, string snapshotSha)
    {
        var who = git.IdentityOf(snapshotSha);
        var lines = who.Body.Replace("\r", "").Split('\n');
        var sb = new StringBuilder();
        sb.Append(lines.Length > 0 ? lines[0] : "snapshot").Append("\n\n");
        foreach (var line in lines)
            if (line.StartsWith("svn-rev: ", StringComparison.Ordinal) || line.StartsWith("svn-url: ", StringComparison.Ordinal)
                || line.StartsWith("svn-external: ", StringComparison.Ordinal)
                || line.StartsWith(SnapshotMeta.GitRev, StringComparison.Ordinal) || line.StartsWith(SnapshotMeta.GitUrl, StringComparison.Ordinal)
                || line.StartsWith(SnapshotMeta.GitCommit, StringComparison.Ordinal) || line.StartsWith(SnapshotMeta.GitSubmodule, StringComparison.Ordinal))
                sb.Append(line).Append('\n');
        sb.Append(KeyVersion).Append(": ").Append(Version).Append('\n');
        sb.Append(KeyKind).Append(": marker\n");
        sb.Append(KeySource).Append(": ").Append(snapshotSha).Append('\n');
        return git.CommitTreeExact(git.EmptyTree(), null, sb.ToString(), who);
    }

    /// <summary>The thin tip, how many commits it stands for, and the files too big to go, as "path (size)".</summary>
    public sealed record Result(string Tip, int Changes, bool FromScratch, List<string> LeftOut);

    /// <summary>
    /// The thin history of everything between a snapshot and a tip. From the marker when none of it was
    /// built before; from a thin tip already built when the commit it stands for is still on the run,
    /// so only what sits above it is rewritten. The candidates are tried in order: a wip or a shelf
    /// hands in the branch's own tip, whose history is its prefix.
    ///
    /// A file bigger than maxFileBytes, in either version, stays out of that commit whole: its base is
    /// not put in and its change is not either. So the change commit does not touch the path, and a
    /// restore leaves the file here as it is rather than reading its absence as a delete. What stayed
    /// out is named. The limit is decided by size alone, so the same run still makes the same objects.
    /// </summary>
    public static Result Rewrite(SgRoot root, string tip, string snapshot, IEnumerable<string?> builtTips, long maxFileBytes = 0)
    {
        var git = root.Git;
        string? thinParent = null;
        var from = snapshot;
        foreach (var built in builtTips)
        {
            if (built == null || !git.HasCommit(built)) continue;
            var b = Read(git, built);
            if (b.Kind is not (ThinKind.Marker or ThinKind.Change) || b.Source == null || !git.HasCommit(b.Source)) continue;
            if (!git.IsAncestor(snapshot, b.Source) || !git.IsAncestor(b.Source, tip)) continue;
            thinParent = built;
            from = b.Source;
            break;
        }
        var scratch = thinParent == null;
        thinParent ??= Marker(git, snapshot);

        var present = new HashSet<string>(git.LsTree(thinParent, null, recursive: true).Select(e => e.Path), StringComparer.Ordinal);
        var chain = git.RevListFirstParent(from + ".." + tip);
        var leftOut = new Dictionary<string, long>(StringComparer.Ordinal);
        var index = root.NewTempFile(".index");
        try
        {
            foreach (var c in chain)
            {
                var parent = git.ParentOf(c) ?? snapshot;
                var changed = git.DiffNameStatus(git.Store, parent, c, renames: false);
                var who = git.IdentityOf(c);

                // The version each path starts from, for the paths this history has not seen. A path the
                // commit adds has none, and one the run deleted earlier and adds again has none either.
                var needBase = changed.Where(e => e.Status != 'A' && !present.Contains(e.Path)).Select(e => e.Path).ToList();
                var bases = needBase.Count > 0 ? git.BlobsAt(parent, needBase) : new List<TreeEntry>();
                var adds = changed.Where(e => e.Status != 'D').Select(e => e.Path).ToList();
                var entries = git.BlobsAt(c, adds);
                if (maxFileBytes > 0)
                {
                    var sizes = git.ObjectSizes(bases.Concat(entries).Select(e => e.Sha));
                    var big = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var e in bases.Concat(entries))
                    {
                        var size = sizes.GetValueOrDefault(e.Sha);
                        if (size <= maxFileBytes) continue;
                        big.Add(e.Path);
                        leftOut[e.Path] = Math.Max(size, leftOut.GetValueOrDefault(e.Path));
                    }
                    bases = bases.Where(e => !big.Contains(e.Path)).ToList();
                    entries = entries.Where(e => !big.Contains(e.Path)).ToList();
                }
                if (bases.Count > 0)
                {
                    var tree = Compose(git, index, thinParent, bases, Array.Empty<string>());
                    var msg = $"sg base: {bases.Count} file(s) the next change starts from\n\n{KeyKind}: base\n{KeySource}: {c}\n";
                    thinParent = git.CommitTreeExact(tree, thinParent, msg, who);
                    foreach (var b in bases) present.Add(b.Path);
                }

                var dels = changed.Where(e => e.Status == 'D' && present.Contains(e.Path)).Select(e => e.Path).ToList();
                var t = Compose(git, index, thinParent, entries, dels);
                var body = who.Body.TrimEnd() + "\n\n" + KeyKind + ": change\n" + KeySource + ": " + c + "\n";
                thinParent = git.CommitTreeExact(t, thinParent, body, who);
                foreach (var e in entries) present.Add(e.Path);
                foreach (var d in dels) present.Remove(d);
            }
        }
        finally
        {
            try { File.Delete(index); } catch (IOException) { }
        }
        return new Result(thinParent, chain.Count, scratch,
            leftOut.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({DiskUsage.Human(kv.Value)})").ToList());
    }

    /// <summary>A tree that is another commit's tree with these blobs put in and these paths taken out, built in an index of its own.</summary>
    static string Compose(Git git, string index, string baseCommit, IEnumerable<TreeEntry> put, IEnumerable<string> remove)
    {
        git.ReadTree(git.Store, baseCommit, index);
        var entries = put.Select(e => (e.Mode, e.Sha, e.Path))
            .Concat(remove.Select(p => ("0", "0000000000000000000000000000000000000000", p)));
        git.UpdateIndexInfo(git.Store, entries, index);
        return git.WriteTree(git.Store, index);
    }
}

/// <summary>One thing a backup sends: a branch, the uncommitted changes of a folder, or a shelf.</summary>
public sealed class BackupItem
{
    /// <summary>"branch", "wip" or "shelf".</summary>
    public string Kind = "";
    public string Name = "";
    public string RemoteRef = "";
    /// <summary>The thin tip, once it was built.</summary>
    public string Thin = "";
    /// <summary>Commits above the snapshot. A branch with none sends its marker alone.</summary>
    public int Commits;
    /// <summary>"pushed", "up to date", "would push", "would reconcile", "behind", "rejected" or "failed".</summary>
    public string State = "";
    /// <summary>It went up over an older copy another machine had left there.</summary>
    public bool Reconciled;
    public string? Why;
    /// <summary>Files too big for the backup that stayed here, as "path (size)". The rest of the item went without them.</summary>
    public List<string> LeftOut = new();

    public bool Pushed => State == "pushed";
    public bool Rejected => State == "rejected";
    public bool Failed => State == "failed";
    /// <summary>The remote holds a newer version than this one, so backing up would send an older copy. Restore brings it here.</summary>
    public bool Behind => State == "behind";
}

public sealed class BackupResult
{
    public string Url = "";
    /// <summary>Null for a complete backup, otherwise the one worktree this result covers.</summary>
    public string? Worktree;
    /// <summary>When it ran. Set on a run that is kept as the last one; null on a check.</summary>
    public DateTimeOffset? When;
    /// <summary>Why the run as a whole stopped - the remote could not be reached, say. Null when it ran.</summary>
    public string? Error;
    public List<BackupItem> Items = new();
    /// <summary>Refs on the remote under sg's names that nothing here answers to any more. Prune deletes them.</summary>
    public List<string> RemoteOnly = new();
    public int Pushed => Items.Count(i => i.Pushed);
    public int Rejected => Items.Count(i => i.Rejected);
    /// <summary>Names the remote holds a newer version of than here: a restore, not a push, is the move.</summary>
    public int Behind => Items.Count(i => i.Behind);
    public bool Ok => Error == null && Items.All(i => !i.Rejected && !i.Failed);
}

/// <summary>What the remote holds under one name: a branch with what it was cut from, a shelf, or a folder's uncommitted changes.</summary>
public sealed class BackupEntry
{
    public string Kind = "";
    public string Name = "";
    public string Sha = "";
    /// <summary>The checkout here that the marker's URL names, or "".</summary>
    public string Checkout = "";
    public string Url = "";
    public long Revision;
    /// <summary>The server commit the marker names, for a branch cut from a git checkout. Empty for SVN.</summary>
    public string Commit = "";
    public List<ExportWc> Bases = new();
    public int Commits;
    public List<string> Subjects = new();
    public List<ExportDrift> Drift = new();
    /// <summary>The branch a shelf or a wip belongs to.</summary>
    public string Branch = "";
    public string Title = "";
    /// <summary>A branch of this name, a shelf of this id, is already here.</summary>
    public bool ExistsHere;
    public bool HasWip;
    public bool HasReview;
    public bool HasAppearance;
    public DateTimeOffset? Last;
    /// <summary>Why it cannot be read, when it cannot: written by a newer sg, or not an sg backup at all.</summary>
    public string? Unreadable;
    /// <summary>The worktree it belongs to is left out of the backup here, so no backup sends over this or reads it, and prune lists it.</summary>
    public bool Excluded;
}

public sealed class RestoreResult
{
    public string Name = "";
    public string Branch = "";
    public string Path = "";
    public string Checkout = "";
    public int Commits;
    public int Applied;
    /// <summary>The store still had the real commits, so the branch was pointed at them and nothing was replayed.</summary>
    public bool Relinked;
    /// <summary>Git retained the series for Continue, Skip, or Abort.</summary>
    public bool Waiting;
    public List<ExportDrift> Drift = new();
    public string? Stopped;
    public List<string> Conflicted = new();
    public string? Why;
    /// <summary>A branch of this name was here already and force wrote over it.</summary>
    public bool Replaced;
    public string? RecoveryBranch;
    public string? RecoveryPath;
    /// <summary>The shelf the uncommitted changes came back as, when they came back.</summary>
    public string? WipShelf;
    /// <summary>And they were written into the folder, so the shelf is gone again.</summary>
    public bool WipWritten;
    public List<string> WipConflicted = new();
    /// <summary>A pull found the uncommitted changes of the copy in the folder already, so nothing was written or shelved.</summary>
    public bool WipAlreadyHere;
    /// <summary>Why a pull put the uncommitted changes on a shelf rather than into the folder: what differs from the folder's own.</summary>
    public string? WipWhy;
    /// <summary>Shelves of the branch made again here, by id.</summary>
    public List<string> Shelves = new();
    public int ReviewThreads;
    public bool CheckoutAppearanceRestored;
    public string? CheckoutAppearanceWarning;
    public bool Ok => Stopped == null;
}

/// <summary>
/// A second copy of everything this machine has that SVN does not - the branches, the changes not yet
/// committed, the shelves - in a git repository somewhere else, as thin histories. Push mirrors with a
/// lease and never deletes; restore puts a branch back the way import does, through a three way merge
/// onto whatever revision the checkout here is at.
/// </summary>
public static partial class Backup
{
    /// <summary>What this root last pushed, per ref: read for the lease and to continue a rewrite.</summary>
    public const string PushedPrefix = "refs/sg/backup/";
    /// <summary>What the remote was last seen to hold, kept apart so a look does not weaken the lease.</summary>
    public const string FetchedPrefix = "refs/sg/fetched/";
    const string RemoteWip = "refs/sg/wip/";
    /// <summary>
    /// A checkout's local edits, apart from a worktree's uncommitted changes. One namespace for both
    /// would have let a branch that carries a checkout's name write over it - or, named "mono/x" beside
    /// a checkout "mono", fail outright, because git cannot hold a ref and a folder of the same name.
    /// </summary>
    const string RemoteEdits = "refs/sg/edits/";
    const string RemoteShelf = "refs/sg/shelf/";

    public static BackupConfig Require(SgRoot root) =>
        root.Config.Backup is { Url.Length: > 0 } b ? b : throw new SgException("no backup URL is set. Run: sg backup set <url>");

    public static bool Configured(SgRoot root) => root.Config.Backup is { Url.Length: > 0 };

    static string Prefix(BackupConfig b)
    {
        var p = b.Prefix.Trim().Trim('/');
        return p.Length > 0 ? p + "/" : "";
    }

    public static string RemoteRef(BackupConfig b, string kind, string name) => kind switch
    {
        "branch" => "refs/heads/" + Prefix(b) + name,
        "wip" => RemoteWip + Prefix(b) + name,
        "edits" => RemoteEdits + Prefix(b) + name,
        "review" => "refs/sg/review/" + Prefix(b) + name,
        "appearance" => "refs/sg/appearance/" + Prefix(b) + name,
        _ => RemoteShelf + Prefix(b) + name,
    };

    /// <summary>The kind and the name a ref on the remote stands for, or null when it is not one of sg's.</summary>
    public static (string Kind, string Name)? Owned(BackupConfig b, string remoteRef)
    {
        var p = Prefix(b);
        foreach (var (kind, head) in new[] { ("branch", "refs/heads/" + p), ("wip", RemoteWip + p), ("edits", RemoteEdits + p), ("shelf", RemoteShelf + p), ("review", "refs/sg/review/" + p), ("appearance", "refs/sg/appearance/" + p) })
            if (remoteRef.StartsWith(head, StringComparison.Ordinal) && remoteRef.Length > head.Length)
                return (kind, remoteRef[head.Length..]);
        return null;
    }

    static string Ns(string kind) => kind == "branch" ? "heads" : kind;
    public static string PushedRef(string kind, string name) => PushedPrefix + Ns(kind) + "/" + name;
    public static string FetchedRef(string kind, string name) => FetchedPrefix + Ns(kind) + "/" + name;
    static string KeyBackedUp(string branch) => $"branch.{branch}.sgBackedUp";
    /// <summary>The branch config key that holds why the last backup did not send a branch's work.</summary>
    public const string FailedKey = "sgBackupFailed";
    static string KeyFailed(string branch) => $"branch.{branch}.{FailedKey}";
    /// <summary>The branch config key that says the remote holds a newer or a different copy of a branch, with the reason, until that clears.</summary>
    public const string RemoteKey = "sgBackupRemote";
    static string KeyRemote(string branch) => $"branch.{branch}.{RemoteKey}";

    /// <summary>Where backups go. The URL is tried before it is kept, so a typo is found now and not on the timer.</summary>
    public static BackupConfig Set(SgRoot root, string url, string? prefix = null, bool? uncommitted = null, int? maxFileMb = null, int? maxPushMb = null)
    {
        using var operation = root.Lock();
        var b = root.Config.Backup ?? new BackupConfig();
        var u = url.Trim();
        if (u.Length == 0) throw new SgException("give the URL of a git repository: a bare folder, a share, or a hosted repository.");
        root.Git.LsRemote(u);
        b.Url = u;
        if (prefix != null) b.Prefix = prefix.Trim().Trim('/');
        if (uncommitted != null) b.Uncommitted = uncommitted.Value;
        if (maxFileMb != null) b.MaxFileMb = Math.Max(0, maxFileMb.Value);
        if (maxPushMb != null) b.MaxPushMb = Math.Max(0, maxPushMb.Value);
        root.Config.Backup = b;
        root.Save();
        return b;
    }

    public static void Clear(SgRoot root)
    {
        root.Config.Backup = null;
        root.Save();
    }

    /// <summary>
    /// One worktree left out of the backup, or put back in. Nothing of a worktree left out goes: not the
    /// branch, not its uncommitted changes, not its shelves. What the remote already holds of it is not
    /// touched either - not sent over, not read, not deleted as stale - and prune lists it as answering to
    /// nothing here. The name has to be a branch here, so a typo is refused now and not found on the timer.
    /// </summary>
    public static BackupConfig Exclude(SgRoot root, string branch, bool exclude = true)
    {
        var b = root.Config.Backup ?? new BackupConfig();
        var name = branch.Trim();
        if (name.Length == 0) throw new SgException("name the branch of the worktree, for example: sg backup exclude big-assets");
        if (exclude)
        {
            var git = root.Git;
            if (git.RefSha("refs/heads/" + name) == null) throw new SgException($"no branch named {name} here");
            if (!b.Excluded.Contains(name, StringComparer.Ordinal)) b.Excluded.Add(name);
            // The last backup's verdict on the branch stays on it for the card until a backup replaces it,
            // and no backup will: so it goes now, or the card would say "backup failed" over a branch that does not go.
            if (git.BranchConfig(FailedKey).ContainsKey(name)) git.ConfigUnset(KeyFailed(name));
            if (git.BranchConfig(RemoteKey).ContainsKey(name)) git.ConfigUnset(KeyRemote(name));
        }
        else b.Excluded.RemoveAll(n => n == name);
        root.Config.Backup = b;
        root.Save();
        return b;
    }

    /// <summary>Whether the worktree of this branch is left out of the backup.</summary>
    public static bool IsExcluded(BackupConfig? b, string branch) => b != null && b.Excluded.Contains(branch, StringComparer.Ordinal);

    // ---- pushing ----

    /// <summary>
    /// Every branch, every folder's uncommitted changes, every shelf: rewritten thin, compared with what
    /// the remote holds, and pushed under a lease unless check only asks. One branch the remote holds
    /// another version of is skipped and named; the rest still go.
    /// </summary>
    /// <remarks>worktree scopes the run to one branch, its enabled saved edits, and its shelves.
    /// force with only writes over just the items it names - by name, kind/name, or remote ref - and the rest in scope reconcile as usual.</remarks>
    public static BackupResult Run(SgRoot root, bool check = false, bool force = false, IReadOnlyCollection<string>? only = null, string? worktree = null)
    {
        var cfg = Require(root);
        // Keep the result and its success receipt in the same operation, including CLI runs.
        using var operation = root.Lock();
        if (worktree != null && string.IsNullOrWhiteSpace(worktree)) throw new SgException("Choose a worktree to back up.");
        if (check)
        {
            var preview = RunOnce(root, check: true, force, only, worktree);
            BackUpReviews(root, preview, check: true);
            BackUpAppearance(root, preview, check: true);
            return preview;
        }
        try
        {
            var res = RunOnce(root, check: false, force, only, worktree);
            BackUpReviews(root, res, check: false);
            BackUpAppearance(root, res, check: false);
            res.When = DateTimeOffset.Now;
            KeepLast(root, res);
            if (worktree == null && res.Error == null && res.Items.All(i => i.State is "pushed" or "up to date" && i.LeftOut.Count == 0))
                KeepSuccess(root, new(cfg.Url, cfg.Prefix, res.When.Value));
            return res;
        }
        catch (SgException e)
        {
            KeepLast(root, new BackupResult { Url = root.Config.Backup?.Url ?? "", Worktree = worktree, When = DateTimeOffset.Now, Error = e.Message });
            throw;
        }
    }

    static readonly System.Text.Json.JsonSerializerOptions LastJson = new(SgConfig.JsonOptions) { IncludeFields = true };

    /// <summary>Where the last run's result is kept, so every window can say how the timer's backup went, not only the one that ran it.</summary>
    static string LastPath(SgRoot root) => Path.Combine(root.StorePath, "backup-last.json");

    sealed record SuccessReceipt(string Url, string Prefix, DateTimeOffset When);
    static string SuccessPath(SgRoot root) => Path.Combine(root.StorePath, "backup-success.json");

    /// <summary>
    /// Last complete successful run for this destination. Checks, failures, newer remote work and
    /// omitted files do not replace it. Older versions did not keep a success receipt.
    /// </summary>
    public static DateTimeOffset? LastSuccess(SgRoot root)
    {
        if (root.Config.Backup is not { } cfg) return null;
        try
        {
            var receipt = System.Text.Json.JsonSerializer.Deserialize<SuccessReceipt>(File.ReadAllText(SuccessPath(root)), LastJson);
            return receipt?.Url == cfg.Url && receipt.Prefix == cfg.Prefix ? receipt.When : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    static void KeepSuccess(SgRoot root, SuccessReceipt receipt)
    {
        try { AtomicFile.WriteAllText(SuccessPath(root), System.Text.Json.JsonSerializer.Serialize(receipt, LastJson)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { root.Log.Warn("the successful backup time could not be kept: " + e.Message); }
    }

    /// <summary>How the last backup went, or null when none has run here or the file cannot be read.</summary>
    public static BackupResult? Last(SgRoot root)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<BackupResult>(File.ReadAllText(LastPath(root)), LastJson); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    static void KeepLast(SgRoot root, BackupResult res)
    {
        var path = LastPath(root);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(res, LastJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { root.Log.Warn("the backup result could not be kept: " + e.Message); }
    }

    static BackupResult RunOnce(SgRoot root, bool check, bool force, IReadOnlyCollection<string>? only, string? worktree)
    {
        var cfg = Require(root);
        var git = root.Git;
        using var _ = root.Lock();
        var res = new BackupResult { Url = cfg.Url, Worktree = worktree };
        var remote = git.LsRemote(cfg.Url);
        var pending = new List<(BackupItem Item, string? Lease)>();

        var sources = Sources(root, cfg, worktree);
        if (worktree != null && !sources.Any(s => s.Kind == "branch"))
            throw new SgException($"{worktree} has no eligible local worktree to back up. Check its folder and backup exclusion setting.");

        // Uncommitted changes on the remote for a folder that has none here now: this root's old ones, since
        // committed or dropped, or another machine's work in progress. The loop after the items tells which.
        var sourceRefs = sources.Select(s => RemoteRef(cfg, s.Kind, s.Name)).ToHashSet(StringComparer.Ordinal);
        var here = LocalNames(root, cfg);
        var clean = new Dictionary<string, (string Kind, string Name)>(StringComparer.Ordinal);
        foreach (var r in remote.Keys)
            if (!sourceRefs.Contains(r) && Owned(cfg, r) is { } o
                && (worktree == null || o.Kind == "wip" && o.Name == worktree)
                && ((o.Kind == "wip" && here.Branches.Contains(o.Name)) || (o.Kind == "edits" && here.Checkouts.Contains(o.Name))))
                clean[r] = o;

        // The refs the remote holds a version of that this root did not push last - another machine, or
        // another root. Fetched in one call so what they hold can be read, and reconciled by it below.
        var foreign = sources
            .Where(s => s.Why == null)
            .Select(s => (S: s, Ref: RemoteRef(cfg, s.Kind, s.Name)))
            .Where(x => remote.TryGetValue(x.Ref, out var have) && have != git.RefSha(PushedRef(x.S.Kind, x.S.Name)))
            .Select(x => "+" + x.Ref + ":" + FetchedRef(x.S.Kind, x.S.Name))
            .Concat(clean.Where(c => remote[c.Key] != git.RefSha(PushedRef(c.Value.Kind, c.Value.Name)))
                .Select(c => "+" + c.Key + ":" + FetchedRef(c.Value.Kind, c.Value.Name)))
            .ToList();
        if (foreign.Count > 0) git.FetchRefs(cfg.Url, foreign);

        foreach (var s in sources)
        {
            var item = new BackupItem { Kind = s.Kind, Name = s.Name, RemoteRef = RemoteRef(cfg, s.Kind, s.Name), LeftOut = s.LeftOut };
            res.Items.Add(item);
            // Refused before a commit was made of it: uncommitted changes too big for any push. What the
            // remote holds under its name stays there, since it is not stale, only not replaced.
            if (s.Why != null)
            {
                item.State = "failed";
                item.Why = s.Why;
                continue;
            }
            var pushedRef = PushedRef(s.Kind, s.Name);
            var last = git.RefSha(pushedRef);
            try
            {
                var thin = Thin.Rewrite(root, s.Tip, s.Snapshot, [last, s.Branch != null ? git.RefSha(PushedRef("branch", s.Branch)) : null], cfg.MaxFileBytes);
                item.Thin = thin.Tip;
                item.Commits = git.CountCommits(s.Snapshot, s.Tip);
                item.LeftOut = item.LeftOut.Concat(thin.LeftOut).Distinct(StringComparer.Ordinal).ToList();
            }
            catch (SgException e)
            {
                item.State = "failed";
                item.Why = e.Message;
                continue;
            }
            remote.TryGetValue(item.RemoteRef, out var have);
            if (have == item.Thin)
            {
                item.State = "up to date";
                if (last != item.Thin) git.UpdateRef(pushedRef, item.Thin);
                if (!check && s.Kind == "branch") git.Config(KeyBackedUp(s.Name), Now());
                continue;
            }

            // Nothing there, or the version this root last pushed: a plain push under the lease. A forced item
            // goes the same way. Every lease is what was just read, so only the copy looked at is written over.
            var forced = force && (only == null || only.Any(n => n == s.Name || n == s.Kind + "/" + s.Name || n == item.RemoteRef));
            if (have == null || have == last || forced)
            {
                item.State = check ? "would push" : "pending";
                pending.Add((item, have));
                continue;
            }
            var (verdict, detail) = Reconcile(git, s, have, item.Thin);
            switch (verdict)
            {
                case Verdict.Same:
                    // The work the remote holds, under other hashes: sent by another machine, or by this one before
                    // a rebase or a restore made new commits of it. Nothing goes, and this side counts as backed up.
                    item.State = "up to date";
                    if (last != item.Thin) git.UpdateRef(pushedRef, item.Thin);
                    if (!check && s.Kind == "branch") git.Config(KeyBackedUp(s.Name), Now());
                    break;
                case Verdict.Ahead:
                    // The remote's copy is older than what is here, so the push carries it forward. The lease is
                    // what was just read, so a race between the look and the push still rejects.
                    item.Reconciled = true;
                    item.State = check ? "would reconcile" : "pending";
                    pending.Add((item, have));
                    break;
                case Verdict.Behind:
                    item.State = "behind";
                    item.Why = detail == null
                        ? $"the backup holds a newer version of this than the one here. Backing up would send an older copy over it; pull brings it here: sg backup pull {s.Name}"
                        : s.Kind == "branch" ? detail
                        : $"{detail}. Backing up would send the older changes over them; pull brings them here: sg backup pull {s.Name}";
                    break;
                default:
                    item.State = "rejected";
                    item.Why = (detail ?? "the remote holds a version of this that did not come from here, and neither is an ancestor of the other")
                               + " - different work under one name. "
                               + (s.Kind == "branch" ? $"Pull puts the commits only the remote has on top of this branch (sg backup pull {s.Name}), "
                                                       + $"or restore the remote's under another name to compare (sg backup restore {s.Name} --name {s.Name}-remote)"
                                  : s.Kind is "wip" or "edits" ? $"Pull puts the remote's on a shelf beside these (sg backup pull {s.Name})"
                                  : "Restore it to compare")
                               + $", or keep this machine's: sg backup --force --only {s.Kind}/{s.Name}";
                    break;
            }
        }

        // Uncommitted changes on the remote for a folder that is clean here. This root's own, sent before the
        // folder was committed or cleaned, go with this push. Another machine's stay: they go only when this
        // folder already holds every file they wrote, and otherwise the folder is behind them. A clean branch
        // here once deleted a laptop's work in progress this way, as if it had been committed.
        var mine = res.Items.Select(i => i.RemoteRef).ToHashSet(StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var r in remote.Keys)
        {
            if (mine.Contains(r) || Owned(cfg, r) is not { } o || o.Kind is "review" or "appearance") continue;
            if (worktree != null && !(o.Kind is "branch" or "wip" && o.Name == worktree)) continue;
            if (!clean.ContainsKey(r))
            {
                res.RemoteOnly.Add(r);
                continue;
            }
            if (remote[r] == git.RefSha(PushedRef(o.Kind, o.Name)))
            {
                stale.Add(r);
                continue;
            }
            var (verdict, why) = (Verdict.Diverged, (string?)null);
            try
            {
                var folder = o.Kind == "wip" ? git.RefSha("refs/heads/" + o.Name)! : git.HeadSha(root.Checkout(o.Name).Path);
                (verdict, why) = ReconcileChanges(git, folder, Thin.Chain(git, git.RefSha(FetchedRef(o.Kind, o.Name))!));
            }
            catch (SgException e) { why = e.Message; }
            if (verdict is Verdict.Same or Verdict.Ahead)
            {
                stale.Add(r);
                continue;
            }
            res.Items.Add(new BackupItem
            {
                Kind = o.Kind,
                Name = o.Name,
                RemoteRef = r,
                State = "behind",
                Why = (why ?? "the remote holds uncommitted changes another machine sent") + $". This folder has none; pull brings them here: sg backup pull {o.Name}",
            });
        }
        res.RemoteOnly.Sort(StringComparer.Ordinal);

        // What each push would carry, against everything the remote holds that this store has too. A host
        // drops a push past its size with a bare HTTP 500 and no answer per ref, and that once failed every
        // branch for one folder of build output. So one thing too big on its own stays here, named, and
        // the rest go in as many pushes as it takes to keep each one under the limit.
        var haves = git.ObjectSizes(remote.Values).Keys.ToList();
        var sized = new List<(BackupItem Item, string Lease, long Bytes)>();
        foreach (var (item, lease) in pending)
        {
            var bytes = git.DiskUsage(item.Thin, haves);
            if (cfg.MaxPushBytes > 0 && bytes > cfg.MaxPushBytes)
            {
                item.State = "failed";
                item.Why = $"{DiskUsage.Human(bytes)} to send, more than the {DiskUsage.Human(cfg.MaxPushBytes)} one backup push carries. "
                           + "Raise the limit with: sg backup set <url> --max-push <MB>";
                continue;
            }
            sized.Add((item, lease ?? "", bytes));
        }
        if (check) return res;

        var batches = new List<List<(BackupItem Item, string Lease, long Bytes)>> { new() };
        long used = 0;
        foreach (var p in sized)
        {
            if (cfg.MaxPushBytes > 0 && batches[^1].Count > 0 && used + p.Bytes > cfg.MaxPushBytes)
            {
                batches.Add(new());
                used = 0;
            }
            batches[^1].Add(p);
            used += p.Bytes;
        }
        for (var n = 0; n < batches.Count; n++)
        {
            var batch = batches[n];
            var pushes = batch.Select(p => new PushRef(p.Item.Thin, p.Item.RemoteRef, p.Lease)).ToList();
            // A stale wip is a delete and carries nothing, so it rides with the first push.
            if (n == 0) pushes.AddRange(stale.Select(r => new PushRef(null, r, remote[r])));
            if (pushes.Count == 0) continue;
            Dictionary<string, PushRefResult> answers;
            // Never a blind --force: a forced item carries the lease of what was just read, like every other.
            try { answers = git.PushRefs(cfg.Url, pushes, force: false); }
            catch (SgException e)
            {
                // Nothing per ref came back, so every ref of this push gets the one reason, and the size it was.
                var why = $"{e.Message} ({DiskUsage.Human(batch.Sum(p => p.Bytes))} in this push)";
                answers = pushes.ToDictionary(p => p.Dst, _ => new PushRefResult(false, '!', why), StringComparer.Ordinal);
            }
            foreach (var (item, _, _) in batch)
            {
                if (answers.TryGetValue(item.RemoteRef, out var a) && a.Ok)
                {
                    item.State = "pushed";
                    git.UpdateRef(PushedRef(item.Kind, item.Name), item.Thin);
                    if (item.Kind == "branch") git.Config(KeyBackedUp(item.Name), Now());
                    continue;
                }
                var summary = a?.Summary ?? "";
                var lease = summary.Contains("stale info", StringComparison.Ordinal) || summary.Contains("fetch first", StringComparison.Ordinal);
                item.State = lease ? "rejected" : "failed";
                item.Why = lease
                    ? "the remote holds a version of this that did not come from here - another root, or another machine. "
                      + "Restore it first if it is the newer one, back up under a prefix, or --force to write over it."
                    : summary.Length > 0 ? summary : "the push did not answer for this ref";
            }
            if (n == 0)
                foreach (var r in stale)
                    if (answers.TryGetValue(r, out var a) && a.Ok && Owned(cfg, r) is { } o) git.DeleteRef(PushedRef(o.Kind, o.Name));
        }
        RememberFailures(git, res);
        return res;
    }

    /// <summary>
    /// What the last backup could not send, per branch, where the overview reads it. A branch whose commits
    /// or uncommitted changes failed keeps the reason until a backup sends them, so its badge cannot say
    /// "backed up" over work that never left. A check writes nothing.
    /// </summary>
    static void RememberFailures(Git git, BackupResult res)
    {
        var before = git.BranchConfig(FailedKey);
        var remote = git.BranchConfig(RemoteKey);
        foreach (var name in res.Items.Where(i => i.Kind is "branch" or "wip").Select(i => i.Name).Distinct(StringComparer.Ordinal))
        {
            var failed = res.Items.FirstOrDefault(i => i.Name == name && (i.Kind is "branch" or "wip") && i.Failed);
            if (failed != null)
            {
                var why = (failed.Kind == "wip" ? "uncommitted changes: " : "") + (failed.Why ?? "the push did not answer").Split('\n')[0];
                if (before.GetValueOrDefault(name) != why) git.Config(KeyFailed(name), why);
            }
            else if (before.ContainsKey(name)) git.ConfigUnset(KeyFailed(name));

            // What the remote holds that this machine does not, until a backup sends over it or a pull
            // takes it: the card on the overview offers the pull from this, so it has to outlive the toast.
            var other = res.Items.FirstOrDefault(i => i.Name == name && (i.Kind is "branch" or "wip") && (i.Behind || i.Rejected));
            if (other != null)
            {
                var note = (other.Behind ? "newer" : "differs") + ": " + (other.Kind == "wip" ? "uncommitted changes: " : "")
                           + (other.Why ?? "").Split('\n')[0];
                if (remote.GetValueOrDefault(name) != note) git.Config(KeyRemote(name), note);
            }
            else if (remote.ContainsKey(name)) git.ConfigUnset(KeyRemote(name));
        }
    }

    static string Now() => DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);


    /// <summary>One thing to back up. Why is set when it was refused before a commit could be made of it.</summary>
    sealed record Source(string Kind, string Name, string Tip, string Snapshot, string? Branch, List<string> LeftOut, string? Why = null);

    /// <summary>Everything a backup is made of, with the snapshot each one sits on.</summary>
    static List<Source> Sources(SgRoot root, BackupConfig cfg, string? worktree = null)
    {
        var git = root.Git;
        var list = new List<Source>();
        var coPaths = root.Config.Checkouts.Select(c => c.Path.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bases = git.BranchBases();
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var co in root.Config.Checkouts)
            if (git.RefSha(root.SnapshotRef(co)) is { } sha) snapshots[co.Name] = sha;

        foreach (var w in git.WorktreeList())
        {
            if (w.Bare || coPaths.Contains(w.Path.TrimEnd('\\', '/'))) continue;
            var branch = w.Branch;
            if (branch == null || IsExcluded(cfg, branch) || worktree != null && branch != worktree) continue;
            if (!bases.TryGetValue(branch, out var coName) || !snapshots.TryGetValue(coName, out var snap)) continue;
            var tip = git.RefSha("refs/heads/" + branch);
            if (tip == null) continue;
            var mb = git.MergeBase(tip, snap);
            if (mb == null)
            {
                root.Log.Warn($"{branch} shares no history with svn/{coName}, so it is not backed up");
                continue;
            }
            list.Add(new Source("branch", branch, tip, mb, null, []));
            if (!cfg.Uncommitted || !Directory.Exists(w.Path)) continue;
            // Refused changes still make an item, failed and named: a line in the log alone left the branch
            // looking backed up while its uncommitted work never went.
            try
            {
                var wip = Shelf.Wip(root, w.Path, cfg.MaxFileBytes, cfg.MaxPushBytes);
                if (wip != null) list.Add(new Source("wip", branch, wip.Sha, mb, branch, wip.LeftOut));
            }
            catch (SgException e)
            {
                root.Log.Warn($"{branch}: the uncommitted changes are not in the backup: {e.Message}");
                list.Add(new Source("wip", branch, "", mb, branch, [], e.Message));
            }
        }

        if (cfg.Uncommitted && worktree == null)
            foreach (var co in root.Config.Checkouts)
            {
                if (!snapshots.TryGetValue(co.Name, out var snap) || !Directory.Exists(co.Path)) continue;
                try
                {
                    var wip = Shelf.Wip(root, co.Path, cfg.MaxFileBytes, cfg.MaxPushBytes);
                    if (wip != null) list.Add(new Source("edits", co.Name, wip.Sha, snap, null, wip.LeftOut));
                }
                catch (SgException e)
                {
                    root.Log.Warn($"{co.Name}: the local edits are not in the backup: {e.Message}");
                    list.Add(new Source("edits", co.Name, "", snap, null, [], e.Message));
                }
            }

        foreach (var s in Shelf.List(root))
        {
            if (worktree != null && (s.IsCheckout || s.Branch != worktree)) continue;
            // A shelf of a worktree left out is that worktree's work, put aside: it stays with the rest.
            if (!s.IsCheckout && IsExcluded(cfg, s.Branch)) continue;
            string? mb = null;
            if (s.Checkout.Length > 0 && snapshots.TryGetValue(s.Checkout, out var snap)) mb = git.MergeBase(s.Sha, snap);
            if (mb == null)
                foreach (var other in snapshots.Values)
                    if ((mb = git.MergeBase(s.Sha, other)) != null) break;
            if (mb == null)
            {
                root.Log.Warn($"shelf {s.Id} sits on no snapshot here, so it is not backed up");
                continue;
            }
            list.Add(new Source("shelf", s.Id, s.Sha, mb, s.IsCheckout ? null : s.Branch, []));
        }
        return list;
    }

    sealed record Local(HashSet<string> Branches, HashSet<string> Checkouts, HashSet<string> Shelves);

    /// <summary>
    /// What is here, by name. With a config, only what its backup answers for: a worktree left out, and its
    /// shelves, are not here as far as the remote is concerned, so what it holds of them is remote only.
    /// </summary>
    static Local LocalNames(SgRoot root, BackupConfig? sent = null)
    {
        var git = root.Git;
        var branches = git.RefIndex("refs/heads/").Keys.Select(k => k["refs/heads/".Length..]).Where(b => !IsExcluded(sent, b)).ToHashSet(StringComparer.Ordinal);
        var checkouts = root.Config.Checkouts.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var shelves = Shelf.List(root).Where(s => s.IsCheckout || !IsExcluded(sent, s.Branch)).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return new Local(branches, checkouts, shelves);
    }

    // ---- what is there ----

    /// <summary>Fetches every ref of sg's on the remote and says what each one is. Nothing here is written but the fetched refs.</summary>
    public static List<BackupEntry> List(SgRoot root)
    {
        var cfg = Require(root);
        var git = root.Git;
        var remote = git.LsRemote(cfg.Url);
        var owned = remote.Where(kv => Owned(cfg, kv.Key) is { Kind: not ("review" or "appearance") }).ToList();
        git.FetchRefs(cfg.Url, owned.Select(kv => { var o = Owned(cfg, kv.Key)!.Value; return "+" + kv.Key + ":" + FetchedRef(o.Kind, o.Name); }));

        var here = LocalNames(root);
        var wips = owned.Select(kv => Owned(cfg, kv.Key)!.Value).Where(o => o.Kind == "wip").Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var res = new List<BackupEntry>();
        foreach (var kv in owned)
        {
            var (kind, name) = Owned(cfg, kv.Key)!.Value;
            var e = new BackupEntry { Kind = kind, Name = name, Sha = kv.Value,
                HasReview = kind == "branch" && remote.ContainsKey(RemoteRef(cfg, "review", name)),
                HasAppearance = kind == "branch" && remote.ContainsKey(RemoteRef(cfg, "appearance", name)) };
            res.Add(e);
            ReadEntry(root, cfg, here, wips, e);
        }
        return res.OrderBy(e => e.Kind switch { "branch" => 0, "wip" => 1, "edits" => 2, _ => 3 }).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void ReadEntry(SgRoot root, BackupConfig cfg, Local here, HashSet<string> wips, BackupEntry e)
    {
        var git = root.Git;
        var kind = e.Kind; var name = e.Name;
        List<ThinCommit> chain;
        try { chain = Thin.Chain(git, e.Sha); }
        catch (SgException ex) { e.Unreadable = ex.Message; return; }
        if (chain.Count == 0 || chain[0].Kind != ThinKind.Marker)
        {
            e.Unreadable = "not an sg backup: it does not start with a marker";
            return;
        }
        if (chain[0].Version > Thin.Version)
        {
            e.Unreadable = $"written by a newer sg (format {chain[0].Version}, this one reads {Thin.Version}). Update sg.";
            return;
        }
        var meta = MetaOf(name, chain[0].Body);
        e.Url = meta.Root?.Url ?? "";
        e.Revision = meta.Root?.Revision ?? 0;
        e.Commit = meta.Root?.Commit ?? "";
        e.Bases = meta.Bases;
        var changes = chain.Where(c => c.Kind == ThinKind.Change).ToList();
        e.Commits = changes.Count;
        e.Subjects = changes.Select(c => c.Subject).ToList();
        var last = chain[^1];
        if (DateTimeOffset.TryParse(last.Author.Date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)) e.Last = when;
        var co = Export.MatchCheckout(root, meta);
        if (co != null)
        {
            e.Checkout = co.Name;
            e.Drift = Export.DriftOf(root, meta, co);
        }
        switch (kind)
        {
            case "branch":
                e.Branch = name;
                e.ExistsHere = here.Branches.Contains(name);
                e.HasWip = wips.Contains(name);
                e.Excluded = IsExcluded(cfg, name);
                break;
            case "wip":
                e.Branch = name;
                e.ExistsHere = here.Branches.Contains(name);
                e.Title = "uncommitted changes";
                e.Excluded = IsExcluded(cfg, name);
                break;
            case "edits":
                e.Branch = name;
                e.ExistsHere = here.Checkouts.Contains(name);
                e.Title = "local edits";
                break;
            default:
                var s = Shelf.Parse(name, last.Sha, last.Body);
                e.Branch = s.IsCheckout ? "" : s.Branch;
                e.Title = s.Title;
                e.ExistsHere = here.Shelves.Contains(name);
                e.Excluded = !s.IsCheckout && IsExcluded(cfg, s.Branch);
                break;
        }
    }

    /// <summary>The marker as the export reader sees an export: the same matching and the same drift report.</summary>
    static ExportMeta MetaOf(string name, string markerBody)
    {
        var snap = SnapshotMeta.Parse(markerBody);
        var meta = new ExportMeta { Branch = name, Version = ExportMeta.Current };
        meta.Bases.Add(new ExportWc { Rel = "", Url = snap.Url.TrimEnd('/'), Revision = snap.Revision, Commit = snap.Commit.Length > 0 ? snap.Commit : null });
        foreach (var (rel, rev) in snap.Externals.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            meta.Bases.Add(new ExportWc
            {
                Rel = rel,
                Url = (snap.ExternalUrls.TryGetValue(rel, out var u) ? u : "").TrimEnd('/'),
                Revision = rev,
                Commit = snap.ExternalCommits.TryGetValue(rel, out var c) ? c : null,
            });
        return meta;
    }

    // ---- putting one back ----

    /// <summary>
    /// Makes the branch here again from the remote. When the store still has the real commits the
    /// branch is pointed at them; otherwise every change is merged onto the snapshot this checkout is
    /// at, one at a time, keeping the ones that went in when one stops. The uncommitted changes come
    /// back through the shelf, and the branch's shelves are made again against the restored tip.
    /// </summary>
    public static RestoreResult Restore(SgRoot root, string name, string? asBranch = null, string? intoCheckout = null, bool wip = false, bool force = false, IReadOnlyDictionary<string, string>? expectedRefs = null, bool rehearsal = false)
    {
        var cfg = Require(root);
        var git = root.Git;
        var remote = git.LsRemote(cfg.Url);
        var branchRef = RemoteRef(cfg, "branch", name);
        var wipRef = RemoteRef(cfg, "wip", name);
        var editsRef = RemoteRef(cfg, "edits", name);
        var hasBranch = remote.ContainsKey(branchRef);
        var hasWip = remote.ContainsKey(wipRef);
        var hasEdits = remote.ContainsKey(editsRef);
        var reviewRef = RemoteRef(cfg, "review", name);
        if (expectedRefs != null && remote.ContainsKey(reviewRef) != expectedRefs.ContainsKey(reviewRef))
            throw new SgException("Code review backup coverage changed. Refresh the preview.");
        var appearanceRef = RemoteRef(cfg, "appearance", name);
        if (expectedRefs != null && remote.ContainsKey(appearanceRef) != expectedRefs.ContainsKey(appearanceRef))
            throw new SgException("Checkout appearance backup coverage changed. Refresh the preview.");
        if (expectedRefs != null && (hasWip != expectedRefs.ContainsKey(wipRef) || !hasBranch))
            throw new SgException("Backup coverage changed. Refresh the receipt before restoring.");
        if (!hasBranch && !hasEdits) throw new SgException($"the backup holds nothing named {name}. sg backup list says what is there.");
        var res = new RestoreResult { Name = name };
        using var _ = root.Lock();

        // A checkout's local edits under this name, and no branch: nothing to make, a shelf to bring
        // back. A branch of the same name wins when both are there; its checkout is reached by its own name.
        if (!hasBranch)
        {
            var co = root.Checkout(name);
            var snap = git.RefSha(root.SnapshotRef(co)) ?? throw new SgException($"no snapshot of {co.Name} yet. Run: sg sync {co.Name}");
            git.FetchRefs(cfg.Url, ["+" + editsRef + ":" + FetchedRef("edits", name)]);
            res.Checkout = co.Name;
            res.Path = co.Path;
            Adopt(root, res, git.RefSha(FetchedRef("edits", name))!, snap, co.Path, co, null, write: true);
            git.UpdateRef(PushedRef("edits", name), git.RefSha(FetchedRef("edits", name))!);
            return res;
        }

        var specs = new List<string> { "+" + branchRef + ":" + FetchedRef("branch", name) };
        var reviews = FetchReview(root, cfg, name, remote);
        var appearance = FetchAppearance(root, cfg, name, remote);
        if (hasWip) specs.Add("+" + wipRef + ":" + FetchedRef("wip", name));
        var shelfRefs = remote.Keys.Where(r => Owned(cfg, r) is { Kind: "shelf" }).ToList();
        specs.AddRange(shelfRefs.Select(r => "+" + r + ":" + FetchedRef("shelf", Owned(cfg, r)!.Value.Name)));
        git.FetchRefs(cfg.Url, specs);

        if (expectedRefs != null)
            foreach (var pair in expectedRefs)
            {
                var owned = Owned(cfg, pair.Key) ?? throw new SgException("Receipt has an unrecognized ref.");
                if (git.RefSha(FetchedRef(owned.Kind, owned.Name)) != pair.Value)
                    throw new SgException("Backup changed during fetch. Refresh coverage before restoring.");
                if (git.Out(null, "rev-list", "--objects", "--missing=print", pair.Value).Split('\n').Any(x => x.StartsWith('?')))
                    throw new SgException("Recorded backup objects are incomplete.");
            }
        var thinTip = git.RefSha(FetchedRef("branch", name))!;
        var chain = Thin.Chain(git, thinTip);
        if (chain.Count == 0 || chain[0].Kind != ThinKind.Marker) throw new SgException($"{name} on the remote is not an sg backup: it does not start with a marker.");
        if (chain[0].Version > Thin.Version)
            throw new SgException($"{name} was backed up by a newer sg (format {chain[0].Version}, this one reads {Thin.Version}). Update sg, then restore it.");
        var meta = MetaOf(name, chain[0].Body);
        var target = (asBranch ?? name).Trim();
        if (target.Length == 0) throw new SgException("give the branch a name with --name.");
        var into = intoCheckout != null ? root.Checkout(intoCheckout) : Export.MatchCheckout(root, meta) ?? throw new SgException(Export.NoMatch(root, meta));
        var snapshot = git.RefSha(root.SnapshotRef(into)) ?? throw new SgException($"no snapshot of {into.Name} yet. Run: sg sync {into.Name}");
        if (git.RefSha("refs/heads/" + target) != null)
        {
            if (!force) throw new SgException($"branch exists here: {target}. Restore it under another name with --name, or force to write over it.");
            if (Operations.List(root).Any(x => !x.Terminal && x.Branch == target))
                throw new SgException("An unfinished operation protects this branch. Finish or close it in Activity before replacing it.");
            // Keep the entire original worktree, including ignored and untracked files.
            // A failed replacement must leave an accessible recovery branch.
            var recovery = target + "-before-restore-" + Guid.NewGuid().ToString("N")[..8];
            var original = git.WorktreeList().FirstOrDefault(w => w.Branch == target
                || (!w.Bare && Directory.Exists(w.Path) && git.RebaseHeadName(w.Path) == target));
            var savedPath = original == null ? null : original.Path + "-before-restore-" + Guid.NewGuid().ToString("N")[..8];
            if (original != null)
            {
                if (git.ReplayInProgress(original.Path) != Replay.None || ReplayName(git, original.Path) != null)
                    throw new SgException("Finish the pending operation before replacing " + target);
                git.Ok(null, "worktree", "move", original.Path, savedPath!);
            }
            try { git.Ok(null, "branch", "-m", target, recovery); }
            catch
            {
                if (original != null) git.Ok(null, "worktree", "move", savedPath!, original.Path);
                throw;
            }
            res.RecoveryBranch = recovery;
            res.RecoveryPath = savedPath;
            root.Log.Info("Original work preserved as " + recovery + (savedPath == null ? "" : " in " + savedPath));
            res.Replaced = true;
        }

        res.Branch = target;
        res.Checkout = into.Name;
        res.Drift = Export.DriftOf(root, meta, into);
        var changes = chain.Where(c => c.Kind == ThinKind.Change).ToList();
        res.Commits = changes.Count;

        // The store still has what the backup stands for: the branch was removed, or the ref was lost,
        // and the commits are here. Pointing at them is exact; a replay would only be a copy.
        var markerSource = chain[0].Source;
        var lastSource = changes.Count > 0 ? changes[^1].Source : markerSource;
        var relink = !rehearsal && markerSource != null && lastSource != null && git.HasCommit(markerSource) && git.HasCommit(lastSource)
                     && changes.All(c => c.Source != null && git.HasCommit(c.Source))
                     && git.IsAncestor(markerSource, lastSource) && git.IsAncestor(snapshot, lastSource) ;

        var made = Ops.Branch(root, target, into);
        res.Path = made.Path;
        if (reviews != null) res.ReviewThreads = CodeReview.Import(root, made.Path, reviews).Threads.Count;
        var tip = snapshot;
        if (relink)
        {
            tip = lastSource!;
            res.Applied = changes.Count;
            res.Relinked = true;
        }
        else
        {
            foreach (var c in changes)
            {
                var under = git.ParentOf(c.Sha) ?? throw new SgException("a change commit with nothing under it: " + c.Sha);
                var m = git.MergeTree(under, tip, c.Sha);
                if (!m.Clean)
                {
                    res.Stopped = c.Subject;
                    res.Conflicted = m.Conflicted;
                    res.Why = m.Messages;
                    break;
                }
                tip = git.CommitTreeAs(m.Tree, tip, Thin.Original(c.Body), c.Author);
                res.Applied++;
            }
        }
        if (!res.Ok)
        {
            BeginReplay(root, res, changes, wip && hasWip ? git.RefSha(FetchedRef("wip", name)) : null, pull: false);
            tip = git.HeadSha(made.Path);
        }
        else if (tip != snapshot) git.ResetHard(made.Path, tip);
        if (target == name && res.Ok)
        {
            git.UpdateRef(PushedRef("branch", name), thinTip);
            git.Config(KeyBackedUp(name), Now());
        }

        if (wip && hasWip && res.Ok)
        {
            Adopt(root, res, git.RefSha(FetchedRef("wip", name))!, tip, made.Path, into, target, write: true);
            // Taken: in the folder or on a shelf. This root may send its own uncommitted changes over the copy now.
            if (target == name) git.UpdateRef(PushedRef("wip", name), git.RefSha(FetchedRef("wip", name))!);
        }

        // The branch's shelves, made again against what the branch is here. On the shelf, not written:
        // a shelf was put aside on purpose.
        foreach (var r in shelfRefs)
        {
            var id = Owned(cfg, r)!.Value.Name;
            var sha = git.RefSha(FetchedRef("shelf", id));
            if (sha == null || (!rehearsal && here(id))) continue;
            var s = Shelf.Parse(id, sha, git.Body(sha));
            if (s.IsCheckout || !s.Branch.Equals(name, StringComparison.Ordinal)) continue;
            var under = git.ParentOf(sha);
            if (under == null) continue;
            var m = git.MergeTree(under, tip, sha);
            var info = Shelf.Adopt(root, m.Tree, git.Body(sha), made.Path, tip, into, target);
            res.Shelves.Add(info.Id + (m.Clean ? "" : " (with conflict markers)"));
        }
        if (!rehearsal) RestoreAppearance(root, into, appearance, res);
        Operations.Receipt(root, "Restore from backup", made.Path, ["Source branch: " + name, "Applied commits: " + res.Applied, res.WipWhy ?? (res.WipWritten ? "Local edits recovered" : "See shelves for preserved edits" )]);
        return res;

        bool here(string id) => git.RefSha(Shelf.RefPrefix + id) != null;
    }

    /// <summary>A thin wip merged onto what the folder is at, registered as a shelf, and written back when asked and clean.</summary>
    static void Adopt(SgRoot root, RestoreResult res, string thinSha, string ours, string path, CheckoutConfig co, string? branch, bool write)
    {
        var git = root.Git;
        var under = git.ParentOf(thinSha) ?? throw new SgException("uncommitted changes with nothing under them: " + thinSha);
        var m = git.MergeTree(under, ours, thinSha);
        var info = Shelf.Adopt(root, m.Tree, git.Body(thinSha), path, ours, co, branch);
        res.WipShelf = info.Id;
        if (!m.Clean)
        {
            res.WipConflicted = m.Conflicted;
            return;
        }
        if (!write) return;
        var r = Shelf.Restore(root, info.Id);
        res.WipWritten = true;
        res.WipConflicted = r.Conflicted;
    }

    // ---- prune ----

    /// <summary>
    /// Refs on the remote that nothing here answers to any more - a branch removed, a shelf dropped, and
    /// whatever a worktree left out of the backup sent before it was. Deleted only when asked.
    /// </summary>
    public static List<string> Prune(SgRoot root, bool delete)
    {
        var plan = PlanPrune(root);
        return delete ? Prune(root, plan) : plan.Refs.Keys.ToList();
    }

    // ---- status ----

    /// <summary>
    /// How far a branch is from its backup: 0 when the backup holds its tip, the commits above what it
    /// holds, or every commit when the branch was rewritten since. -1 when it was never backed up.
    /// </summary>
    public static int NotBackedUp(Git git, string branch, string tip, int ahead, RefInfo? pushed)
    {
        if (pushed == null) return -1;
        var t = Thin.Parse(pushed.Sha, pushed.Message, new Author("", "", ""));
        if (t.Source == null) return ahead;
        if (t.Source == tip) return 0;
        if (!git.HasCommit(t.Source) || !git.IsAncestor(t.Source, tip)) return ahead;
        return git.CountCommits(t.Source, tip);
    }

    public static DateTimeOffset? BackedUpAt(string? value) =>
        value != null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when : null;
}
