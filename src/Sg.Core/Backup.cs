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
                || line.StartsWith("svn-external: ", StringComparison.Ordinal))
                sb.Append(line).Append('\n');
        sb.Append(KeyVersion).Append(": ").Append(Version).Append('\n');
        sb.Append(KeyKind).Append(": marker\n");
        sb.Append(KeySource).Append(": ").Append(snapshotSha).Append('\n');
        return git.CommitTreeExact(git.EmptyTree(), null, sb.ToString(), who);
    }

    public sealed record Result(string Tip, int Changes, bool FromScratch);

    /// <summary>
    /// The thin history of everything between a snapshot and a tip. From the marker when none of it was
    /// built before; from a thin tip already built when the commit it stands for is still on the run,
    /// so only what sits above it is rewritten. The candidates are tried in order: a wip or a shelf
    /// hands in the branch's own tip, whose history is its prefix.
    /// </summary>
    public static Result Rewrite(SgRoot root, string tip, string snapshot, IEnumerable<string?> builtTips)
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
                if (needBase.Count > 0)
                {
                    var bases = git.BlobsAt(parent, needBase);
                    if (bases.Count > 0)
                    {
                        var tree = Compose(git, index, thinParent, bases, Array.Empty<string>());
                        var msg = $"sg base: {bases.Count} file(s) the next change starts from\n\n{KeyKind}: base\n{KeySource}: {c}\n";
                        thinParent = git.CommitTreeExact(tree, thinParent, msg, who);
                        foreach (var b in bases) present.Add(b.Path);
                    }
                }

                var adds = changed.Where(e => e.Status != 'D').Select(e => e.Path).ToList();
                var dels = changed.Where(e => e.Status == 'D' && present.Contains(e.Path)).Select(e => e.Path).ToList();
                var entries = git.BlobsAt(c, adds);
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
        return new Result(thinParent, chain.Count, scratch);
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

    public bool Pushed => State == "pushed";
    public bool Rejected => State == "rejected";
    public bool Failed => State == "failed";
    /// <summary>The remote holds a newer version than this one, so backing up would send an older copy. Restore brings it here.</summary>
    public bool Behind => State == "behind";
}

public sealed class BackupResult
{
    public string Url = "";
    public List<BackupItem> Items = new();
    /// <summary>Refs on the remote under sg's names that nothing here answers to any more. Prune deletes them.</summary>
    public List<string> RemoteOnly = new();
    public int Pushed => Items.Count(i => i.Pushed);
    public int Rejected => Items.Count(i => i.Rejected);
    /// <summary>Names the remote holds a newer version of than here: a restore, not a push, is the move.</summary>
    public int Behind => Items.Count(i => i.Behind);
    public bool Ok => Items.All(i => !i.Rejected && !i.Failed);
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
    public DateTimeOffset? Last;
    /// <summary>Why it cannot be read, when it cannot: written by a newer sg, or not an sg backup at all.</summary>
    public string? Unreadable;
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
    public List<ExportDrift> Drift = new();
    public string? Stopped;
    public List<string> Conflicted = new();
    public string? Why;
    /// <summary>A branch of this name was here already and force wrote over it.</summary>
    public bool Replaced;
    /// <summary>The shelf the uncommitted changes came back as, when they came back.</summary>
    public string? WipShelf;
    /// <summary>And they were written into the folder, so the shelf is gone again.</summary>
    public bool WipWritten;
    public List<string> WipConflicted = new();
    /// <summary>Shelves of the branch made again here, by id.</summary>
    public List<string> Shelves = new();
    public bool Ok => Stopped == null;
}

/// <summary>
/// A second copy of everything this machine has that SVN does not - the branches, the changes not yet
/// committed, the shelves - in a git repository somewhere else, as thin histories. Push mirrors with a
/// lease and never deletes; restore puts a branch back the way import does, through a three way merge
/// onto whatever revision the checkout here is at.
/// </summary>
public static class Backup
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
        _ => RemoteShelf + Prefix(b) + name,
    };

    /// <summary>The kind and the name a ref on the remote stands for, or null when it is not one of sg's.</summary>
    public static (string Kind, string Name)? Owned(BackupConfig b, string remoteRef)
    {
        var p = Prefix(b);
        foreach (var (kind, head) in new[] { ("branch", "refs/heads/" + p), ("wip", RemoteWip + p), ("edits", RemoteEdits + p), ("shelf", RemoteShelf + p) })
            if (remoteRef.StartsWith(head, StringComparison.Ordinal) && remoteRef.Length > head.Length)
                return (kind, remoteRef[head.Length..]);
        return null;
    }

    static string Ns(string kind) => kind == "branch" ? "heads" : kind;
    public static string PushedRef(string kind, string name) => PushedPrefix + Ns(kind) + "/" + name;
    public static string FetchedRef(string kind, string name) => FetchedPrefix + Ns(kind) + "/" + name;
    static string KeyBackedUp(string branch) => $"branch.{branch}.sgBackedUp";

    /// <summary>Where backups go. The URL is tried before it is kept, so a typo is found now and not on the timer.</summary>
    public static BackupConfig Set(SgRoot root, string url, string? prefix = null, bool? uncommitted = null)
    {
        var b = root.Config.Backup ?? new BackupConfig();
        var u = url.Trim();
        if (u.Length == 0) throw new SgException("give the URL of a git repository: a bare folder, a share, or a hosted repository.");
        root.Git.LsRemote(u);
        b.Url = u;
        if (prefix != null) b.Prefix = prefix.Trim().Trim('/');
        if (uncommitted != null) b.Uncommitted = uncommitted.Value;
        root.Config.Backup = b;
        root.Save();
        return b;
    }

    public static void Clear(SgRoot root)
    {
        root.Config.Backup = null;
        root.Save();
    }

    // ---- pushing ----

    /// <summary>
    /// Every branch, every folder's uncommitted changes, every shelf: rewritten thin, compared with what
    /// the remote holds, and pushed under a lease unless check only asks. One branch the remote holds
    /// another version of is skipped and named; the rest still go.
    /// </summary>
    public static BackupResult Run(SgRoot root, bool check = false, bool force = false)
    {
        var cfg = Require(root);
        var git = root.Git;
        using var _ = root.Lock();
        var res = new BackupResult { Url = cfg.Url };
        var remote = git.LsRemote(cfg.Url);
        var pending = new List<(BackupItem Item, string? Lease)>();

        var sources = Sources(root, cfg);

        // The refs the remote holds a version of that this root did not push last - another machine, or
        // another root. Fetched in one call so their lineage can be read, and reconciled by it below.
        var foreign = sources
            .Select(s => (S: s, Ref: RemoteRef(cfg, s.Kind, s.Name)))
            .Where(x => remote.TryGetValue(x.Ref, out var have) && have != git.RefSha(PushedRef(x.S.Kind, x.S.Name)))
            .ToList();
        if (foreign.Count > 0)
            git.FetchRefs(cfg.Url, foreign.Select(x => "+" + x.Ref + ":" + FetchedRef(x.S.Kind, x.S.Name)));

        foreach (var s in sources)
        {
            var item = new BackupItem { Kind = s.Kind, Name = s.Name, RemoteRef = RemoteRef(cfg, s.Kind, s.Name) };
            res.Items.Add(item);
            var pushedRef = PushedRef(s.Kind, s.Name);
            var last = git.RefSha(pushedRef);
            try
            {
                var thin = Thin.Rewrite(root, s.Tip, s.Snapshot, [last, s.Branch != null ? git.RefSha(PushedRef("branch", s.Branch)) : null]);
                item.Thin = thin.Tip;
                item.Commits = git.CountCommits(s.Snapshot, s.Tip);
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

            // Nothing there, or the version this root last pushed: a plain push under the lease. Force
            // writes over whatever is there, so it too goes straight to the push. Otherwise another machine
            // wrote it, and which side is ahead decides between overwriting and waiting.
            if (have == null || have == last || force)
            {
                item.State = check ? "would push" : "pending";
                pending.Add((item, have ?? last));
                continue;
            }
            switch (Reconcile(git, s.Tip, have))
            {
                case Verdict.Ahead:
                    // The remote's is an ancestor of the tip here, so the push carries it forward. The
                    // lease is what was just read, so a race between the look and the push still rejects.
                    item.Reconciled = true;
                    item.State = check ? "would reconcile" : "pending";
                    pending.Add((item, have));
                    break;
                case Verdict.Behind:
                    item.State = "behind";
                    item.Why = "the backup holds a newer version of this than the one here - it came from another machine. "
                               + "Restore it to bring that work here; backing up would only send an older copy over it.";
                    break;
                default:
                    item.State = "rejected";
                    item.Why = "the remote holds a version of this that did not come from here, and neither is an ancestor "
                               + "of the other - another machine has different work under this name. "
                               + "Restore it if it is the one to keep, back up under a prefix, or --force to write over it.";
                    break;
            }
        }

        // A folder whose changes went up earlier and is clean now: the wip on the remote would bring back
        // work that was committed since, so it goes with this push. Everything else on the remote stays.
        var mine = res.Items.Select(i => i.RemoteRef).ToHashSet(StringComparer.Ordinal);
        var here = LocalNames(root);
        var stale = new List<string>();
        foreach (var r in remote.Keys)
        {
            if (mine.Contains(r) || Owned(cfg, r) is not { } o) continue;
            if ((o.Kind == "wip" && here.Branches.Contains(o.Name)) || (o.Kind == "edits" && here.Checkouts.Contains(o.Name))) stale.Add(r);
            else res.RemoteOnly.Add(r);
        }
        res.RemoteOnly.Sort(StringComparer.Ordinal);
        if (check || (pending.Count == 0 && stale.Count == 0)) return res;

        var pushes = pending.Select(p => new PushRef(p.Item.Thin, p.Item.RemoteRef, p.Lease ?? "")).ToList();
        pushes.AddRange(stale.Select(r => new PushRef(null, r, remote[r])));
        var answers = git.PushRefs(cfg.Url, pushes, force);
        foreach (var (item, _) in pending)
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
        foreach (var r in stale)
            if (answers.TryGetValue(r, out var a) && a.Ok && Owned(cfg, r) is { } o) git.DeleteRef(PushedRef(o.Kind, o.Name));
        return res;
    }

    static string Now() => DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);

    enum Verdict { Ahead, Behind, Diverged }

    /// <summary>
    /// Which side is ahead, the tip here or the version already on the remote, read by the real commits
    /// their thin histories stand for. Ahead: the remote's real commit is an ancestor of the tip here, so
    /// a push carries it forward and is safe to write. Behind: the tip here is an ancestor of the remote's,
    /// so the remote is the newer one and a restore is the move. Diverged: neither is an ancestor of the
    /// other, or the remote cannot be read - a push would drop one side, so it waits for a restore or a
    /// --force. The remote's real commit is read only when it is still in this store; when it is not - two
    /// machines whose commits never met over SVN - that alone is a divergence.
    /// </summary>
    static Verdict Reconcile(Git git, string tip, string remoteThin)
    {
        List<ThinCommit> chain;
        try { chain = Thin.Chain(git, remoteThin); }
        catch (SgException) { return Verdict.Diverged; }
        if (chain.Count == 0 || chain[0].Kind != ThinKind.Marker || chain[0].Version > Thin.Version) return Verdict.Diverged;
        var changes = chain.Where(c => c.Kind == ThinKind.Change).ToList();
        var source = changes.Count > 0 ? changes[^1].Source : chain[0].Source;
        if (source == null || !git.HasCommit(source)) return Verdict.Diverged;
        if (git.IsAncestor(source, tip)) return Verdict.Ahead;
        if (git.IsAncestor(tip, source)) return Verdict.Behind;
        return Verdict.Diverged;
    }

    sealed record Source(string Kind, string Name, string Tip, string Snapshot, string? Branch);

    /// <summary>Everything a backup is made of, with the snapshot each one sits on.</summary>
    static List<Source> Sources(SgRoot root, BackupConfig cfg)
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
            if (branch == null) continue;
            if (!bases.TryGetValue(branch, out var coName) || !snapshots.TryGetValue(coName, out var snap)) continue;
            var tip = git.RefSha("refs/heads/" + branch);
            if (tip == null) continue;
            var mb = git.MergeBase(tip, snap);
            if (mb == null)
            {
                root.Log.Warn($"{branch} shares no history with svn/{coName}, so it is not backed up");
                continue;
            }
            list.Add(new Source("branch", branch, tip, mb, null));
            if (!cfg.Uncommitted || !Directory.Exists(w.Path)) continue;
            try
            {
                var wip = Shelf.Wip(root, w.Path);
                if (wip != null) list.Add(new Source("wip", branch, wip.Sha, mb, branch));
            }
            catch (SgException e) { root.Log.Warn($"{branch}: the uncommitted changes are not in the backup: {e.Message}"); }
        }

        if (cfg.Uncommitted)
            foreach (var co in root.Config.Checkouts)
            {
                if (!snapshots.TryGetValue(co.Name, out var snap) || !Directory.Exists(co.Path)) continue;
                try
                {
                    var wip = Shelf.Wip(root, co.Path);
                    if (wip != null) list.Add(new Source("edits", co.Name, wip.Sha, snap, null));
                }
                catch (SgException e) { root.Log.Warn($"{co.Name}: the local edits are not in the backup: {e.Message}"); }
            }

        foreach (var s in Shelf.List(root))
        {
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
            list.Add(new Source("shelf", s.Id, s.Sha, mb, s.IsCheckout ? null : s.Branch));
        }
        return list;
    }

    sealed record Local(HashSet<string> Branches, HashSet<string> Checkouts, HashSet<string> Shelves);

    static Local LocalNames(SgRoot root)
    {
        var git = root.Git;
        var branches = git.RefIndex("refs/heads/").Keys.Select(k => k["refs/heads/".Length..]).ToHashSet(StringComparer.Ordinal);
        var checkouts = root.Config.Checkouts.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var shelves = Shelf.List(root).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return new Local(branches, checkouts, shelves);
    }

    // ---- what is there ----

    /// <summary>Fetches every ref of sg's on the remote and says what each one is. Nothing here is written but the fetched refs.</summary>
    public static List<BackupEntry> List(SgRoot root)
    {
        var cfg = Require(root);
        var git = root.Git;
        var remote = git.LsRemote(cfg.Url);
        var owned = remote.Where(kv => Owned(cfg, kv.Key) != null).ToList();
        git.FetchRefs(cfg.Url, owned.Select(kv => { var o = Owned(cfg, kv.Key)!.Value; return "+" + kv.Key + ":" + FetchedRef(o.Kind, o.Name); }));

        var here = LocalNames(root);
        var wips = owned.Select(kv => Owned(cfg, kv.Key)!.Value).Where(o => o.Kind == "wip").Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var res = new List<BackupEntry>();
        foreach (var kv in owned)
        {
            var (kind, name) = Owned(cfg, kv.Key)!.Value;
            var e = new BackupEntry { Kind = kind, Name = name, Sha = kv.Value };
            res.Add(e);
            List<ThinCommit> chain;
            try { chain = Thin.Chain(git, kv.Value); }
            catch (SgException ex) { e.Unreadable = ex.Message; continue; }
            if (chain.Count == 0 || chain[0].Kind != ThinKind.Marker)
            {
                e.Unreadable = "not an sg backup: it does not start with a marker";
                continue;
            }
            if (chain[0].Version > Thin.Version)
            {
                e.Unreadable = $"written by a newer sg (format {chain[0].Version}, this one reads {Thin.Version}). Update sg.";
                continue;
            }
            var meta = MetaOf(name, chain[0].Body);
            e.Url = meta.Root?.Url ?? "";
            e.Revision = meta.Root?.Revision ?? 0;
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
                    break;
                case "wip":
                    e.Branch = name;
                    e.ExistsHere = here.Branches.Contains(name);
                    e.Title = "uncommitted changes";
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
                    break;
            }
        }
        return res.OrderBy(e => e.Kind switch { "branch" => 0, "wip" => 1, "edits" => 2, _ => 3 }).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The marker as the export reader sees an export: the same matching and the same drift report.</summary>
    static ExportMeta MetaOf(string name, string markerBody)
    {
        var snap = SnapshotMeta.Parse(markerBody);
        var meta = new ExportMeta { Branch = name, Version = ExportMeta.Current };
        meta.Bases.Add(new ExportWc { Rel = "", Url = snap.Url.TrimEnd('/'), Revision = snap.Revision });
        foreach (var (rel, rev) in snap.Externals.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            meta.Bases.Add(new ExportWc { Rel = rel, Url = (snap.ExternalUrls.TryGetValue(rel, out var u) ? u : "").TrimEnd('/'), Revision = rev });
        return meta;
    }

    // ---- putting one back ----

    /// <summary>
    /// Makes the branch here again from the remote. When the store still has the real commits the
    /// branch is pointed at them; otherwise every change is merged onto the snapshot this checkout is
    /// at, one at a time, keeping the ones that went in when one stops. The uncommitted changes come
    /// back through the shelf, and the branch's shelves are made again against the restored tip.
    /// </summary>
    public static RestoreResult Restore(SgRoot root, string name, string? asBranch = null, string? intoCheckout = null, bool wip = false, bool force = false)
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
            return res;
        }

        var specs = new List<string> { "+" + branchRef + ":" + FetchedRef("branch", name) };
        if (hasWip) specs.Add("+" + wipRef + ":" + FetchedRef("wip", name));
        var shelfRefs = remote.Keys.Where(r => Owned(cfg, r) is { Kind: "shelf" }).ToList();
        specs.AddRange(shelfRefs.Select(r => "+" + r + ":" + FetchedRef("shelf", Owned(cfg, r)!.Value.Name)));
        git.FetchRefs(cfg.Url, specs);

        var thinTip = git.RefSha(FetchedRef("branch", name))!;
        var chain = Thin.Chain(git, thinTip);
        if (chain.Count == 0 || chain[0].Kind != ThinKind.Marker) throw new SgException($"{name} on the remote is not an sg backup: it does not start with a marker.");
        if (chain[0].Version > Thin.Version)
            throw new SgException($"{name} was backed up by a newer sg (format {chain[0].Version}, this one reads {Thin.Version}). Update sg, then restore it.");
        var meta = MetaOf(name, chain[0].Body);
        var target = (asBranch ?? name).Trim();
        if (target.Length == 0) throw new SgException("give the branch a name with --name.");
        var into = intoCheckout != null ? root.Checkout(intoCheckout) : Export.MatchCheckout(root, meta) ?? throw new SgException(Export.NoMatch(root, meta));
        if (git.RefSha("refs/heads/" + target) != null)
        {
            if (!force) throw new SgException($"branch exists here: {target}. Restore it under another name with --name, or force to write over it.");
            // Force writes over the branch and its worktree with what the backup holds, uncommitted
            // changes in it and all. Removing it first lets the rest of restore make it the usual way.
            Ops.Remove(root, target, force: true);
            res.Replaced = true;
        }
        var snapshot = git.RefSha(root.SnapshotRef(into)) ?? throw new SgException($"no snapshot of {into.Name} yet. Run: sg sync {into.Name}");

        res.Branch = target;
        res.Checkout = into.Name;
        res.Drift = Export.DriftOf(root, meta, into);
        var changes = chain.Where(c => c.Kind == ThinKind.Change).ToList();
        res.Commits = changes.Count;

        // The store still has what the backup stands for: the branch was removed, or the ref was lost,
        // and the commits are here. Pointing at them is exact; a replay would only be a copy.
        var markerSource = chain[0].Source;
        var lastSource = changes.Count > 0 ? changes[^1].Source : markerSource;
        var relink = markerSource != null && lastSource != null && git.HasCommit(markerSource) && git.HasCommit(lastSource)
                     && changes.All(c => c.Source != null && git.HasCommit(c.Source))
                     && git.IsAncestor(markerSource, lastSource) && git.IsAncestor(snapshot, lastSource) ;

        var made = Ops.Branch(root, target, into);
        res.Path = made.Path;
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
        if (tip != snapshot) git.ResetHard(made.Path, tip);
        if (target == name && res.Ok)
        {
            git.UpdateRef(PushedRef("branch", name), thinTip);
            git.Config(KeyBackedUp(name), Now());
        }

        if (wip && hasWip && res.Ok)
            Adopt(root, res, git.RefSha(FetchedRef("wip", name))!, tip, made.Path, into, target, write: true);

        // The branch's shelves, made again against what the branch is here. On the shelf, not written:
        // a shelf was put aside on purpose.
        foreach (var r in shelfRefs)
        {
            var id = Owned(cfg, r)!.Value.Name;
            var sha = git.RefSha(FetchedRef("shelf", id));
            if (sha == null || here(id)) continue;
            var s = Shelf.Parse(id, sha, git.Body(sha));
            if (s.IsCheckout || !s.Branch.Equals(name, StringComparison.Ordinal)) continue;
            var under = git.ParentOf(sha);
            if (under == null) continue;
            var m = git.MergeTree(under, tip, sha);
            var info = Shelf.Adopt(root, m.Tree, git.Body(sha), made.Path, tip, into, target);
            res.Shelves.Add(info.Id + (m.Clean ? "" : " (with conflict markers)"));
        }
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

    /// <summary>Refs on the remote that nothing here answers to any more. Deleted only when asked.</summary>
    public static List<string> Prune(SgRoot root, bool delete)
    {
        var cfg = Require(root);
        var git = root.Git;
        var remote = git.LsRemote(cfg.Url);
        var here = LocalNames(root);
        var orphans = new List<string>();
        foreach (var r in remote.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (Owned(cfg, r) is not { } o) continue;
            var known = o.Kind switch
            {
                "branch" or "wip" => here.Branches.Contains(o.Name),
                "edits" => here.Checkouts.Contains(o.Name),
                _ => here.Shelves.Contains(o.Name),
            };
            if (!known) orphans.Add(r);
        }
        if (!delete || orphans.Count == 0) return orphans;
        var answers = git.PushRefs(cfg.Url, orphans.Select(r => new PushRef(null, r, remote[r])), force: false);
        var failed = orphans.Where(r => !(answers.TryGetValue(r, out var a) && a.Ok)).ToList();
        foreach (var r in orphans.Except(failed))
        {
            var o = Owned(cfg, r)!.Value;
            git.DeleteRef(PushedRef(o.Kind, o.Name));
            git.DeleteRef(FetchedRef(o.Kind, o.Name));
        }
        if (failed.Count > 0) throw new SgException("not deleted: " + string.Join(", ", failed));
        return orphans;
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
