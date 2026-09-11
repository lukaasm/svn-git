using System.Text.RegularExpressions;

namespace Sg.Core;

/// <summary>
/// One working copy of a checkout a merge can go into: the checkout root, or one of its externals.
/// A merge is an SVN operation and SVN works one working copy at a time, so the target is one of these
/// rather than the checkout as a whole.
/// </summary>
public sealed record MergeTarget(string Wc, string Url, string ReposRoot)
{
    public string Label => Wc.Length == 0 ? "root" : Wc;
}

/// <summary>A branch of the same repository, as a merge can take changes from.</summary>
public sealed record MergeSource(string Name, string Url);

/// <summary>
/// One working copy and the folder of the source branch it takes changes from. A merge of the checkout
/// root is several of these: the root itself, and every external paired with the source branch's
/// external at the same relative folder. The content of a monorepo lives in the externals, so a
/// merge that stopped at the root would move almost nothing.
/// </summary>
public sealed record MergePair(MergeTarget Target, string SourceUrl)
{
    public string Label => Target.Label;
}

/// <summary>
/// One revision on offer, and which working copy would take it. Merged says this working copy already
/// has it, so it is folded away rather than offered again.
/// </summary>
public sealed record MergeRevision(MergePair Pair, SvnLogRevision Entry, bool Merged)
{
    public long Revision => Entry.Revision;
}

/// <summary>What a merge did, or would do. A dry run fills the same lists without touching a file.</summary>
public sealed class MergeResult
{
    public bool DryRun;
    public string SourceUrl = "";
    public string Target = "";
    public string Output = "";

    /// <summary>Paths the merge changed, with the letter svn gave each one.</summary>
    public List<(char Action, string Path)> Changed = new();

    /// <summary>Paths left in conflict. With --accept postpone they are on disk as conflict files.</summary>
    public List<string> Conflicts = new();

    /// <summary>Which revisions it took. Empty means everything the source has that this branch has not.</summary>
    public List<long> Revisions = new();

    /// <summary>It took those revisions back out rather than bringing them in.</summary>
    public bool Reverse;

    /// <summary>What each working copy did, when the merge covered more than one.</summary>
    public List<MergeResult> Parts = new();

    public bool Clean => Conflicts.Count == 0;
}

/// <summary>
/// Merging between server branches, in the checkout, the way it has always been done here by hand.
/// Two shapes: a cherry pick of named revisions, and everything the source has that this branch has not.
/// Both leave their result as local changes in the checkout, which is where they are read and committed;
/// nothing here talks to git, and nothing here commits.
/// </summary>
public static class Merge
{
    /// <summary>The working copies of a checkout a merge can target: the root first, then each external.</summary>
    public static List<MergeTarget> Targets(SgRoot root, CheckoutConfig co)
    {
        var parts = Server.Parts(root, co);
        // One svn info for every working copy at once. It answers with the paths as they were given or
        // as absolute ones depending on the version, so both are folded back to the relative form here.
        var infos = new Dictionary<string, SvnInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in root.Svn.InfoMany(co.Path, parts.Select(p => p.Wc.Length == 0 ? "." : p.Wc), recursive: false))
            infos[PathUtil.Rel(Path.IsPathRooted(i.Path) ? PathUtil.RelativeTo(co.Path, i.Path) : i.Path)] = i;

        var res = new List<MergeTarget>();
        foreach (var p in parts)
        {
            infos.TryGetValue(p.Wc, out var info);
            res.Add(new MergeTarget(p.Wc, info?.Url.TrimEnd('/') ?? p.Url, info?.ReposRoot.TrimEnd('/') ?? ""));
        }
        return res;
    }

    /// <summary>
    /// The branches of the same repository this working copy could take changes from, itself left out.
    /// It reads the layout off the target's own URL: the sibling entries of its "branches" folder, and
    /// the "trunk" beside them where there is one.
    /// </summary>
    public static List<MergeSource> Sources(SgRoot root, MergeTarget target)
    {
        var parts = target.Url.TrimEnd('/').Split('/');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i] == "branches" && i + 1 < parts.Length)
            {
                var branchesUrl = string.Join("/", parts[..(i + 1)]);
                var current = parts[i + 1];
                var tail = parts[(i + 2)..];
                var res = Siblings(root, branchesUrl, tail, name => !name.Equals(current, StringComparison.OrdinalIgnoreCase));
                var trunk = string.Join("/", parts[..i].Concat(["trunk"]).Concat(tail));
                if (root.Svn.UrlExists(trunk)) res.Insert(0, new MergeSource("trunk", trunk));
                return res;
            }
            if (parts[i] == "trunk")
            {
                var branchesUrl = string.Join("/", parts[..i].Concat(["branches"]));
                return root.Svn.UrlExists(branchesUrl)
                    ? Siblings(root, branchesUrl, parts[(i + 1)..], _ => true)
                    : new List<MergeSource>();
            }
        }
        return new List<MergeSource>();
    }

    static List<MergeSource> Siblings(SgRoot root, string branchesUrl, string[] tail, Func<string, bool> keep) =>
        root.Svn.ListDirs(branchesUrl)
            .Where(keep)
            .Select(name => new MergeSource(name, string.Join("/", new[] { branchesUrl, name }.Concat(tail))))
            .ToList();

    /// <summary>The revisions of a source branch, newest first, with the paths each one touched.</summary>
    public static List<SvnLogRevision> Revisions(SgRoot root, string url, int limit = 100) =>
        root.Svn.LogVerbose(null, url, limit);

    /// <summary>
    /// What a branch declares as externals, as the folder they land in against the URL they come from.
    /// Read off the server, so a branch nobody has checked out answers too. The folders are relative to
    /// the branch, which is the same shape a checkout's own externals are listed in, so the two pair up.
    /// </summary>
    public static Dictionary<string, string> SourceExternals(SgRoot root, string sourceUrl)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, value) in root.Svn.PropGetRecursiveUrl(sourceUrl, "svn:externals"))
            foreach (var (url, name) in Externals.Definitions(value))
                res[PathUtil.Rel(dir.Length == 0 ? name : dir + "/" + name)] = url.TrimEnd('/');
        return res;
    }

    /// <summary>
    /// Every working copy a merge into this target covers, each with the folder of the source branch it
    /// takes from. An external is only itself. The root is itself and every external, paired with the
    /// source branch's external at the same relative folder; an external the source does not have at
    /// that folder is left out, because there is nothing on the other side to merge from.
    /// </summary>
    public static List<MergePair> Pairs(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl)
    {
        var pairs = new List<MergePair> { new(target, sourceUrl.TrimEnd('/')) };
        if (target.Wc.Length > 0) return pairs;

        var theirs = SourceExternals(root, sourceUrl);
        foreach (var t in Targets(root, co))
        {
            if (t.Wc.Length == 0) continue;
            if (!theirs.TryGetValue(t.Wc, out var their)) continue;
            // Both sides must be the same repository, the same rule the single target follows.
            if (t.ReposRoot.Length > 0 && !their.StartsWith(t.ReposRoot, StringComparison.OrdinalIgnoreCase)) continue;
            // An external the source branch points at the same place we do was never branched, so
            // there is nothing on the other side of it. Leaving it out is the answer, not an error.
            if (their.Equals(t.Url, StringComparison.OrdinalIgnoreCase)) continue;
            pairs.Add(new MergePair(t, their));
        }
        return pairs;
    }

    /// <summary>
    /// What is on offer across every working copy the merge covers, newest first. A revision this
    /// working copy already merged is in the list too, marked, so a window can fold those away rather
    /// than pretend the branch is shorter than it is.
    /// </summary>
    public static List<MergeRevision> Offered(SgRoot root, CheckoutConfig co, IReadOnlyList<MergePair> pairs, int limit = 100)
    {
        var read = Fan.Map(pairs, pair =>
        {
            var cwd = PathUtil.Join(co.Path, pair.Target.Wc);
            var log = root.Svn.LogVerbose(null, pair.SourceUrl, limit);
            // svn answers with what has not been merged yet; everything else in the log has been.
            var eligible = root.Svn.EligibleRevisions(cwd, pair.SourceUrl, ".").ToHashSet();
            // A source with no mergeinfo at all answers nothing, and then nothing is known to be merged.
            var known = eligible.Count > 0;
            return log.Select(e => new MergeRevision(pair, e, known && !eligible.Contains(e.Revision))).ToList();
        });
        return read.SelectMany(x => x).OrderByDescending(r => r.Entry.Date, StringComparer.Ordinal)
            .ThenByDescending(r => r.Revision).ToList();
    }

    /// <summary>
    /// Everything that stops a merge before it starts. The window shows these; Run checks them again,
    /// because the CLI and an agent reach Run without going past the window.
    /// </summary>
    public static List<string> Problems(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl)
    {
        var problems = new List<string>();
        var cwd = PathUtil.Join(co.Path, target.Wc);
        if (!Directory.Exists(cwd)) { problems.Add(target.Label + " is not on disk"); return problems; }

        SvnInfo? source = null;
        try { source = root.Svn.InfoUrl(sourceUrl); }
        catch (SgException ex) { problems.Add("cannot read " + sourceUrl + ": " + ex.Message.Split('\n')[0]); }

        if (source != null && target.ReposRoot.Length > 0
            && !source.ReposRoot.TrimEnd('/').Equals(target.ReposRoot, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{sourceUrl} is in another repository than {target.Label}. A merge stays inside one repository.");

        if (source != null && source.Url.TrimEnd('/').Equals(target.Url, StringComparison.OrdinalIgnoreCase))
            problems.Add("the source and the target are the same branch");

        // A merge writes over the working copy. Landing it on top of edits that are already there makes
        // the two impossible to tell apart afterwards, and reverting one of them reverts both.
        var local = root.Svn.StatusOf(cwd, ".").Where(e => e.Path.Length > 0
            && e.Item is not ("external" or "unversioned" or "ignored" or "normal")).Select(e => e.Path).Take(4).ToList();
        if (local.Count > 0)
            problems.Add($"{target.Label} has local changes ({string.Join(", ", local)}). Commit or revert them first, so what the merge brings in stands on its own.");

        return problems;
    }

    /// <summary>
    /// Runs the merge. revisions names the ones to take, oldest applied first, which is the cherry pick;
    /// null or empty takes everything the source has that this branch has not. A dry run writes nothing
    /// and answers with the same lists, which is what the window shows before it lets the real one go.
    /// </summary>
    public static MergeResult Run(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl,
        IReadOnlyList<long>? revisions, bool dryRun, bool reverse = false)
    {
        if (reverse && (revisions == null || revisions.Count == 0))
            throw new SgException("taking a revision back out needs the revisions named. Pick them in the list first.");
        var problems = Problems(root, co, target, sourceUrl);
        // A dry run is allowed to answer over a working copy that has changes: it writes nothing, and
        // seeing what would land is exactly what someone with edits in front of them wants to know.
        if (!dryRun && problems.Count > 0) throw new SgException("merge refused:\n  " + string.Join("\n  ", problems));

        var cwd = PathUtil.Join(co.Path, target.Wc);
        // Taking changes back out is the same merge run backwards, which svn writes as a negative
        // revision. Newest first there: undoing a run of revisions works the way it was built up.
        var picked = (revisions ?? Array.Empty<long>()).Distinct().ToList();
        picked = reverse ? picked.OrderByDescending(r => r).ToList() : picked.OrderBy(r => r).ToList();
        var args = new List<string> { "merge", "--non-interactive", "--accept", "postpone" };
        if (dryRun) args.Add("--dry-run");
        if (picked.Count > 0) { args.Add("-c"); args.Add(string.Join(",", picked.Select(r => reverse ? "-" + r : r.ToString()))); }
        args.Add(sourceUrl);
        args.Add(".");

        var r = root.Svn.Run(cwd, args);
        var result = new MergeResult
        {
            DryRun = dryRun,
            SourceUrl = sourceUrl,
            Target = target.Label,
            Output = (r.StdOut + "\n" + r.StdErr).Trim(),
            Revisions = picked,
            Reverse = reverse,
        };
        if (!r.Ok) throw new SgException("svn merge failed:\n" + result.Output);
        Parse(result);
        return result;
    }

    /// <summary>
    /// The whole merge, across every working copy it covers. Each pair takes the revisions that belong
    /// to it; a pair with nothing to take is left alone. With no revisions named, every pair takes
    /// everything the branch on its other side has that it has not.
    ///
    /// It stops at the first working copy that fails and says what the ones before it did: a merge is
    /// not one transaction across repositories, and pretending otherwise would hide half a result.
    /// </summary>
    public static MergeResult RunAll(SgRoot root, CheckoutConfig co, IReadOnlyList<MergePair> pairs,
        IReadOnlyList<MergeRevision>? picked, bool dryRun, bool reverse = false)
    {
        var whole = new MergeResult
        {
            DryRun = dryRun,
            Reverse = reverse,
            Target = pairs.Count == 1 ? pairs[0].Label : "the checkout",
        };
        foreach (var pair in pairs)
        {
            var mine = picked?.Where(p => p.Pair == pair).Select(p => p.Revision).ToList();
            // Named revisions that belong to another working copy are not this one's business.
            if (picked != null && (mine == null || mine.Count == 0)) continue;
            var part = Run(root, co, pair.Target, pair.SourceUrl, mine, dryRun, reverse);
            whole.Parts.Add(part);
            whole.Changed.AddRange(part.Changed);
            whole.Conflicts.AddRange(part.Conflicts);
            whole.Revisions.AddRange(part.Revisions);
            whole.Output += (whole.Output.Length > 0 ? "\n\n" : "") + $"--- {pair.Label} ---\n" + part.Output;
        }
        if (whole.Parts.Count == 0) throw new SgException("none of the picked revisions belong to a working copy this merge covers");
        whole.SourceUrl = whole.Parts[0].SourceUrl;
        return whole;
    }

    // "U    path", "A    path", "C    path", and " C   path" for a property conflict. The two columns
    // svn writes are the file and the property, and either of them can carry the C.
    static readonly Regex Line = new(@"^(?<a>[ADUCGER ])(?<p>[ADUCG ])?\s+(?<path>\S.*)$", RegexOptions.Compiled);

    static void Parse(MergeResult result)
    {
        foreach (var raw in result.Output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            // svn narrates the ranges it is merging; those lines are not paths.
            if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("Summary of conflicts", StringComparison.Ordinal)) continue;
            if (line.StartsWith(" ", StringComparison.Ordinal) && line.TrimStart().StartsWith("Text conflicts", StringComparison.Ordinal)) continue;
            var m = Line.Match(line);
            if (!m.Success) continue;
            var action = m.Groups["a"].Value[0];
            var prop = m.Groups["p"].Success && m.Groups["p"].Value.Length > 0 ? m.Groups["p"].Value[0] : ' ';
            if (action == ' ' && prop == ' ') continue;
            var path = PathUtil.Rel(m.Groups["path"].Value.Trim());
            if (path.Length == 0) continue;
            result.Changed.Add((action == ' ' ? prop : action, path));
            if (action == 'C' || prop == 'C') result.Conflicts.Add(path);
        }
    }
}
