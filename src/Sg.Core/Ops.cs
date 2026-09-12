using System.Text;
using System.Text.RegularExpressions;

namespace Sg.Core;

public sealed record CheckoutResult(CheckoutConfig Checkout, SnapshotInfo Snapshot);

public sealed class SyncResult
{
    public string Checkout = "";
    public long Revision;
    public string Sha = "";
    public List<ExternalInfo> Externals = new();
    public int Overlaid;
    public int Conflicts;
    public bool Changed;
    /// <summary>Externals sync kept pointed somewhere other than svn:externals declares.</summary>
    public List<string> KeptSwitched = new();
    public List<string> Warnings = new();
}

public sealed record BranchResult(string Branch, string Path, string Checkout, List<string> Excluded, List<string> Shared, SharedMode SharedMode);

public sealed class RebaseResult
{
    public string Branch = "";
    public string Checkout = "";
    public bool Ok;
    public bool Conflict;
    public int Ahead;
    public string Output = "";
    /// <summary>Shared folders of a clone or copy worktree that were mirrored from the checkout after the rebase.</summary>
    public List<string> Refreshed = new();
}

public sealed class CheckoutStatus
{
    public string Name = "";
    public string Path = "";
    public string Url = "";
    public long Revision;
    public string Snapshot = "";
    /// <summary>When the snapshot was taken: the last sync. Null before the first one.</summary>
    public DateTimeOffset? SnapshotTaken;
    public int? LocalEdits;
    /// <summary>Sets of local changes put aside from this checkout, waiting to be taken back.</summary>
    public int Shelves;
    public List<ExternalInfo> Externals = new();
}

public sealed class RemoteEntry
{
    public string Rel = "";
    public string Url = "";
    public long Snapshot;
    public long Server;
    public int Commits;
    public bool Behind => Server > Snapshot;
}

/// <summary>What the server has that the snapshot does not. One entry for the root and one per external.</summary>
public sealed class RemoteCheckResult
{
    public string Checkout = "";
    public List<RemoteEntry> Entries = new();
    public bool Behind => Entries.Any(e => e.Behind);
    public int Commits => Entries.Sum(e => e.Commits);
}

public sealed class WorktreeStatus
{
    public string Branch = "";
    public string Path = "";
    public string Base = "";
    public int Ahead;
    /// <summary>Snapshots on the base that the branch does not have yet.</summary>
    public int Behind;
    public bool NeedsRebase;
    /// <summary>A rebase or an import stopped here. Conflicts holds the files it left in conflict.</summary>
    public Replay Stopped;

    /// <summary>Something is half done in this worktree, whichever of the two it is.</summary>
    public bool RebaseInProgress => Stopped != Replay.None;

    public int Conflicts;
    public bool Dirty;
    /// <summary>How many tracked files are changed and not committed. Untracked files are not in it.</summary>
    public int DirtyFiles;
    public bool Pending;
    public bool Missing;
    /// <summary>How its shared folders were made: junction, clone or copy. Empty for a worktree older than the choice.</summary>
    public string Shared = "";
    /// <summary>Sets of changes put aside from this worktree, waiting to be taken back.</summary>
    public int Shelves;
    /// <summary>Commits the backup does not hold: 0 when it holds the tip, -1 when the branch was never backed up.</summary>
    public int NotBackedUp = -1;
    /// <summary>When the backup last held this branch.</summary>
    public DateTimeOffset? BackedUp;
}

public sealed class StatusResult
{
    public string Root = "";
    /// <summary>Where backups go, when somewhere.</summary>
    public string? BackupUrl;
    public List<CheckoutStatus> Checkouts = new();
    public List<WorktreeStatus> Worktrees = new();
}

/// <summary>The bridge operations. Push lives in its own file.</summary>
public static class Ops
{
    static readonly (string Key, string Value)[] StoreConfig =
    {
        ("core.autocrlf", "false"),
        ("core.safecrlf", "false"),
        ("core.filemode", "false"),
        ("core.longpaths", "true"),
        ("core.symlinks", "false"),
        ("core.untrackedCache", "true"),
        ("feature.manyFiles", "true"),
        ("core.bigFileThreshold", "1m"),
        ("core.looseCompression", "0"),
        ("gc.auto", "0"),
        ("core.quotePath", "false"),
    };

    // ---- init ----

    public static SgRoot Init(string rootPath, ILog log, bool fsmonitor = true, string gitExe = "git", string svnExe = "svn")
    {
        rootPath = Path.GetFullPath(rootPath).TrimEnd('\\', '/');
        if (Directory.Exists(Path.Combine(rootPath, ".sg"))) throw new SgException("already an sg root: " + rootPath);
        Directory.CreateDirectory(rootPath);
        var cfg = new SgConfig { Root = rootPath, GitExe = gitExe, SvnExe = svnExe };
        var root = SgRoot.Create(rootPath, cfg, log);
        root.Git.InitBare();
        foreach (var (k, v) in StoreConfig) root.Git.Config(k, v);
        if (fsmonitor) root.Git.Config("core.fsmonitor", "true");
        root.Git.EnsureRootCommit();
        root.RefreshExcludes();
        log.Info("sg root ready: " + rootPath);
        return root;
    }

    // ---- checkout add ----

    /// <summary>
    /// A checkout that does not exist yet: svn checkout the URL into a folder, then register it like
    /// any other. The name defaults to the last part of the URL, and the folder to the root's own
    /// folder plus the name, which is where server-checkout puts its copies too. Skip, junctions and
    /// optional are typed, not picked, because there is nothing on disk to pick from yet.
    /// </summary>
    public static CheckoutResult CheckoutFromUrl(SgRoot root, string url, string? folder = null, IEnumerable<string>? skip = null,
        IEnumerable<string>? junctions = null, IEnumerable<string>? optional = null, string? name = null, SharedMode? shared = null)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.Contains("://")) throw new SgException("give a full SVN URL, like http://svn/repo/branches/x: " + url);
        var last = url.Split('/').Last();
        name ??= last;
        root.Git.CheckBranchName(name);
        folder = Path.GetFullPath(folder ?? Path.Combine(root.RootPath, name)).TrimEnd('\\', '/');
        if (File.Exists(folder)) throw new SgException("a file is in the way: " + folder);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new SgException("folder exists and is not empty: " + folder + ". Register it with 'checkout add' if it is already a working copy.");
        if (root.Config.Checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new SgException("checkout name already in use: " + name);
        if (!root.Svn.UrlExists(url)) throw new SgException("not on the server: " + url);

        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        root.Log.Info($"svn checkout {url} into {folder}");
        var r = root.Svn.Checkout(url, folder);
        if (r.Conflicts > 0) root.Log.Warn($"{r.Conflicts} conflict(s) after the checkout, check svn status in {folder}");
        return CheckoutAdd(root, folder, skip, junctions, optional, name, shared);
    }

    public static CheckoutResult CheckoutAdd(SgRoot root, string folder, IEnumerable<string>? skip = null,
        IEnumerable<string>? junctions = null, IEnumerable<string>? optional = null, string? name = null, SharedMode? shared = null)
    {
        folder = Path.GetFullPath(folder).TrimEnd('\\', '/');
        if (!Directory.Exists(folder)) throw new SgException("no such folder: " + folder);
        name ??= Path.GetFileName(folder);
        root.Git.CheckBranchName(name);
        if (root.Config.Checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new SgException("checkout name already in use: " + name);
        if (root.Config.Checkouts.Any(c => c.Path.TrimEnd('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase)))
            throw new SgException("folder already registered: " + folder);
        var gitFile = Path.Combine(folder, ".git");
        if (File.Exists(gitFile) || Directory.Exists(gitFile)) throw new SgException(folder + " already has a .git. Remove it first.");

        var info = root.Svn.Info(folder, ".");
        using var _ = root.Lock();

        var co = new CheckoutConfig
        {
            Name = name,
            Path = folder,
            Url = info.Url,
            ReposRoot = info.ReposRoot,
            Skip = Clean(skip),
            Junctions = Clean(junctions),
            Shared = shared ?? SharedMode.Junction,
            Optional = Clean(optional),
        };
        foreach (var j in co.Junctions)
            if (!co.Skip.Contains(j, StringComparer.OrdinalIgnoreCase)) co.Skip.Add(j);

        // git worktree add refuses a folder that has files. Add it elsewhere, then move the .git file in.
        root.Git.WorktreePrune();
        var tmpParent = root.NewTempDir();
        var tmp = Path.Combine(tmpParent, name);
        try
        {
            root.Git.WorktreeAddDetachedNoCheckout(tmp, SgRoot.RootRef);
            File.Move(Path.Combine(tmp, ".git"), gitFile);
        }
        finally
        {
            try { Directory.Delete(tmpParent, true); } catch { /* best effort */ }
        }
        root.Git.WorktreeRepair(folder);

        root.Config.Checkouts.Add(co);
        root.Save();
        root.Log.Info($"first snapshot of {name}: every file is hashed once, this can take a while");
        SnapshotInfo snap;
        try
        {
            snap = Snapshot.Build(root, co, null);
        }
        catch (Exception)
        {
            // Stopping here must not leave a checkout registered with no snapshot behind it.
            root.Config.Checkouts.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            root.Save();
            try { File.Delete(gitFile); } catch (IOException) { /* leave it, the message says what happened */ }
            try { root.Git.WorktreePrune(); } catch (SgException) { /* best effort */ }
            root.Log.Warn($"{name} was not registered: the first snapshot did not finish");
            throw;
        }
        root.RefreshExcludes();
        return new CheckoutResult(co, snap);
    }

    static List<string> Clean(IEnumerable<string>? paths) =>
        (paths ?? []).Select(PathUtil.Rel).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ---- editing a registered checkout ----

    /// <summary>What may be changed about a checkout after it is registered. Null leaves a field alone.</summary>
    public sealed record CheckoutEdit(string? Name = null, List<string>? Skip = null,
        List<string>? Junctions = null, List<string>? Optional = null, List<string>? PushOrder = null, SharedMode? Shared = null);

    /// <summary>
    /// Changes a registered checkout in place. The name is the interesting one: it is the snapshot ref,
    /// so renaming moves refs/remotes/svn/&lt;name&gt;, re-points every branch that was born from it, and
    /// renames the cached ignore file. Nothing here touches SVN, and nothing re-hashes the checkout.
    /// </summary>
    public static void UpdateCheckout(SgRoot root, CheckoutConfig co, CheckoutEdit edit)
    {
        using var _ = root.Lock();
        var git = root.Git;

        if (edit.Name != null && !edit.Name.Equals(co.Name, StringComparison.Ordinal))
        {
            var name = edit.Name.Trim();
            if (name.Length == 0) throw new SgException("a checkout needs a name");
            git.CheckBranchName(name);
            if (root.Config.Checkouts.Any(c => !ReferenceEquals(c, co) && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new SgException("checkout name already in use: " + name);

            var oldRef = root.SnapshotRef(co);
            var oldIgnores = root.IgnoreFileFor(co);
            var sha = git.RefSha(oldRef);
            var oldName = co.Name;
            co.Name = name;

            if (sha != null)
            {
                // Move the ref before anything reads it under the new name, and only then drop the old.
                git.UpdateRef(root.SnapshotRef(co), sha);
                git.DeleteRef(oldRef);
            }
            // Every branch born from this checkout remembers it by name.
            foreach (var (branch, baseName) in git.BranchBases())
                if (baseName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                    git.Config($"branch.{branch}.sgBase", name);

            var newIgnores = root.IgnoreFileFor(co);
            try
            {
                if (File.Exists(oldIgnores) && !File.Exists(newIgnores)) File.Move(oldIgnores, newIgnores);
            }
            catch (IOException) { /* RefreshExcludes writes it again below */ }
            root.Log.Info($"checkout {oldName} is now {name}");
        }

        if (edit.Skip != null) co.Skip = Clean(edit.Skip);
        if (edit.Junctions != null) co.Junctions = Clean(edit.Junctions);
        if (edit.Optional != null) co.Optional = Clean(edit.Optional);
        if (edit.PushOrder != null) co.PushOrder = Clean(edit.PushOrder);
        if (edit.Shared != null) co.Shared = edit.Shared.Value;
        // A shared folder is never stored, the way checkout add arranges it.
        foreach (var j in co.Junctions)
            if (!co.Skip.Contains(j, StringComparer.OrdinalIgnoreCase)) co.Skip.Add(j);

        root.Save();
        if (edit.Skip != null || edit.Junctions != null || edit.Name != null) root.RefreshExcludes();
    }

    // ---- externals ----

    /// <summary>Where one external of a checkout currently points, and where its property says it should.</summary>
    public sealed class ExternalState
    {
        /// <summary>Path relative to the checkout. Never empty: the root is not an external.</summary>
        public string Rel = "";
        /// <summary>Where this working copy is pointed right now.</summary>
        public string Url = "";
        /// <summary>Where svn:externals says it goes. Empty when it could not be read.</summary>
        public string Declared = "";
        public long Revision;
        public string ReposRoot = "";
        /// <summary>Switched away from what the property declares, here and not on the server.</summary>
        public bool Switched => Declared.Length > 0 && !Url.TrimEnd('/').Equals(Declared.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every external of a checkout: where it points now, and where the committed svn:externals says it
    /// should. The two differ when someone switched one locally, which is a working copy state and not
    /// a commit anyone else sees.
    /// </summary>
    public static List<ExternalState> ExternalsOf(SgRoot root, CheckoutConfig co)
    {
        var svn = root.Svn;
        var rels = svn.Status(co.Path, noIgnore: false)
            .Where(e => e.Item == "external" && e.Path.Length > 0)
            .Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (rels.Count == 0) return new();

        var infos = svn.InfoMany(co.Path, rels, recursive: false);
        var declared = DeclaredExternals(root, co);
        var res = new List<ExternalState>();
        foreach (var rel in rels)
        {
            var info = infos.FirstOrDefault(i => i.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (info == null) continue;
            res.Add(new ExternalState
            {
                Rel = rel,
                Url = info.Url,
                Revision = info.Revision,
                ReposRoot = info.ReposRoot,
                Declared = declared.GetValueOrDefault(rel, ""),
            });
        }
        return res;
    }

    /// <summary>
    /// What the svn:externals properties say, resolved to absolute URLs and keyed by the path the
    /// external lands on. Externals.Definitions does the reading: those lines carry quoting, peg
    /// revisions and two historic orderings, and there is no second parser for them.
    /// </summary>
    static Dictionary<string, string> DeclaredExternals(SgRoot root, CheckoutConfig co)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<(string Dir, string Value)> props;
        try { props = root.Svn.PropGetRecursive(co.Path, "svn:externals"); }
        catch (SgException) { return res; }

        foreach (var (dir, value) in props)
            foreach (var (url, name) in Externals.Definitions(value))
            {
                if (name.Length == 0) continue;
                var rel = PathUtil.Rel(dir.Length == 0 ? name : dir + "/" + name);
                res[rel] = Absolute(url, co);
            }
        return res;
    }

    /// <summary>svn:externals may point at the repository root with ^, or give a whole URL.</summary>
    static string Absolute(string url, CheckoutConfig co) =>
        url.StartsWith('^') ? co.ReposRoot.TrimEnd('/') + url[1..] : url;

    /// <summary>
    /// The branches a URL could be pointed at: the siblings of its own branch on the server. A URL
    /// under branches/&lt;x&gt; lists that branches folder; one under trunk lists the branches folder
    /// beside it. Empty when the repository does not follow either shape.
    /// </summary>
    public static List<string> BranchNames(SgRoot root, string url)
    {
        var parts = url.TrimEnd('/').Split('/');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i] == "branches" && i + 1 < parts.Length)
                return root.Svn.ListDirs(string.Join("/", parts[..(i + 1)]));
            if (parts[i] == "trunk")
                return root.Svn.ListDirs(string.Join("/", parts[..i].Append("branches")));
        }
        return new();
    }

    /// <summary>The same URL, pointed at another branch. The rule server branches already use.</summary>
    public static string UrlForBranch(SgRoot root, string url, string branch) =>
        BranchRule.NewUrl(url.TrimEnd('/'), branch, root.Config.BranchUrlOverrides);

    /// <summary>
    /// Points one external at another URL, here only. This is svn switch on the external's own working
    /// copy: the svn:externals property is untouched, so nothing is committed and nobody else sees it.
    /// The next sync records the new content and the new URL in the snapshot, which is what makes a
    /// branch built on this checkout build against the switched external. Sync has to put the switch
    /// back itself after its svn update, because svn update undoes one; see <see cref="Reswitch"/>.
    /// </summary>
    public static SvnUpdateResult SwitchExternal(SgRoot root, CheckoutConfig co, string rel, string url)
    {
        rel = PathUtil.Rel(rel);
        if (rel.Length == 0) throw new SgException("the checkout root is not an external");
        url = url.Trim().TrimEnd('/');
        if (url.Length == 0) throw new SgException("give the URL to point it at");

        var dir = PathUtil.Join(co.Path, rel);
        if (!Directory.Exists(dir)) throw new SgException("no such external in the checkout: " + rel);
        using var _ = root.Lock();
        root.Log.Info($"switching {rel} to {url}");
        var r = root.Svn.Switch(dir, url);
        root.Log.Info($"{rel} is now at {url}" + (r.Revision.HasValue ? $", r{r.Revision}" : ""));
        return r;
    }

    // ---- sync ----

    public static SyncResult Sync(SgRoot root, CheckoutConfig co)
    {
        using var _ = root.Lock();
        var git = root.Git;
        var svn = root.Svn;
        var snapRef = root.SnapshotRef(co);
        var prevSha = git.RefSha(snapRef);
        var prev = prevSha != null ? SnapshotMeta.Parse(git.Body(prevSha)) : null;

        // svn update processes externals, and that step points a switched one back at whatever
        // svn:externals declares, deleting what only existed on the branch it was switched to. It
        // says nothing about having done it. Read them first, and take the update that keeps them.
        var switched = SwitchedExternals(root, co);

        var upd = switched.Count == 0 ? svn.Update(co.Path) : UpdateKeepingSwitches(root, co);
        root.Log.Info($"svn update {co.Name}: " + (upd.Revision.HasValue ? "r" + upd.Revision : "done")
                      + (upd.Conflicts > 0 ? $", {upd.Conflicts} conflict(s) in the checkout" : ""));

        // The safety net under both paths, and before the snapshot, so the snapshot records the
        // switched content, which is the point of switching one.
        Reswitch(root, co, switched);

        var snap = Snapshot.Build(root, co, prevSha, info => LogsSince(root, co, prev, info));
        return new SyncResult
        {
            Checkout = co.Name,
            Revision = snap.Revision,
            Sha = snap.Sha,
            Externals = snap.Externals,
            Overlaid = snap.Overlaid,
            Conflicts = upd.Conflicts,
            Changed = !snap.Unchanged,
            KeptSwitched = switched.Select(s => s.Rel).ToList(),
            Warnings = snap.Warnings,
        };
    }

    /// <summary>
    /// The update for a checkout that has a switched external. A plain svn update points it home and
    /// then sync points it away again: the whole difference between the two branches crosses the wire
    /// twice. On a builds folder over a VPN that is tens of gigabytes and hours, so it is not a price
    /// worth paying for the common case.
    ///
    /// Instead the root goes first on its own, and each external is updated inside its own working
    /// copy, which leaves the URL it sits on alone. That is only safe while svn has no external work
    /// of its own to do, so the svn:externals text has to come out of the root update exactly as it
    /// went in, with no pinned revisions in it. Anything else falls back to the plain update, and
    /// <see cref="Reswitch"/> puts the switches back after it.
    /// </summary>
    static SvnUpdateResult UpdateKeepingSwitches(SgRoot root, CheckoutConfig co)
    {
        var before = ExternalProps(root, co);
        var upd = root.Svn.Update(co.Path, ignoreExternals: true);
        var after = ExternalProps(root, co);

        var blocker = Blocker(before, after);
        if (blocker != null)
        {
            root.Log.Warn($"{blocker}, so svn has to place the externals itself. "
                          + "It will pull the switched ones home first and sync will send them back out, which transfers each of them twice.");
            return root.Svn.Update(co.Path);
        }

        foreach (var rel in ExternalDirs(root, co))
        {
            var dir = PathUtil.Join(co.Path, rel);
            if (!Directory.Exists(dir))
            {
                root.Log.Warn($"svn:externals declares {rel} and there is no folder there. Sync leaves it alone.");
                continue;
            }
            // Its own working copy, so this updates whatever URL it is on. That is the whole trick.
            upd.Conflicts += root.Svn.Update(dir).Conflicts;
        }
        return upd;
    }

    /// <summary>
    /// Why the cheap update cannot be used, or null when it can. It compares the property text and
    /// nothing else: resolving the URLs would drag in the relative forms and the pegs that
    /// <see cref="Externals.Definitions"/> does not carry, and a wrong answer here would leave a
    /// folder missing rather than a label wrong.
    /// </summary>
    static string? Blocker(string? before, string? after)
    {
        if (before == null || after == null) return "svn:externals could not be read";
        if (!string.Equals(before, after, StringComparison.Ordinal)) return "svn:externals changed on the server";
        // -r 123 or @123 pins an external to one revision, and updating it in place would move it to head.
        if (Regex.IsMatch(after, @"(?:^|\s)-r\s*\d+|@\d+(?:\s|$)", RegexOptions.Multiline)) return "an external is pinned to a revision";
        return null;
    }

    /// <summary>Every svn:externals value in the checkout as one comparable string, or null if unreadable.</summary>
    static string? ExternalProps(SgRoot root, CheckoutConfig co)
    {
        try
        {
            return string.Join("\n", root.Svn.PropGetRecursive(co.Path, "svn:externals")
                .OrderBy(p => p.Dir, StringComparer.Ordinal)
                .Select(p => p.Dir + " " + p.Value.Replace("\r\n", "\n").Trim()));
        }
        catch (SgException) { return null; }
    }

    /// <summary>Where the externals land. Only the paths: the URLs are svn's business, not sync's.</summary>
    static List<string> ExternalDirs(SgRoot root, CheckoutConfig co)
    {
        try { return DeclaredExternals(root, co).Keys.OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch (SgException) { return new(); }
    }

    /// <summary>
    /// The externals pointed somewhere other than svn:externals declares. Reads the properties and
    /// the URLs only: ExternalsOf walks the whole working copy for its status, and this runs on every
    /// sync of a 173k file checkout.
    /// </summary>
    static List<(string Rel, string Url)> SwitchedExternals(SgRoot root, CheckoutConfig co)
    {
        Dictionary<string, string> declared;
        try { declared = DeclaredExternals(root, co); }
        catch (SgException) { return new(); }
        var present = declared.Keys.Where(rel => Directory.Exists(PathUtil.Join(co.Path, rel))).ToList();
        if (present.Count == 0) return new();

        var res = new List<(string, string)>();
        try
        {
            foreach (var info in root.Svn.InfoMany(co.Path, present, recursive: false))
            {
                var want = declared.GetValueOrDefault(info.Path, "");
                if (want.Length == 0) continue;
                if (!info.Url.TrimEnd('/').Equals(want.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    res.Add((info.Path, info.Url));
            }
        }
        catch (SgException) { return new(); }
        return res;
    }

    /// <summary>
    /// Points back the externals the update pulled home. This transfers the same difference a second
    /// time, which is the price of letting svn keep doing everything else it does to externals:
    /// checking out ones the server added, dropping ones it removed, following a changed definition.
    /// It only runs where the user deliberately pointed an external somewhere else.
    /// </summary>
    static List<string> Reswitch(SgRoot root, CheckoutConfig co, List<(string Rel, string Url)> switched)
    {
        var done = new List<string>();
        foreach (var (rel, url) in switched)
        {
            var dir = PathUtil.Join(co.Path, rel);
            if (!Directory.Exists(dir))
            {
                root.Log.Warn($"{rel} was switched to {url}, and the update left no folder there. It stays as it is.");
                continue;
            }
            string now;
            try { now = root.Svn.Info(co.Path, rel).Url; }
            catch (SgException) { continue; }
            // An update that left it alone needs nothing; only a real move is worth a second transfer.
            if (now.TrimEnd('/').Equals(url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) continue;

            root.Log.Info($"svn update put {rel} back on {now}; switching it to {url} again");
            try { root.Svn.Switch(dir, url); }
            catch (SgException e)
            {
                root.Log.Warn($"could not switch {rel} back to {url}: {e.Message}");
                continue;
            }
            done.Add(rel);
        }
        return done;
    }

    static string LogsSince(SgRoot root, CheckoutConfig co, SnapshotMeta? prev, SnapshotInfo info)
    {
        if (prev == null) return "";
        // One section per repository that moved, and each is a round trip to the server. They all go
        // out together, and the message is built from the answers in the order the sections belong in.
        var wanted = new List<(string Label, string Target, long From, long To)>();
        if (info.Revision > prev.Revision) wanted.Add(("root", ".", prev.Revision, info.Revision));
        foreach (var e in info.Externals)
            if (prev.Externals.TryGetValue(e.Rel, out var pr) && e.Revision > pr) wanted.Add((e.Rel, e.Rel, pr, e.Revision));
        if (wanted.Count == 0) return "";

        var logs = Fan.Map(wanted, w => root.Svn.Log(co.Path, w.Target, w.From + 1, w.To, 30));
        var sb = new StringBuilder();
        var lines = 0;
        for (var i = 0; i < wanted.Count && lines <= 200; i++)
        {
            if (logs[i].Count == 0) continue;
            var (label, _, from, to) = wanted[i];
            sb.Append("== ").Append(label).Append(" r").Append(from + 1).Append("..r").Append(to).Append('\n');
            foreach (var e in logs[i])
            {
                sb.Append('r').Append(e.Revision).Append(' ').Append(e.Author).Append(": ").Append(e.Message.Split('\n')[0].Trim()).Append('\n');
                lines++;
            }
        }
        return sb.ToString();
    }

    // ---- branch ----

    /// <summary>
    /// Why the name cannot be used, in the words of what is actually there. "branch exists: graph" left
    /// three questions unanswered: is something using it, would removing it lose anything, and which
    /// command fixes it. The commonest way to see this is a removal that stopped half way, and that case
    /// looks identical to a branch someone is working in unless the message separates them.
    /// </summary>
    static string NameTaken(SgRoot root, string name)
    {
        var git = root.Git;
        var wt = git.WorktreeList().FirstOrDefault(w => w.Branch == name);
        if (wt != null && Directory.Exists(wt.Path))
            return $"branch \"{name}\" is already checked out at {wt.Path}. "
                   + $"Work in that folder, or remove it first with: sg rm {name}";
        if (wt != null)
            return $"branch \"{name}\" still claims the folder {wt.Path}, which is not on disk any more. "
                   + $"A removal stopped half way. Finish it with: sg rm {name}";

        // A branch with no worktree at all: the folder went and the branch did not. Whether that matters
        // is whether anything on it never reached SVN, so the message counts before it advises.
        var unpushed = 0;
        var basedOn = git.BranchBases().GetValueOrDefault(name);
        var snap = basedOn == null ? null : root.Config.Checkouts
            .FirstOrDefault(c => c.Name.Equals(basedOn, StringComparison.OrdinalIgnoreCase));
        if (snap != null)
        {
            try { unpushed = git.RevList(null, $"{root.SnapshotRef(snap)}..refs/heads/{name}").Count; }
            catch (SgException) { /* the count is a courtesy; the collision is the message */ }
        }
        return $"branch \"{name}\" exists with no worktree, left behind by a removal that did not delete it. "
               + (unpushed > 0
                   ? $"It still has {unpushed} commit(s) that never reached SVN, so removing it would lose them. "
                     + $"Push it, export it with \"sg export {name}\", or use another name."
                   : $"Nothing on it is unpushed. Remove it with: sg rm {name} — or use another name.");
    }

    /// <summary>Why the folder cannot be used. A live worktree and an abandoned folder want different answers.</summary>
    static string FolderTaken(SgRoot root, string dir, string name)
    {
        var owner = root.Git.WorktreeList().FirstOrDefault(w => w.Path.Equals(dir, StringComparison.OrdinalIgnoreCase));
        if (owner != null)
            return $"folder exists: {dir}, and worktree \"{owner.Branch ?? "(detached)"}\" is using it. "
                   + $"Remove that worktree first, or give the branch another name.";
        return File.Exists(dir)
            ? $"a file is in the way: {dir}. Move or delete it, or use another name than \"{name}\"."
            : $"folder exists: {dir}, and no worktree is using it — it was left behind. "
              + $"Delete the folder, or use another name than \"{name}\".";
    }

    /// <summary>
    /// A branch and its worktree. shared says how the checkout's shared folders come along: null takes
    /// the checkout's setting. A clone is refused up front, before anything is made, when the volumes
    /// cannot do it; the reason names the volumes so the fix is obvious.
    /// </summary>
    public static BranchResult Branch(SgRoot root, string name, CheckoutConfig co, IEnumerable<string>? without = null, bool minimal = false,
        SharedMode? shared = null)
    {
        var git = root.Git;
        git.CheckBranchName(name);
        if (git.RefSha("refs/heads/" + name) != null) throw new SgException(NameTaken(root, name));
        var snapRef = root.SnapshotRef(co);
        if (git.RefSha(snapRef) == null) throw new SgException($"no snapshot of {co.Name} yet. Run: sg sync {co.Name}");
        var dir = root.WorktreePathFor(name);
        if (Directory.Exists(dir) || File.Exists(dir)) throw new SgException(FolderTaken(root, dir, name));
        var mode = shared ?? co.Shared;
        if (mode == SharedMode.Clone && co.Junctions.Count > 0)
        {
            var problem = SharedFolders.CloneProblem(co.Path, Path.GetDirectoryName(dir)!);
            if (problem != null) throw new SgException("cannot clone the shared folders: " + problem + ". Use --shared junction or --shared copy.");
        }
        using var _ = root.Lock();

        var excluded = Clean(without);
        if (minimal) excluded.AddRange(co.Optional.Where(o => !excluded.Contains(o, StringComparer.OrdinalIgnoreCase)));

        git.WorktreeAddBranchNoCheckout(dir, name, snapRef);
        if (excluded.Count > 0) git.SparseSetCone(dir, ConeDirsExcluding(git, snapRef, excluded));
        root.Log.Info("writing files into " + dir);
        git.CheckoutForceProgress(dir, name, (n, t) => root.Log.Progress("writing files", n, t, "files", null));
        root.Log.ProgressEnd("writing files", null);

        var made = SharedFolders.Populate(root.Log, co, dir, mode);
        AgentNotes.Write(dir, co, made, mode);
        root.WriteWorktreeGitignore(co, dir);
        git.Config($"branch.{name}.sgBase", co.Name);
        git.Config($"branch.{name}.sgShared", SharedFolders.Name(mode));
        return new BranchResult(name, dir, co.Name, excluded, made, mode);
    }

    /// <summary>Cone-mode directories that give "everything except these folders".</summary>
    static List<string> ConeDirsExcluding(Git git, string treeish, List<string> excluded)
    {
        var result = new List<string>();
        void Expand(string dir)
        {
            foreach (var e in git.LsTreeChildren(treeish, dir).Where(e => e.Type == "tree"))
            {
                var rel = e.Path;
                if (excluded.Any(x => x.Equals(rel, StringComparison.OrdinalIgnoreCase))) continue;
                if (excluded.Any(x => PathUtil.IsUnder(x, rel) && !x.Equals(rel, StringComparison.OrdinalIgnoreCase))) Expand(rel);
                else result.Add(rel);
            }
        }
        Expand("");
        return result;
    }

    // ---- rewriting the branch's own commits ----

    /// <summary>What a squash or a reword left behind.</summary>
    public sealed record RewriteResult(string Branch, string Sha, int Replaced);

    /// <summary>
    /// One run of commits, ready to be replaced: the branch it sits on, its ends, and the commits above
    /// it that will have to be replayed. Every rule about what may be rewritten is checked here.
    /// </summary>
    sealed record Rewritable(string Worktree, string Branch, string Oldest, string Newest, bool AtTip, List<string> Messages);

    /// <summary>
    /// The commits a rewrite is allowed to touch, or an error saying why not. Only the branch's own
    /// commits qualify: a snapshot is what SVN said, and rewriting one would make the branch a lie
    /// about where it came from. The run has to be unbroken, because a commit in the middle of it
    /// cannot be kept while the ones around it go.
    /// </summary>
    static Rewritable Check(SgRoot root, string worktree, IReadOnlyList<string> shas)
    {
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var branch = git.CurrentBranch(worktree);
        var snapRef = root.SnapshotRef(BaseCheckout(root, branch));

        if (shas.Count == 0) throw new SgException("no commits were picked");
        if (git.RebaseInProgress(worktree))
            throw new SgException(Conflicts.Note(git, worktree) + ". " + Conflicts.Where);
        if (!git.IsClean(worktree))
            throw new SgException("the worktree has uncommitted changes: " + worktree
                + "\nRewriting commits moves the branch out from under them. Commit them first, or discard them.");

        // The branch's own commits, newest first. Anything not in here is a snapshot or another branch's.
        var own = git.RevList(worktree, snapRef + "..HEAD");
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < own.Count; i++) at[own[i]] = i;

        var picked = new List<int>();
        foreach (var sha in shas)
        {
            if (!at.TryGetValue(sha, out var i))
                throw new SgException($"{Short(sha)} is not one of this branch's own commits. Snapshots of SVN cannot be rewritten.");
            picked.Add(i);
        }
        picked.Sort();
        for (var i = 1; i < picked.Count; i++)
            if (picked[i] != picked[i - 1] + 1)
                throw new SgException("the picked commits do not sit next to each other. Squashing joins one unbroken run of commits, with nothing left in between.");

        var newest = own[picked[0]];
        var oldest = own[picked[^1]];
        var messages = picked.Select(i => git.Body(own[i]).TrimEnd()).Reverse().ToList();   // oldest first, the way git squashes
        return new Rewritable(worktree, branch, oldest, newest, picked[0] == 0, messages);
    }

    static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;

    /// <summary>
    /// The message a squash starts from: every message in the run, oldest first, the way
    /// "git rebase --interactive" hands them over. Nothing is committed by asking for it.
    /// </summary>
    public static string SquashMessage(SgRoot root, string worktree, IReadOnlyList<string> shas) =>
        string.Join("\n\n", Check(root, worktree, shas).Messages.Where(m => m.Length > 0));

    /// <summary>Joins an unbroken run of the branch's commits into one. The commits above it are replayed on top.</summary>
    public static RewriteResult Squash(SgRoot root, string worktree, IReadOnlyList<string> shas, string message)
    {
        if (shas.Count < 2) throw new SgException("squashing joins two commits or more");
        return Replace(root, Check(root, worktree, shas), message, shas.Count);
    }

    /// <summary>Gives one commit a new message and leaves everything it changed exactly as it was.</summary>
    public static RewriteResult Reword(SgRoot root, string worktree, string sha, string message) =>
        Replace(root, Check(root, worktree, new[] { sha }), message, 1);

    /// <summary>
    /// The message a revert starts from: what it undoes, named. git writes "Revert" and the subject; a
    /// run of them says so once and lists them, which is what a person reads before pressing the button.
    /// </summary>
    public static string RevertMessage(SgRoot root, string worktree, IReadOnlyList<string> shas)
    {
        var git = root.Git;
        var picked = Reachable(root, worktree, shas);
        if (picked.Count == 1) return $"Revert \"{git.Subject(picked[0])}\"\n\nThis undoes commit {Short(picked[0])}.\n";
        var lines = picked.Select(s => $"  {Short(s)}  {git.Subject(s)}");
        return $"Revert {picked.Count} commits\n\nThis undoes:\n{string.Join("\n", lines)}\n";
    }

    /// <summary>
    /// Undoes what the given commits changed, as one new commit on top. Nothing is rewritten: the
    /// commits stay in the history and a new one takes their changes back out, which is the only kind
    /// of undo that is safe once something has been shared. Reverting is refused where the changes no
    /// longer apply cleanly, and nothing is left half done when that happens.
    /// </summary>
    public static RewriteResult Revert(SgRoot root, string worktree, IReadOnlyList<string> shas, string message)
    {
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var branch = git.CurrentBranch(worktree);
        if (message.Trim().Length == 0) throw new SgException("a commit needs a message");
        if (git.RebaseInProgress(worktree))
            throw new SgException(Conflicts.Note(git, worktree) + ". " + Conflicts.Where);
        if (!git.IsClean(worktree))
            throw new SgException("the worktree has uncommitted changes: " + worktree
                + "\nA revert writes over the same files. Commit them first, or discard them.");

        // Newest first: undoing a run works backwards, the way it was built up.
        var picked = Reachable(root, worktree, shas);
        var r = git.RevertNoCommit(worktree, picked);
        if (!r.Ok)
        {
            git.RevertAbort(worktree);
            throw new SgException("these changes do not come back out cleanly, so nothing was changed:\n"
                + (r.StdErr.Trim() + "\n" + r.StdOut.Trim()).Trim()
                + "\nUndo them by hand in the worktree instead, and commit that.");
        }
        if (git.IsClean(worktree))
        {
            git.RevertAbort(worktree);
            throw new SgException("there is nothing to take back out: what those commits changed is not in the branch any more.");
        }
        var sha = git.CommitAsUser(worktree, message.TrimEnd() + "\n");
        return new RewriteResult(branch, sha, picked.Count);
    }

    /// <summary>
    /// The picked commits, newest first, checked to be the branch's own. A snapshot is what SVN said and
    /// undoing one here would put the whole of a sync back out again; the SVN side reverses a revision.
    /// </summary>
    static List<string> Reachable(SgRoot root, string worktree, IReadOnlyList<string> shas)
    {
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var branch = git.CurrentBranch(worktree);
        var snapRef = root.SnapshotRef(BaseCheckout(root, branch));
        if (shas.Count == 0) throw new SgException("no commits were picked");

        var own = git.RevList(worktree, snapRef + "..HEAD");
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < own.Count; i++) at[own[i]] = i;
        var picked = new List<(int At, string Sha)>();
        foreach (var sha in shas)
        {
            if (!at.TryGetValue(sha, out var i))
                throw new SgException($"{Short(sha)} is not one of this branch's own commits. A snapshot of SVN is undone on the SVN side, by reversing its revision.");
            picked.Add((i, sha));
        }
        return picked.OrderBy(p => p.At).Select(p => p.Sha).ToList();
    }

    /// <summary>
    /// Puts one commit where the run was: the run's own last tree, its first commit's parent, and the
    /// given message. The new commit changes the same bytes the run did, so replaying what sat above it
    /// cannot conflict; a rebase that fails anyway is undone rather than left half done.
    /// </summary>
    static RewriteResult Replace(SgRoot root, Rewritable r, string message, int replaced)
    {
        var git = root.Git;
        if (message.Trim().Length == 0) throw new SgException("a commit needs a message");

        var sha = git.CommitTreeAs(git.TreeOf(r.Newest), git.ParentOf(r.Oldest), message.TrimEnd() + "\n", git.AuthorOf(r.Oldest));

        if (r.AtTip)
        {
            // Nothing sits above the run, so the branch simply moves. The new commit has the tree the
            // branch already has on disk, so no file is touched by this.
            git.ResetHard(r.Worktree, sha);
        }
        else
        {
            var result = git.RebaseOnto(r.Worktree, sha, r.Newest, r.Branch);
            if (!result.Ok)
            {
                git.RebaseAbort(r.Worktree);
                throw new SgException("the commits above could not be replayed, so nothing was changed:\n"
                    + (result.StdErr.Trim() + "\n" + result.StdOut.Trim()).Trim());
            }
        }
        return new RewriteResult(r.Branch, git.HeadSha(r.Worktree), replaced);
    }

    // ---- rebase ----

    public static RebaseResult Rebase(SgRoot root, string worktree, bool abortOnConflict = false)
    {
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var branch = git.CurrentBranch(worktree);
        var co = BaseCheckout(root, branch);
        var snapRef = root.SnapshotRef(co);
        if (git.RebaseInProgress(worktree))
            throw new SgException(Conflicts.Note(git, worktree) + ". " + Conflicts.Where);
        if (!git.IsClean(worktree))
            throw new SgException("worktree has uncommitted changes: " + worktree
                + "\nA rebase moves the branch out from under them. Commit them first, or discard them.");

        var r = git.Rebase(worktree, snapRef);
        var res = new RebaseResult { Branch = branch, Checkout = co.Name, Output = (r.StdOut + r.StdErr).Trim() };
        if (r.Ok)
        {
            res.Ok = true;
            res.Ahead = git.CountCommits(snapRef, "refs/heads/" + branch);
            res.Refreshed = RefreshShared(root, co, worktree, branch);
            return res;
        }
        if (git.RebaseInProgress(worktree))
        {
            res.Conflict = true;
            if (abortOnConflict) git.RebaseAbort(worktree);
            return res;
        }
        r.EnsureOk();
        return res;
    }

    // ---- remove ----

    public static void Remove(SgRoot root, string branch, bool force)
    {
        using var _ = root.Lock();
        var git = root.Git;
        var wt = git.WorktreeList().FirstOrDefault(w => w.Branch == branch);
        if (wt == null && git.RefSha("refs/heads/" + branch) == null) throw new SgException("no such branch: " + branch);
        if (wt != null)
        {
            if (Directory.Exists(wt.Path))
            {
                if (!force && !git.IsClean(wt.Path))
                    throw new SgException("worktree has uncommitted changes. Use --force to drop them: " + wt.Path);
                RemoveReparsePoints(wt.Path, 3);
                // Windows holds a folder open for anything running in it, and this step is the one that
                // fails. It used to fail as git's own sentence, which named a file and not the situation,
                // and it stopped before the branch went - so the next "sg branch <same name>" said only
                // "branch exists" and the two halves of one story never met.
                try { git.WorktreeRemove(wt.Path, force: true); }
                catch (SgException e)
                {
                    throw new SgException(
                        $"the worktree folder is still in use, so it was not removed: {wt.Path}. "
                        + "Close what is holding it - a terminal in that folder, an editor, a running build - and try again. "
                        + $"Nothing is lost: branch \"{branch}\" and its commits are still here. (git said: {e.Message})");
                }
            }
            else git.WorktreePrune();
        }
        if (git.RefSha("refs/heads/" + branch) != null) git.BranchDelete(branch);
        git.ConfigUnset($"branch.{branch}.sgBase");
        git.ConfigUnset($"branch.{branch}.sgShared");
    }

    /// <summary>Junctions must go before git deletes the folder, so nothing follows them into the checkout.</summary>
    static void RemoveReparsePoints(string dir, int depth)
    {
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            if (PathUtil.IsReparsePoint(d)) { Directory.Delete(d); continue; }
            if (depth > 1 && Path.GetFileName(d) != ".git") RemoveReparsePoints(d, depth - 1);
        }
    }

    // ---- status ----

    public static StatusResult Status(SgRoot root, bool checkSvn)
    {
        var git = root.Git;
        var res = new StatusResult { Root = root.RootPath };
        // Two git calls answer every ref and every branch base. Asking one ref at a time cost a process
        // each, and the overview asks again on every refresh and on every monitor tick.
        var refs = git.RefIndex("refs/heads/", SgRoot.SnapshotRefPrefix, Shelf.RefPrefix.TrimEnd('/'), Backup.PushedPrefix + "heads");
        var bases = git.BranchBases();
        var shared = git.BranchConfig("sgShared");
        var backedUp = git.BranchConfig("sgBackedUp");
        res.BackupUrl = root.Config.Backup?.Url;
        var shelves = Shelf.From(refs);

        foreach (var co in root.Config.Checkouts)
        {
            var cs = new CheckoutStatus { Name = co.Name, Path = co.Path, Url = co.Url };
            cs.Shelves = shelves.Count(s => s.IsCheckout && s.Checkout.Equals(co.Name, StringComparison.OrdinalIgnoreCase));
            if (refs.TryGetValue(root.SnapshotRef(co), out var snap))
            {
                cs.Snapshot = snap.Sha;
                cs.SnapshotTaken = snap.Committed;
                var meta = SnapshotMeta.Parse(snap.Message);
                cs.Revision = meta.Revision;
                cs.Externals = meta.Externals.Select(kv => new ExternalInfo(kv.Key, kv.Value, "")).ToList();
            }
            if (checkSvn)
            {
                try
                {
                    cs.LocalEdits = root.Svn.Status(co.Path, noIgnore: false)
                        .Count(e => e.Path.Length > 0 && e.Item is not ("external" or "unversioned" or "ignored"));
                }
                catch (SgException ex) { root.Log.Warn(ex.Message); }
            }
            res.Checkouts.Add(cs);
        }

        var coPaths = root.Config.Checkouts.Select(c => c.Path.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<(WorktreeInfo Wt, string Branch, Replay Stopped, bool Missing)>();
        foreach (var w in git.WorktreeList())
        {
            if (w.Bare || coPaths.Contains(w.Path.TrimEnd('\\', '/'))) continue;
            var missing = !Directory.Exists(w.Path);
            var branch = w.Branch;
            // A rebase detaches HEAD, so the branch has to be read out of the folder git left behind.
            // An import does not - it commits onto the branch, and the branch stays checked out - so
            // what stopped here is a separate question from what the branch is called.
            var stopped = missing ? Replay.None : git.ReplayInProgress(w.Path);
            if (branch == null && !missing) branch = git.RebaseHeadName(w.Path);
            if (branch == null) continue;
            found.Add((w, branch, stopped, missing));
        }

        // git status walks the whole worktree, and a worktree here holds tens of thousands of files.
        // One worktree at a time made the overview wait for the sum of them.
        res.Worktrees.AddRange(Fan.Map(found, w => WorktreeStatusOf(git, refs, bases, shared, backedUp, w)));
        foreach (var ws in res.Worktrees)
            ws.Shelves = shelves.Count(s => !s.IsCheckout && s.Branch.Equals(ws.Branch, StringComparison.OrdinalIgnoreCase));
        return res;
    }

    static WorktreeStatus WorktreeStatusOf(Git git, Dictionary<string, RefInfo> refs, Dictionary<string, string> bases,
        Dictionary<string, string> shared, Dictionary<string, string> backedUp, (WorktreeInfo Wt, string Branch, Replay Stopped, bool Missing) w)
    {
        var (info, branch, stopped, missing) = w;
        var ws = new WorktreeStatus { Branch = branch, Path = info.Path, Missing = missing, Stopped = stopped };
        var rebasing = ws.RebaseInProgress;
        if (shared.TryGetValue(branch, out var how)) ws.Shared = how;
        var branchRef = "refs/heads/" + branch;
        var hasBranch = refs.TryGetValue(branchRef, out var head);
        if (bases.TryGetValue(branch, out var baseName))
        {
            ws.Base = baseName;
            var snapRef = SgRoot.SnapshotRefPrefix + baseName;
            if (refs.ContainsKey(snapRef) && hasBranch)
            {
                // One rev-list answers both directions.
                var (behind, ahead) = git.CountBoth(snapRef, branchRef);
                ws.Ahead = ahead;
                ws.Behind = behind;
                ws.NeedsRebase = ws.Behind > 0 && !rebasing;
                ws.BackedUp = Backup.BackedUpAt(backedUp.GetValueOrDefault(branch));
                ws.NotBackedUp = Backup.NotBackedUp(git, branch, head!.Sha, ws.Ahead, refs.GetValueOrDefault(Backup.PushedRef("branch", branch)));
            }
        }
        if (!missing)
        {
            var dirty = rebasing ? 0 : git.DirtyCount(info.Path);
            ws.Dirty = dirty > 0;
            ws.DirtyFiles = dirty;
            if (rebasing) ws.Conflicts = git.ConflictedFiles(info.Path).Count;
        }
        if (hasBranch) ws.Pending = head!.Subject.StartsWith("not pushed yet", StringComparison.OrdinalIgnoreCase);
        return ws;
    }

    // ---- edits made directly in the checkout ----

    public sealed class SvnChange
    {
        public string Path = "";
        public string Item = "";
        public string Props = "";
        /// <summary>Working copy the change belongs to. "" is the root.</summary>
        public string Wc = "";
        public bool Versioned => Item is not ("unversioned" or "ignored");

        public string Code => Item switch
        {
            "modified" => "M",
            "added" => "A",
            "deleted" => "D",
            "unversioned" => "?",
            "missing" => "!",
            "conflicted" => "C",
            "replaced" => "R",
            "obstructed" => "~",
            "normal" => Props != "none" ? "P" : " ",
            _ => Item.Length > 0 ? Item[..1].ToUpperInvariant() : " ",
        };
    }

    /// <summary>Every local change in the checkout and its externals, with the working copy each one belongs to.</summary>
    public static List<SvnChange> CheckoutChanges(SgRoot root, CheckoutConfig co)
    {
        var status = root.Svn.Status(co.Path, noIgnore: false);
        // Longest first, so the first working copy a path sits under is the innermost one. Sorting once
        // instead of once per changed path is what a checkout with thousands of edits needs.
        var wcs = status.Where(e => e.Item == "external" && e.Path.Length > 0).Select(e => e.Path)
            .OrderByDescending(w => w.Length).ToList();
        wcs.Add("");
        string WcOf(string p)
        {
            foreach (var w in wcs)
                if (PathUtil.IsUnder(p, w)) return w;
            return "";
        }
        return status
            .Where(e => e.Path.Length > 0 && e.Item is not ("external" or "ignored") && !(e.Item == "normal" && e.Props == "none"))
            .Where(e => e.Path != ".git")
            .Select(e => new SvnChange { Path = e.Path, Item = e.Item, Props = e.Props, Wc = WcOf(e.Path) })
            .OrderBy(c => c.Wc, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Only the number. The overview asks for it on every refresh, so it skips building and sorting the rows.</summary>
    public static int LocalEditCount(SgRoot root, CheckoutConfig co) =>
        root.Svn.Status(co.Path, noIgnore: false)
            .Count(e => e.Path.Length > 0 && e.Item is not ("external" or "ignored")
                        && !(e.Item == "normal" && e.Props == "none") && e.Path != ".git");

    public sealed class SvnCommitGroup
    {
        public string Wc = "";
        public List<string> Paths = new();
        public string State = "pending";
        public long? Revision;
        public string? Error;
    }

    public sealed class SvnCommitResult
    {
        public List<SvnCommitGroup> Groups = new();
        public bool AllCommitted => Groups.All(g => g.State == "committed");
        public SyncResult? Sync;
    }

    /// <summary>
    /// Commits chosen local changes straight to SVN, one commit per working copy. Unversioned files get added first,
    /// missing files get deleted. A choice of a folder takes everything under it. Then a sync, so the snapshot has the new revisions.
    /// </summary>
    public static SvnCommitResult SvnCommit(SgRoot root, CheckoutConfig co, IEnumerable<string> chosen, string message)
    {
        var svn = root.Svn;
        var log = root.Log;
        message = Push.CleanMessage(message);
        if (message.Length < root.Config.MinMessageLength)
            throw new SgException($"message too short: {message.Length} chars, the minimum is {root.Config.MinMessageLength}");
        using var _ = root.Lock();

        var all = CheckoutChanges(root, co);
        var picked = chosen.Select(PathUtil.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = all.Where(c => picked.Any(p => PathUtil.IsUnder(c.Path, p))).ToList();
        if (selected.Count == 0) throw new SgException("nothing chosen");
        if (selected.Any(c => c.Item is "conflicted" or "obstructed"))
            throw new SgException("some chosen files are in conflict or obstructed. Solve that in the checkout first.");

        var result = new SvnCommitResult();
        foreach (var group in selected.GroupBy(c => c.Wc, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key.Length == 0 ? 0 : 1).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var g = new SvnCommitGroup { Wc = group.Key, Paths = group.Select(c => c.Path).ToList() };
            result.Groups.Add(g);
            var label = g.Wc.Length == 0 ? "root" : g.Wc;
            log.Info($"committing {g.Paths.Count} change(s) in {label}");
            try
            {
                var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unversioned = group.Where(c => c.Item == "unversioned").Select(c => c.Path).ToList();
                var missing = group.Where(c => c.Item == "missing").Select(c => c.Path).ToList();
                if (unversioned.Count > 0) svn.Add(co.Path, unversioned);
                if (missing.Count > 0) svn.Rm(co.Path, missing);
                foreach (var c in group) targets.Add(c.Path);
                // svn add on a folder adds everything under it, and --parents may add folders above. All of that must be in the commit.
                foreach (var p in unversioned)
                    foreach (var e in svn.StatusOf(co.Path, p).Where(e => e.Item == "added"))
                        targets.Add(e.Path);
                var ancestors = targets.SelectMany(PathUtil.Ancestors)
                    .Where(a => PathUtil.IsUnder(a, g.Wc) && !a.Equals(g.Wc, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (ancestors.Count > 0)
                    foreach (var e in svn.StatusTargets(co.Path, ancestors).Where(e => e.Item == "added"))
                        targets.Add(e.Path);
                g.Revision = svn.Commit(co.Path, targets.OrderBy(t => t.Length).ToList(), message);
                g.State = "committed";
                log.Info($"  {label}: r{g.Revision}");
            }
            catch (SgException ex)
            {
                g.State = "failed";
                g.Error = ex.Message;
                log.Warn($"  {label} failed: " + ex.Message);
            }
        }

        try { result.Sync = Sync(root, co); }
        catch (SgException ex) { log.Warn("sync after the commit failed: " + ex.Message); }
        return result;
    }

    /// <summary>Throws away chosen local changes. Versioned ones are reverted, unversioned ones are deleted when asked.</summary>
    public static void SvnRevert(SgRoot root, CheckoutConfig co, IEnumerable<string> chosen, bool deleteUnversioned)
    {
        var svn = root.Svn;
        using var _ = root.Lock();
        var all = CheckoutChanges(root, co);
        var picked = chosen.Select(PathUtil.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = all.Where(c => picked.Any(p => PathUtil.IsUnder(c.Path, p))).ToList();
        var versioned = selected.Where(c => c.Versioned).Select(c => c.Path).ToList();
        if (versioned.Count > 0)
        {
            var r = svn.Revert(co.Path, versioned);
            if (!r.Ok) root.Log.Warn("svn revert said: " + r.StdErr.Trim());
        }
        if (!deleteUnversioned) return;
        foreach (var p in selected.Where(c => !c.Versioned).Select(c => c.Path))
        {
            var abs = PathUtil.Join(co.Path, p);
            if (File.Exists(abs)) File.Delete(abs);
            else if (Directory.Exists(abs)) Directory.Delete(abs, true);
        }
    }

    // ---- conflicts ----
    //
    // Reading what stopped and moving it on live in Conflicts, because a rebase is not the only thing
    // that stops: an import replays a patch series and stops the same way. Both are finished there.

    /// <summary>
    /// A clone or copy worktree holds its shared folders as of the branch, and a rebase is the moment
    /// the code moves, so the folders move with it: each is mirrored from the checkout. A junction
    /// worktree needs nothing; it looks into the checkout already. One folder that cannot be
    /// refreshed is a warning, not a failed rebase: the branch is already on the new snapshot.
    /// </summary>
    internal static List<string> RefreshShared(SgRoot root, CheckoutConfig co, string worktree, string branch)
    {
        var how = root.Git.ConfigGet($"branch.{branch}.sgShared");
        if (how is not ("clone" or "copy")) return new List<string>();
        var done = new List<string>();
        foreach (var rel in co.Junctions)
        {
            var source = PathUtil.Join(co.Path, rel);
            var target = PathUtil.Join(worktree, rel);
            if (!Directory.Exists(source)) { root.Log.Warn("not in the checkout, left alone: " + rel); continue; }
            if (PathUtil.IsReparsePoint(target)) { root.Log.Warn("a junction, left alone: " + rel); continue; }
            try
            {
                var m = SharedFolders.Mirror(root.Log, rel, source, target, clone: how == "clone");
                root.Log.Info($"{rel}: {m}");
                done.Add(rel);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                root.Log.Warn($"could not refresh {rel}: {e.Message}");
            }
        }
        return done;
    }

    // ---- remote check ----

    /// <summary>Asks the server whether the root or any external moved past the snapshot. One svn info call, then one log call per part that did.</summary>
    public static RemoteCheckResult RemoteCheck(SgRoot root, CheckoutConfig co)
    {
        var git = root.Git;
        var svn = root.Svn;
        var sha = git.RefSha(root.SnapshotRef(co)) ?? throw new SgException("no snapshot for " + co.Name);
        var meta = SnapshotMeta.Parse(git.Body(sha));
        var entries = new List<RemoteEntry> { new() { Rel = "", Url = meta.Url, Snapshot = meta.Revision } };
        foreach (var (rel, rev) in meta.Externals)
            if (meta.ExternalUrls.TryGetValue(rel, out var url)) entries.Add(new RemoteEntry { Rel = rel, Url = url, Snapshot = rev });
        entries.RemoveAll(e => e.Url.Length == 0);

        var infos = svn.InfoMany(co.Path, entries.Select(e => e.Url), recursive: false);
        foreach (var e in entries)
        {
            var info = infos.FirstOrDefault(i => i.Url.TrimEnd('/').Equals(e.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            if (info == null) continue;
            e.Server = info.LastChangedRev;
            if (e.Behind) e.Commits = Math.Max(1, svn.LogCount(co.Path, e.Url, e.Snapshot + 1, 50));
        }
        return new RemoteCheckResult { Checkout = co.Name, Entries = entries };
    }

    // ---- helpers ----

    public static CheckoutConfig BaseCheckout(SgRoot root, string branch)
    {
        var name = root.Git.ConfigGet($"branch.{branch}.sgBase")
                   ?? throw new SgException($"branch {branch} has no sg base. Make branches with 'sg branch'.");
        return root.Checkout(name);
    }

    /// <summary>Which checkout a command means: the named one, the one around cwd, the base of the worktree around cwd, or the only one.</summary>
    public static CheckoutConfig ResolveCheckout(SgRoot root, string? name, string cwd)
    {
        if (name != null) return root.Checkout(name);
        var co = root.CheckoutContaining(cwd);
        if (co != null) return co;
        var r = root.Git.Run(cwd, "symbolic-ref", "--short", "-q", "HEAD");
        if (r.Ok)
        {
            var b = root.Git.ConfigGet($"branch.{r.StdOut.Trim()}.sgBase");
            if (b != null) return root.Checkout(b);
        }
        if (root.Config.Checkouts.Count == 1) return root.Config.Checkouts[0];
        throw new SgException("which checkout? Give a name: " + string.Join(", ", root.Config.Checkouts.Select(c => c.Name)));
    }
}
