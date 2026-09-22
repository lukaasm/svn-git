using System.Globalization;

namespace Sg.Core;

/// <summary>
/// A backup used between machines: work committed on one, restored on another, carried on there, and sent
/// back. A restore makes new commits with new hashes, and so does a rebase, so a copy on the remote that
/// another machine sent can never be read by lineage alone - every such copy looked like somebody else's
/// work, and backing up refused it or needed --force. Here a commit is also known by what it is: who wrote
/// it, when, and what it says, which a restore and a rebase both keep. Uncommitted changes are known by
/// the files they wrote. That tells an older copy, a newer one, and the same work under other hashes apart
/// from work that really differs, and only the last is refused.
/// </summary>
public static partial class Backup
{
    enum Verdict { Ahead, Behind, Diverged, Same }

    /// <summary>A commit by what it is, with the subject and the date to name it by.</summary>
    sealed record Change(string Key, string Subject, string Date);

    static string KeyOf(string email, string date, string message)
    {
        var when = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            : date.Trim();
        return email.Trim().ToLowerInvariant() + "\x1f" + when + "\x1f" + message.Replace("\r", "").TrimEnd();
    }

    /// <summary>The branch's own commits above the snapshot it sits on, oldest first, along the first parent.</summary>
    static List<Change> LocalChanges(Git git, string snapshot, string tip)
    {
        var r = git.Ok(null, "log", "--reverse", "--first-parent", "--format=%ae%x1f%aI%x1f%B%x1e", snapshot + ".." + tip);
        var res = new List<Change>();
        foreach (var record in r.StdOut.Split('\x1e'))
        {
            var p = record.TrimStart('\n', '\r').Split('\x1f', 3);
            if (p.Length < 3) continue;
            res.Add(new Change(KeyOf(p[0], p[1], p[2]), Msg.Subject(p[2].Split('\n')[0]), p[1]));
        }
        return res;
    }

    /// <summary>The commits a thin history stands for, oldest first.</summary>
    static List<Change> RemoteChanges(IEnumerable<ThinCommit> chain) =>
        chain.Where(c => c.Kind == ThinKind.Change)
            .Select(c => new Change(KeyOf(c.Author.Email, c.Author.Date, Thin.Original(c.Body)), c.Subject, c.Author.Date))
            .ToList();

    static int SharedPrefix(List<Change> a, List<Change> b)
    {
        var n = 0;
        while (n < a.Count && n < b.Count && a[n].Key == b[n].Key) n++;
        return n;
    }

    /// <summary>"the newest "subject", 2026-09-10 10:55" for a run of commits, to name it by in a sentence.</summary>
    static string Newest(IEnumerable<Change> changes)
    {
        var last = changes.LastOrDefault();
        if (last == null) return "";
        var when = DateTimeOffset.TryParse(last.Date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : last.Date;
        return $"the newest \"{last.Subject}\", {when}";
    }

    static string Files(IReadOnlyCollection<string> paths) =>
        paths.Count <= 3 ? string.Join(", ", paths) : $"{string.Join(", ", paths.Take(3))} and {paths.Count - 3} more";

    static Dictionary<string, string> Blobs(Git git, string treeish, IEnumerable<string> paths) =>
        git.BlobsAt(treeish, paths).ToDictionary(e => e.Path, e => e.Sha, StringComparer.Ordinal);

    /// <summary>
    /// Which side is ahead, the thing here or the copy the remote holds, and when it cannot be settled, a
    /// sentence that says what differs. Same: the same work, which needs no push either way.
    /// </summary>
    static (Verdict Verdict, string? Why) Reconcile(Git git, Source s, string remoteThin, string ourThin)
    {
        List<ThinCommit> chain;
        try { chain = Thin.Chain(git, remoteThin); }
        catch (SgException) { return (Verdict.Diverged, "the copy on the remote cannot be read"); }
        if (chain.Count == 0 || chain[0].Kind != ThinKind.Marker) return (Verdict.Diverged, "the copy on the remote is not an sg backup");
        if (chain[0].Version > Thin.Version) return (Verdict.Diverged, "the copy on the remote was written by a newer sg");
        return s.Kind switch
        {
            "branch" => ReconcileBranch(git, s, chain, ourThin),
            "wip" or "edits" => ReconcileChanges(git, s.Tip, chain),
            _ => Lineage(git, s.Tip, chain) ?? (Verdict.Diverged, null),
        };
    }

    /// <summary>
    /// Whether the copy on the remote stands for a commit this branch itself once pointed at here. A rebase,
    /// a squash and an amend all make new commits of the same work, and the one they replaced stays in the
    /// branch's reflog - so a copy built from it is this machine's own older state, not somebody else's work
    /// under the same name, and this side is ahead of it however little the two histories now line up.
    ///
    /// The commits of a rebase carry the same author, date and message, so they compare as the same work;
    /// what a rebase changes is the base they sit on and whatever a conflict was settled to, and both of
    /// those show up as other files in the same commits. Without this, every rebase of a branch that had
    /// been backed up needed --force, which is the one habit a safety net must not teach.
    /// </summary>
    static bool WasHere(Git git, Source s, List<ThinCommit> chain)
    {
        var changes = chain.Where(c => c.Kind == ThinKind.Change).ToList();
        var source = changes.Count > 0 ? changes[^1].Source : chain[0].Source;
        // A branch source carries its name in Name; Branch is what a shelf or a wip hangs off.
        var branch = s.Kind == "branch" ? s.Name : s.Branch;
        if (source == null || branch == null) return false;
        return git.BranchWas(branch).Any(sha => sha == source);
    }

    /// <summary>
    /// When the copy on the remote was written, which is the one fact that says where it came from: a
    /// machine somebody was working on that day. A refusal used to name what differed and leave the reader
    /// to guess whether the other side was last week's laptop or this morning's own push.
    /// </summary>
    static string Elsewhere(Git git, List<ThinCommit> chain)
    {
        var r = git.Run(null, "log", "-1", "--format=%cI", chain[^1].Sha);
        if (!r.Ok || !DateTimeOffset.TryParse(r.StdOut.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            return "";
        return ", and was written " + when.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether every commit the copy holds past the ones this branch shares with it is in this branch's
    /// tip by content: what it changed, taken back out of the tip, comes out cleanly. False for a copy with
    /// nothing past the shared run, which is a question this was not asked.
    /// </summary>
    static bool AllChangesHere(Git git, List<ThinCommit> chain, int shared, string tip)
    {
        var extra = chain.Where(c => c.Kind == ThinKind.Change).Skip(shared).ToList();
        return extra.Count > 0 && extra.All(c => git.ChangeIsIn(c.Sha, tip));
    }

    /// <summary>By lineage, when this store still has the real commit the copy stands for. Null when that does not settle it.</summary>
    static (Verdict, string?)? Lineage(Git git, string tip, List<ThinCommit> chain)
    {
        var changes = chain.Where(c => c.Kind == ThinKind.Change).ToList();
        var source = changes.Count > 0 ? changes[^1].Source : chain[0].Source;
        if (source == null || !git.HasCommit(source)) return null;
        if (git.IsAncestor(source, tip)) return (Verdict.Ahead, null);
        if (git.IsAncestor(tip, source)) return (Verdict.Behind, null);
        return null;
    }

    /// <summary>
    /// A branch against its copy. By lineage when that settles it; otherwise by the commits themselves. The
    /// remote's commits the first of this branch's: ahead. This branch's the first of the remote's: behind.
    /// All the same with the same files: the same work. All the same with other files: whichever sits on the
    /// newer snapshot was rebased, and that one is ahead. Anything else differs, and says how.
    /// </summary>
    static (Verdict, string?) ReconcileBranch(Git git, Source s, List<ThinCommit> chain, string ourThin)
    {
        if (Lineage(git, s.Tip, chain) is { } byLineage) return byLineage;
        var theirs = RemoteChanges(chain);
        var ours = LocalChanges(git, s.Snapshot, s.Tip);
        var shared = SharedPrefix(theirs, ours);
        var theirRev = SnapshotMeta.Parse(chain[0].Body).Revision;
        var ourRev = SnapshotMeta.Parse(git.Body(s.Snapshot)).Revision;
        if (shared == theirs.Count && shared == ours.Count)
        {
            // Both thin trees hold exactly the files their histories wrote, at the last version written: equal
            // trees are the same files both ways, where comparing one side's files against the other missed a
            // file only this side added.
            if (git.TreeOf(chain[^1].Sha) == git.TreeOf(ourThin)) return (Verdict.Same, null);
            if (theirRev < ourRev) return (Verdict.Ahead, null);
            if (theirRev > ourRev)
                return (Verdict.Behind, $"the remote holds the same {ours.Count} commit(s) rebased onto r{theirRev}, and this branch is on r{ourRev}. "
                                        + "Sync this checkout and rebase the branch to catch up; backing up now would send the older base over it.");
            // The same commits, the same SVN revision under them, and other bytes in them: one machine
            // settled a conflict, or rebased over the same revision again. This is what a backup shared
            // between machines looks like in the normal week, and refusing it made every rebase of a
            // branch that lives on two machines need --force. Nothing is lost by sending: every commit
            // the remote holds is here too, under its own name, and this side is the one being backed up.
            return (Verdict.Ahead, null);
        }
        if (WasHere(git, s, chain)) return (Verdict.Ahead, null);
        if (shared == theirs.Count && theirRev <= ourRev) return (Verdict.Ahead, null);
        // The commits only the remote has, by what they changed rather than by name: a commit pushed to
        // SVN from the other machine is in this branch's snapshot now and gone from its own list, and one
        // made again here is a different commit with the same change in it. Neither is lost by sending,
        // so when every one of them is in this branch already, this side is ahead. The check is by content
        // because that is the only thing the two sides still share by then.
        if (AllChangesHere(git, chain, shared, s.Tip)) return (Verdict.Ahead, null);
        if (shared == ours.Count && ourRev <= theirRev)
            return (Verdict.Behind, $"the remote holds {theirs.Count - shared} commit(s) this branch does not, {Newest(theirs.Skip(shared))}. "
                                    + $"Backing up would send an older copy over them; pull brings them here: sg backup pull {s.Name}");
        return (Verdict.Diverged, $"the remote holds {theirs.Count - shared} commit(s) this branch does not ({Newest(theirs.Skip(shared))})"
                                  + $" and this branch holds {ours.Count - shared} the remote does not ({Newest(ours.Skip(shared))})"
                                  + Elsewhere(git, chain));
    }

    /// <summary>
    /// Uncommitted changes against the copy on the remote, file by file. Each file the copy's changes wrote
    /// holds here what they wrote (taken in), what they started from (not here yet), or something else.
    /// All taken in: ahead, or the same when the changes here are exactly those files. None, and nothing
    /// else touched: behind. A mix differs, and names the files. here is a commit whose tree is the folder:
    /// the folder's own wip, or the branch tip of a folder with nothing uncommitted.
    /// </summary>
    static (Verdict, string?) ReconcileChanges(Git git, string here, List<ThinCommit> chain)
    {
        var last = chain[^1];
        var under = git.ParentOf(last.Sha);
        if (last.Kind != ThinKind.Change || under == null) return (Verdict.Diverged, "the copy on the remote holds no changes to compare");
        var paths = git.DiffNameStatus(git.Store, under, last.Sha, renames: false).Select(e => e.Path).ToList();
        if (paths.Count == 0) return (Verdict.Ahead, null);
        var wrote = Blobs(git, last.Sha, paths);
        var from = Blobs(git, under, paths);
        var ours = Blobs(git, here, paths);
        var taken = new List<string>();
        var missing = new List<string>();
        var other = new List<string>();
        foreach (var p in paths)
        {
            var h = ours.GetValueOrDefault(p);
            if (h == wrote.GetValueOrDefault(p)) taken.Add(p);
            else if (h == from.GetValueOrDefault(p)) missing.Add(p);
            else other.Add(p);
        }
        if (missing.Count == 0 && other.Count == 0)
        {
            var mine = git.ParentOf(here) is { } b
                ? git.DiffNameStatus(git.Store, b, here, renames: false).Select(e => e.Path).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            return mine.SetEquals(paths) ? (Verdict.Same, null) : (Verdict.Ahead, null);
        }
        if (taken.Count == 0 && other.Count == 0)
            return (Verdict.Behind, $"the remote holds uncommitted changes to {Files(missing)} that are not here");
        return (Verdict.Diverged, $"the remote's uncommitted changes and the ones here differ in {Files(other.Concat(missing).ToList())}"
                                  + (taken.Count > 0 ? $", and agree on {taken.Count} other file(s)" : ""));
    }

    // ---- pull ----

    /// <summary>
    /// Brings what another machine sent into what is here, without making anything again. For a branch: the
    /// commits the copy holds past this branch's own go on top of its tip, and the worktree moves to them the
    /// way a fast-forward moves it, with its uncommitted changes kept. Then the copy's uncommitted changes, the
    /// same way a checkout's local edits come when the name is a checkout's: nothing when the folder already
    /// holds them, written in when it holds none of them, and on a shelf beside the folder's own when both
    /// changed the same files - conflict markers in files somebody is working on are not a pull's to write.
    /// </summary>
    public static RestoreResult Pull(SgRoot root, string name)
    {
        var cfg = Require(root);
        var git = root.Git;
        var remote = git.LsRemote(cfg.Url);
        var branchRef = RemoteRef(cfg, "branch", name);
        var wipRef = RemoteRef(cfg, "wip", name);
        var editsRef = RemoteRef(cfg, "edits", name);
        var res = new RestoreResult { Name = name };
        using var _ = root.Lock();

        if (git.RefSha("refs/heads/" + name) is { } tip)
        {
            var worktree = git.WorktreeList().FirstOrDefault(w => !w.Bare && w.Branch == name)?.Path
                           ?? throw new SgException($"{name} has no worktree here to pull into.");
            if (Conflicts.HasPending(git, worktree))
                throw new SgException($"{name} is in the middle of a rebase or an import. Finish it, then pull.");
            var coName = git.BranchBases().GetValueOrDefault(name) ?? throw new SgException($"{name} is not a branch of a checkout here.");
            var co = root.Checkout(coName);
            var snapshot = git.RefSha(root.SnapshotRef(co)) ?? throw new SgException($"no snapshot of {co.Name} yet. Run: sg sync {co.Name}");
            res.Branch = name;
            res.Path = worktree;
            res.Checkout = co.Name;

            var specs = new List<string>();
            if (remote.ContainsKey(branchRef)) specs.Add("+" + branchRef + ":" + FetchedRef("branch", name));
            if (cfg.Uncommitted && remote.ContainsKey(wipRef)) specs.Add("+" + wipRef + ":" + FetchedRef("wip", name));
            if (specs.Count == 0) throw new SgException($"the backup holds nothing named {name}. sg backup list says what is there.");
            git.FetchRefs(cfg.Url, specs);

            if (remote.ContainsKey(branchRef))
            {
                var chain = Thin.Chain(git, git.RefSha(FetchedRef("branch", name))!);
                if (chain.Count == 0 || chain[0].Kind != ThinKind.Marker) throw new SgException($"{name} on the remote is not an sg backup: it does not start with a marker.");
                if (chain[0].Version > Thin.Version)
                    throw new SgException($"{name} was backed up by a newer sg (format {chain[0].Version}, this one reads {Thin.Version}). Update sg, then pull it.");
                var theirs = RemoteChanges(chain);
                var ours = LocalChanges(git, git.MergeBase(tip, snapshot) ?? snapshot, tip);
                var shared = SharedPrefix(theirs, ours);
                if (shared < ours.Count)
                    throw new SgException($"{name} here holds {ours.Count - shared} commit(s) the backup does not ({Newest(ours.Skip(shared))}), so its commits do not go on top. "
                                          + $"Back up to send them, or restore the backup's copy under another name to compare: sg backup restore {name} --name {name}-remote");
                var changes = chain.Where(c => c.Kind == ThinKind.Change).Skip(shared).ToList();
                res.Commits = changes.Count;
                var newTip = tip;
                foreach (var c in changes)
                {
                    var under = git.ParentOf(c.Sha) ?? throw new SgException("a change commit with nothing under it: " + c.Sha);
                    var m = git.MergeTree(under, newTip, c.Sha);
                    if (!m.Clean)
                    {
                        res.Stopped = c.Subject;
                        res.Conflicted = m.Conflicted;
                        res.Why = m.Messages;
                        break;
                    }
                    newTip = git.CommitTreeAs(m.Tree, newTip, Thin.Original(c.Body), c.Author);
                    res.Applied++;
                }
                if (!res.Ok)
                {
                    BeginReplay(root, res, changes, cfg.Uncommitted && remote.ContainsKey(wipRef) ? git.RefSha(FetchedRef("wip", name)) : null, pull: true);
                }
                else if (newTip != tip)
                {
                    // A fast-forward: the files the new commits change are written, and a local change to one of
                    // them stops it before anything is touched.
                    var ff = git.Run(worktree, "merge", "--ff-only", newTip);
                    if (!ff.Ok)
                        throw new SgException($"the worktree of {name} has changes to files the pulled commits change, so nothing moved. "
                                              + "Commit or shelve them, then pull again. " + ff.StdErr.Split('\n')[0].Trim());
                }
            }
            if (res.Ok && cfg.Uncommitted && remote.ContainsKey(wipRef))
                PullChanges(root, cfg, res, git.RefSha(FetchedRef("wip", name))!, worktree, co, name, PushedRef("wip", name));
            // What the remote had over this machine is here now, or as much of it as merged: the card stops
            // offering the pull, and the next backup says where the two stand.
            if (res.Ok)
            {
                git.ConfigUnset(KeyRemote(name));
                Operations.Receipt(root, "Get changes from backup", worktree, ["Incoming branch: " + name, "Applied commits: " + res.Applied, res.WipWhy ?? "Review local edits and saved shelves."]);
            }
            return res;
        }

        if (root.Config.Checkouts.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } checkout && remote.ContainsKey(editsRef))
        {
            git.FetchRefs(cfg.Url, ["+" + editsRef + ":" + FetchedRef("edits", checkout.Name)]);
            res.Checkout = checkout.Name;
            res.Path = checkout.Path;
            PullChanges(root, cfg, res, git.RefSha(FetchedRef("edits", checkout.Name))!, checkout.Path, checkout, null, PushedRef("edits", checkout.Name));
            return res;
        }
        throw new SgException($"nothing here to pull {name} into: no branch and no checkout of that name with anything in the backup. "
                              + $"Restore makes the branch: sg backup restore {name}");
    }

    /// <summary>
    /// A copy's uncommitted changes into a folder here, as the verdict decides. Afterwards this root may send
    /// the folder's own over the copy: what the copy held is in the folder or on a shelf, which goes up too.
    /// </summary>
    static void PullChanges(SgRoot root, BackupConfig cfg, RestoreResult res, string thin, string folder, CheckoutConfig co, string? branch, string pushedRef)
    {
        var git = root.Git;
        var chain = Thin.Chain(git, thin);
        var head = git.HeadSha(folder);
        Verdict verdict;
        string? why;
        try
        {
            var local = Shelf.Wip(root, folder, cfg.MaxFileBytes, cfg.MaxPushBytes);
            (verdict, why) = ReconcileChanges(git, local?.Sha ?? head, chain);
        }
        catch (SgException e)
        {
            // The folder's own changes could not be read, so what they hold is not known: the copy goes on a shelf, unwritten.
            (verdict, why) = (Verdict.Diverged, e.Message);
        }
        if (verdict is Verdict.Same or Verdict.Ahead) res.WipAlreadyHere = true;
        else
        {
            Adopt(root, res, thin, head, folder, co, branch, write: verdict == Verdict.Behind);
            if (verdict != Verdict.Behind) res.WipWhy = why;
        }
        git.UpdateRef(pushedRef, thin);
    }
}
