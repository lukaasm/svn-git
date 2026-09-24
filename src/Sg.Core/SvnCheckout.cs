using System.Text;
using System.Text.RegularExpressions;

namespace Sg.Core;

/// <summary>
/// An SVN working copy behind a checkout. It speaks svn and svnmucc, and its snapshot hashes the
/// working copy with the pristine copy of every local edit put back (<see cref="Snapshot"/>). The
/// checkout is a linked worktree of the store: a .git file in it points into .sg, so store commands
/// run in the folder see the snapshot as HEAD.
/// </summary>
public sealed class SvnCheckoutVcs : ICheckoutVcs
{
    public CheckoutKind Kind => CheckoutKind.Svn;
    public string ServerName => "SVN";

    // ---- registering ----

    public CheckoutIdentity Describe(SgRoot root, string folder)
    {
        var gitFile = Path.Combine(folder, ".git");
        if (File.Exists(gitFile) || Directory.Exists(gitFile)) throw new SgException(folder + " already has a .git. Remove it first.");
        var info = root.Svn.Info(folder, ".");
        return new CheckoutIdentity { Kind = CheckoutKind.Svn, Url = info.Url, ReposRoot = info.ReposRoot };
    }

    public void CheckoutUrl(SgRoot root, string url, string folder)
    {
        root.Log.Info($"svn checkout {url} into {folder}");
        var r = root.Svn.Checkout(url, folder);
        if (r.Conflicts > 0) root.Log.Warn($"{r.Conflicts} conflict(s) after the checkout, check svn status in {folder}");
    }

    public bool UrlExists(SgRoot root, string url) => root.Svn.UrlExists(url);

    public string NameFromUrl(string url) => url.TrimEnd('/').Split('/').Last();

    /// <summary>git worktree add refuses a folder that has files. Add it elsewhere, then move the .git file in.</summary>
    public void Attach(SgRoot root, CheckoutConfig co)
    {
        var gitFile = Path.Combine(co.Path, ".git");
        root.Git.WorktreePrune();
        var tmpParent = root.NewTempDir();
        var tmp = Path.Combine(tmpParent, co.Name);
        try
        {
            root.Git.WorktreeAddDetachedNoCheckout(tmp, SgRoot.RootRef);
            File.Move(Path.Combine(tmp, ".git"), gitFile);
        }
        finally
        {
            try { Directory.Delete(tmpParent, true); } catch { /* best effort */ }
        }
        root.Git.WorktreeRepair(co.Path);
    }

    public void Detach(SgRoot root, CheckoutConfig co)
    {
        try { File.Delete(Path.Combine(co.Path, ".git")); } catch (IOException) { /* leave it, the message says what happened */ }
        try { root.Git.WorktreePrune(); } catch (SgException) { /* best effort */ }
    }

    /// <summary>The linked worktree is found by its folder, not by the checkout's name, so nothing moves.</summary>
    public void Renamed(SgRoot root, CheckoutConfig co, string oldName) { }

    // ---- sync ----

    /// <summary>
    /// svn update processes externals, and that step points a switched one back at whatever
    /// svn:externals declares, deleting what only existed on the branch it was switched to. It says
    /// nothing about having done it. So the switched ones are read first, and the update that keeps
    /// them is taken; <see cref="Reswitch"/> is the safety net under both paths, and it runs before
    /// the snapshot, so the snapshot records the switched content, which is the point of switching one.
    /// </summary>
    public UpstreamUpdate Update(SgRoot root, CheckoutConfig co)
    {
        var switched = SwitchedExternals(root, co);
        var upd = switched.Count == 0 ? root.Svn.Update(co.Path) : UpdateKeepingSwitches(root, co);
        root.Log.Info($"svn update {co.Name}: " + (upd.Revision.HasValue ? "r" + upd.Revision : "done")
                      + (upd.Conflicts > 0 ? $", {upd.Conflicts} conflict(s) in the checkout" : ""));
        Reswitch(root, co, switched);
        return new UpstreamUpdate
        {
            Revision = upd.Revision,
            Conflicts = upd.Conflicts,
            Output = upd.Output,
            KeptSwitched = switched.Select(s => s.Rel).ToList(),
        };
    }

    public SnapshotInfo BuildSnapshot(SgRoot root, CheckoutConfig co, string? parentSha, Func<SnapshotInfo, string>? extraMessage) =>
        Snapshot.Build(root, co, parentSha, extraMessage);

    /// <summary>
    /// Every svn:ignore and svn:global-ignores property in the checkout and its externals. Slow on a
    /// big checkout, it walks the svn database.
    /// </summary>
    public List<string> IgnoreLines(SgRoot root, CheckoutConfig co)
    {
        var lines = new List<string>();
        var wcs = new List<string> { "" };
        try
        {
            wcs.AddRange(root.Svn.Status(co.Path, noIgnore: false).Where(e => e.Item == "external").Select(e => e.Path));
        }
        catch (SgException ex) { root.Log.Warn("svn status failed for " + co.Name + ": " + ex.Message); }
        // Two svn processes per working copy, each walking that copy's whole database. They run side
        // by side, because a checkout with twenty externals waited for forty of them in a row. The
        // lines still go in working copy order, so the generated file does not move between runs.
        var read = Fan.Map(wcs, wc =>
        {
            var cwd = PathUtil.Join(co.Path, wc);
            return (Ignore: root.Svn.PropGetRecursive(cwd, "svn:ignore"), Global: root.Svn.PropGetRecursive(cwd, "svn:global-ignores"));
        });
        for (var i = 0; i < wcs.Count; i++)
        {
            var wc = wcs[i];
            foreach (var (dir, val) in read[i].Ignore)
                foreach (var pat in SgRoot.IgnorePatterns(val)) lines.Add(SgRoot.IgnoreAnchor(wc, dir) + SgRoot.IgnoreEscape(pat));
            foreach (var (dir, val) in read[i].Global)
                foreach (var pat in SgRoot.IgnorePatterns(val)) lines.Add(SgRoot.IgnoreAnchor(wc, dir) + "**/" + SgRoot.IgnoreEscape(pat));
        }
        return lines;
    }

    // ---- local changes ----

    public CheckoutScan Scan(SgRoot root, CheckoutConfig co)
    {
        var status = root.Svn.Status(co.Path, noIgnore: false);
        return new CheckoutScan(LocalEdits(status),
            status.Where(x => x.Item == "external" && x.Path.Length > 0).Select(x => x.Path).ToList());
    }

    public HashSet<string> LocalEditsOn(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths) => LocalEditsOn(root.Svn, co, paths);

    public bool HasConflicts(SgRoot root, CheckoutConfig co) =>
        root.Svn.Status(co.Path, noIgnore: false).Any(x => x.Item is "conflicted" or "obstructed");

    /// <summary>An SVN working copy can always take a commit: svn commits what it is told, next to any local edit.</summary>
    public List<string> WriteBlockers(SgRoot root, CheckoutConfig co) => new();

    public void Add(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths) => root.Svn.Add(co.Path, paths);

    public void Remove(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths) => root.Svn.Rm(co.Path, paths);

    public string? Revert(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths)
    {
        var r = root.Svn.Revert(co.Path, paths);
        return r.Ok ? null : r.StdErr.Trim();
    }

    public string DiffLocal(SgRoot root, CheckoutConfig co, string path) => root.Svn.DiffLocal(co.Path, path);

    public string BaseText(SgRoot root, CheckoutConfig co, string path) => root.Svn.CatBase(co.Path, path);

    public void Ignore(SgRoot root, CheckoutConfig co, string folder, IEnumerable<string> names) =>
        root.Svn.AddToIgnore(co.Path, folder, names);

    /// <summary>
    /// One svn commit of one working copy's share. Unversioned files get added first and missing ones
    /// deleted; svn add on a folder adds everything under it and --parents may add folders above, and
    /// all of that must be in the commit.
    /// </summary>
    public CommitId CommitChanges(SgRoot root, CheckoutConfig co, string wc, IReadOnlyList<CheckoutChange> changes, string message,
        IReadOnlyDictionary<string, string> pins)
    {
        var svn = root.Svn;
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unversioned = changes.Where(c => c.Item == "unversioned").Select(c => c.Path).ToList();
        var missing = changes.Where(c => c.Item == "missing").Select(c => c.Path).ToList();
        if (unversioned.Count > 0) svn.Add(co.Path, unversioned);
        if (missing.Count > 0) svn.Rm(co.Path, missing);
        foreach (var c in changes) targets.Add(c.Path);
        foreach (var p in unversioned)
            foreach (var e in svn.StatusOf(co.Path, p).Where(e => e.Item == "added"))
                targets.Add(e.Path);
        var ancestors = targets.SelectMany(PathUtil.Ancestors)
            .Where(a => PathUtil.IsUnder(a, wc) && !a.Equals(wc, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ancestors.Count > 0)
            foreach (var e in svn.StatusTargets(co.Path, ancestors).Where(e => e.Item == "added"))
                targets.Add(e.Path);
        return new CommitId(svn.Commit(co.Path, targets.OrderBy(t => t.Length).ToList(), message), "");
    }

    // ---- push ----

    public void FillRepositories(SgRoot root, CheckoutConfig co, List<PushGroup> groups) => FillReposRoots(root.Svn, co, groups);

    /// <summary>An SVN external is committed on its own: nothing pins it, so the order is the one given.</summary>
    public List<(string Wc, string? PinnedIn)> CommitOrder(SgRoot root, CheckoutConfig co, IReadOnlyList<string> wcs) =>
        wcs.Select(w => (w, (string?)null)).ToList();

    public CommitId CommitWritten(SgRoot root, CheckoutConfig co, PushGroup g, string message) =>
        new(root.Svn.Commit(co.Path, g.Targets, message), "");

    // ---- history ----

    /// <summary>The root and every external, each with the revision its working copy is at and the one the snapshot holds.</summary>
    public List<HistorySource> HistorySources(SgRoot root, CheckoutConfig co)
    {
        var svn = root.Svn;
        var status = svn.Status(co.Path, noIgnore: false);
        var rels = new List<string> { "" };
        rels.AddRange(status.Where(e => e.Item == "external" && e.Path.Length > 0).Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal));
        var infos = svn.InfoMany(co.Path, rels.Select(r => r.Length == 0 ? "." : r), recursive: false);
        var snapSha = root.Git.RefSha(root.SnapshotRef(co));
        var meta = snapSha != null ? SnapshotMeta.Parse(root.Git.Body(snapSha)) : null;
        var list = new List<HistorySource>();
        foreach (var rel in rels)
        {
            var info = infos.FirstOrDefault(i => i.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (info == null) continue;
            long? snapRev = meta == null ? null : rel.Length == 0 ? meta.Revision : meta.Externals.TryGetValue(rel, out var er) ? er : null;
            var url = info.Url.TrimEnd('/');
            var reposRoot = info.ReposRoot.TrimEnd('/');
            list.Add(new HistorySource(rel, url, reposRoot, info.Revision, "", snapRev)
            {
                Prefix = url.StartsWith(reposRoot, StringComparison.OrdinalIgnoreCase) ? url[reposRoot.Length..] : "",
            });
        }
        return list;
    }

    /// <summary>The URL, not the working copy path: a working copy path only shows history up to its own revision.</summary>
    public List<LogRevision> Log(SgRoot root, CheckoutConfig co, HistorySource source, int limit) =>
        root.Svn.LogVerbose(co.Path, source.Url, limit);

    public string RevisionDiff(SgRoot root, CheckoutConfig co, HistorySource source, LogRevision rev, string? folder) =>
        root.Svn.DiffRevision(folder == null ? source.Url : source.ReposRoot + "/" + folder.TrimStart('/'), rev.Revision);

    public string FileAt(SgRoot root, CheckoutConfig co, HistorySource source, LogRevision rev, ChangedPath path, bool before)
    {
        if (before && path.Action == "A" && path.CopyFrom == null) return "";
        if (!before && path.Action == "D") return "";
        return root.Svn.CatUrl(source.ReposRoot + path.Path, before ? rev.Revision - 1 : rev.Revision);
    }

    /// <summary>svn blames the pristine copy, which is what the snapshot holds, so both readings are the same call.</summary>
    public List<ServerBlameLine> Blame(SgRoot root, CheckoutConfig co, string path, bool asInSnapshot) => root.Svn.Blame(co.Path, path);

    public (LogRevision? Log, string Diff) BlameDetails(SgRoot root, CheckoutConfig co, string path, ServerBlameLine line)
    {
        var url = root.Svn.Info(co.Path, path).Url;
        var log = root.Svn.LogVerbose(co.Path, path, 200).FirstOrDefault(x => x.Revision == line.Revision);
        return (log, root.Svn.DiffRevision(url, line.Revision));
    }

    // ---- server branches and checkouts ----

    public List<BranchPart> Parts(SgRoot root, CheckoutConfig co) => SvnServer.Parts(root, co);

    public ServerBranchPlan PlanBranch(SgRoot root, CheckoutConfig co, string name, string? message, IReadOnlyList<BranchPart>? parts) =>
        SvnServer.PlanBranch(root, co, name, message, parts);

    public void ExecuteBranch(SgRoot root, CheckoutConfig co, ServerBranchPlan plan) => SvnServer.ExecuteBranch(root, plan);

    public CheckoutResult ServerCheckout(SgRoot root, CheckoutConfig near, string target, string? name) =>
        SvnServer.Checkout(root, near, target, name);

    // ---- merging ----

    public List<MergeTarget> MergeTargets(SgRoot root, CheckoutConfig co) => SvnMerge.Targets(root, co);

    public List<MergeSource> MergeSources(SgRoot root, CheckoutConfig co, MergeTarget target) => SvnMerge.Sources(root, target);

    public List<MergePair> MergePairs(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl) =>
        SvnMerge.Pairs(root, co, target, sourceUrl);

    public List<MergeRevision> MergeOffered(SgRoot root, CheckoutConfig co, IReadOnlyList<MergePair> pairs, int limit) =>
        SvnMerge.Offered(root, co, pairs, limit);

    public List<string> MergeProblems(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl) =>
        SvnMerge.Problems(root, co, target, sourceUrl);

    public MergeResult MergeRun(SgRoot root, CheckoutConfig co, MergePair pair, IReadOnlyList<LogRevision>? picked, bool dryRun, bool reverse) =>
        SvnMerge.Run(root, co, pair.Target, pair.SourceUrl, picked?.Select(p => p.Revision).ToList(), dryRun, reverse);

    // ---- identity ----

    public (string Uuid, string Path)? Identity(SgRoot root, CheckoutConfig co)
    {
        var info = root.Svn.Info(co.Path, co.Path);
        var repoRoot = info.ReposRoot.TrimEnd('/');
        var here = info.Url.TrimEnd('/');
        var rel = repoRoot.Length > 0 && here.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
            ? here[repoRoot.Length..].TrimStart('/')
            : "";
        return (info.Uuid, rel);
    }

    // ---- what the operations above share ----

    /// <summary>Every local change in the checkout and its externals, with the working copy each one belongs to.</summary>
    public List<CheckoutChange> Changes(SgRoot root, CheckoutConfig co)
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
            .Select(e => new CheckoutChange { Path = e.Path, Item = e.Item, Props = e.Props, Wc = WcOf(e.Path) })
            .OrderBy(c => c.Wc, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Only the number. The overview asks for it on every refresh, so it skips building and sorting the rows.</summary>
    public int LocalEditCount(SgRoot root, CheckoutConfig co) =>
        root.Svn.Status(co.Path, noIgnore: false)
            .Count(e => e.Path.Length > 0 && e.Item is not ("external" or "ignored")
                        && !(e.Item == "normal" && e.Props == "none") && e.Path != ".git");

    /// <summary>Asks the server whether the root or any external moved past the snapshot. One svn info call, then one log call per part that did.</summary>
    public RemoteCheckResult RemoteCheck(SgRoot root, CheckoutConfig co, SnapshotMeta meta, bool countCommits)
    {
        var svn = root.Svn;
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
            if (e.Behind && countCommits) e.Commits = Math.Max(1, svn.LogCount(co.Path, e.Url, e.Snapshot + 1, 50));
        }
        return new RemoteCheckResult { Checkout = co.Name, Entries = entries };
    }

    public string LogsSince(SgRoot root, CheckoutConfig co, SnapshotMeta? prev, SnapshotInfo info)
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

    /// <summary>
    /// Every external of a checkout: where it points now, and where the committed svn:externals says it
    /// should. The two differ when someone switched one locally, which is a working copy state and not
    /// a commit anyone else sees.
    /// </summary>
    public List<Ops.ExternalState> Externals(SgRoot root, CheckoutConfig co)
    {
        var svn = root.Svn;
        var rels = svn.Status(co.Path, noIgnore: false)
            .Where(e => e.Item == "external" && e.Path.Length > 0)
            .Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (rels.Count == 0) return new();

        var infos = svn.InfoMany(co.Path, rels, recursive: false);
        var declared = DeclaredExternals(root, co);
        var res = new List<Ops.ExternalState>();
        foreach (var rel in rels)
        {
            var info = infos.FirstOrDefault(i => i.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (info == null) continue;
            res.Add(new Ops.ExternalState
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
            foreach (var (url, name) in Sg.Core.Externals.Definitions(value))
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
    public List<string> BranchNames(SgRoot root, CheckoutConfig co, string url)
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
    public string UrlForBranch(SgRoot root, CheckoutConfig co, string url, string branch) =>
        BranchRule.NewUrl(url.TrimEnd('/'), branch, root.Config.BranchUrlOverrides);

    /// <summary>
    /// Points one external at another URL, here only. This is svn switch on the external's own working
    /// copy: the svn:externals property is untouched, so nothing is committed and nobody else sees it.
    /// The next sync records the new content and the new URL in the snapshot, which is what makes a
    /// branch built on this checkout build against the switched external. Sync has to put the switch
    /// back itself after its svn update, because svn update undoes one; see <see cref="Reswitch"/>.
    /// </summary>
    public UpstreamUpdate SwitchExternal(SgRoot root, CheckoutConfig co, string rel, string url)
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
        return new UpstreamUpdate { Revision = r.Revision, Conflicts = r.Conflicts, Output = r.Output };
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

    /// <summary>One svn info for every working copy at once. One call each meant a process per external.</summary>
    static void FillReposRoots(Svn svn, CheckoutConfig co, List<PushGroup> groups)
    {
        if (groups.Count == 0) return;
        try
        {
            var roots = svn.InfoMany(co.Path, groups.Select(g => g.Wc.Length == 0 ? "." : g.Wc), recursive: false);
            foreach (var g in groups)
                g.ReposRoot = roots.FirstOrDefault(i => i.Path.Equals(g.Wc, StringComparison.OrdinalIgnoreCase))?.ReposRoot ?? "";
        }
        catch (SgException) { /* shown as empty */ }
    }

    /// <summary>
    /// Puts one working copy's share of the change on disk and tells svn about it: files written, adds
    /// added, deletes deleted, renames moved. It stops there. A push commits what this leaves behind;
    /// an apply that is not committing leaves exactly this, for someone to read and commit by hand.
    /// </summary>
    public void WriteInto(SgRoot root, CheckoutConfig co, PushGroup g, string tip)
    {
        var git = root.Git;
        var svn = root.Svn;
        var cwd = co.Path;
        var renames = g.Entries.Where(e => e.Status == 'R' && e.OldPath != null).ToList();
        var adds = g.Entries.Where(e => e.Status == 'A').Select(e => e.Path).ToList();
        var mods = g.Entries.Where(e => e.Status is 'M' or 'T').Select(e => e.Path).ToList();
        var dels = g.Entries.Where(e => e.Status == 'D').Select(e => e.Path).ToList();

        g.Targets = new List<string>();
        foreach (var r in renames)
        {
            svn.Mv(cwd, r.OldPath!, r.Path);
            g.Targets.Add(r.OldPath!);
            g.Targets.Add(r.Path);
        }
        var write = adds.Concat(mods).Concat(renames.Select(r => r.Path)).ToList();
        git.CheckoutPaths(cwd, tip, write);
        foreach (var d in dels)
        {
            var abs = PathUtil.Join(cwd, d);
            if (File.Exists(abs)) File.Delete(abs);
        }
        if (adds.Count > 0) svn.Add(cwd, adds);
        if (dels.Count > 0) svn.Rm(cwd, dels);
        g.Targets.AddRange(write);
        g.Targets.AddRange(dels);

        // Folders that svn add --parents or svn mv --parents just created must be in the commit too.
        var ancestors = adds.Concat(renames.Select(r => r.Path)).SelectMany(PathUtil.Ancestors)
            .Where(a => PathUtil.IsUnder(a, g.Wc) && !a.Equals(g.Wc, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ancestors.Count > 0)
            g.AddedDirs = svn.StatusTargets(cwd, ancestors).Where(s => s.Item == "added").Select(s => s.Path).ToList();
        g.Targets = g.AddedDirs.Concat(g.Targets).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Puts a working copy back to how the failed batch found it. That is the batch's own starting
    /// commit, not the snapshot: with several batches, an earlier one may already be in SVN.
    /// </summary>
    public void Rollback(SgRoot root, CheckoutConfig co, PushGroup g, string restoreFrom, List<string> warnings)
    {
        var git = root.Git;
        var svn = root.Svn;
        var cwd = co.Path;
        var label = g.Wc.Length == 0 ? "root" : g.Wc;
        try
        {
            var renames = g.Entries.Where(e => e.Status == 'R' && e.OldPath != null).ToList();
            var restore = g.Entries.Where(e => e.Status is 'M' or 'T' or 'D').Select(e => e.Path).Concat(renames.Select(r => r.OldPath!)).ToList();
            var drop = g.Entries.Where(e => e.Status == 'A').Select(e => e.Path).Concat(renames.Select(r => r.Path)).ToList();
            var all = g.Targets.Concat(restore).Concat(drop).Concat(g.AddedDirs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count > 0)
            {
                var r = svn.Revert(cwd, all);
                if (!r.Ok) warnings.Add($"svn revert in {label} said: " + r.StdErr.Trim());
            }
            if (restore.Count > 0) git.CheckoutPaths(cwd, restoreFrom, restore);
            foreach (var d in drop)
            {
                var abs = PathUtil.Join(cwd, d);
                if (File.Exists(abs)) File.Delete(abs);
            }
            foreach (var dir in g.AddedDirs.OrderByDescending(x => x.Length))
            {
                var abs = PathUtil.Join(cwd, dir);
                if (Directory.Exists(abs) && !Directory.EnumerateFileSystemEntries(abs).Any()) Directory.Delete(abs);
            }
        }
        catch (Exception ex) when (ex is SgException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"rollback of {label} hit a problem: {ex.Message}. Check 'svn status' in {cwd}.");
        }
    }

    /// <summary>
    /// The same question as LocalEdits, asked about a handful of paths instead of the whole
    /// checkout. A full svn status walks 173k files and every external, which is far too slow to
    /// run every time the Push window opens.
    /// </summary>
    static HashSet<string> LocalEditsOn(Svn svn, CheckoutConfig co, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var here = paths.Where(p => File.Exists(PathUtil.Join(co.Path, p)) || Directory.Exists(PathUtil.Join(co.Path, p))).ToList();
        if (here.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { return LocalEdits(svn.StatusTargets(co.Path, here)); }
        catch (SgException) { return LocalEdits(svn.Status(co.Path, noIgnore: false)); }
    }

    /// <summary>Paths the checkout has changed locally. Push must never write over one.</summary>
    static HashSet<string> LocalEdits(IEnumerable<SvnStatusEntry> status) =>
        status
            .Where(s => s.Path.Length > 0 && s.Item is not ("external" or "unversioned" or "ignored") && !(s.Item == "normal" && s.Props == "none"))
            .Select(s => s.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
