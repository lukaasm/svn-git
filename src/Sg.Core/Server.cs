using System.Text;

namespace Sg.Core;

public sealed class RepoPlan
{
    public string ReposRoot = "";
    public List<string> Mkdirs = new();
    public List<(string Src, string Dst)> Copies = new();
    public List<(string Url, string Value)> PropSets = new();
    /// <summary>planned, committed, failed, skipped</summary>
    public string State = "planned";
    public long? Revision;
}

public sealed class ServerBranchPlan
{
    public string Name = "";
    public string Source = "";
    public string NewRootUrl = "";
    public string Message = "";
    public List<RepoPlan> Repos = new();
    /// <summary>Externals the branch leaves where they are: no copy, and the line that brings them in stays.</summary>
    public List<(string Wc, string Url)> Kept = new();

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("new server branch ").Append(Name).Append(" from ").Append(Source).Append(", ").Append(Repos.Count).Append(" repositories, one revision each:\n");
        foreach (var (wc, url) in Kept) sb.Append("  kept  ").Append(wc).Append(" stays at ").Append(url).Append('\n');
        foreach (var r in Repos)
        {
            sb.Append("  ").Append(r.ReposRoot).Append('\n');
            foreach (var m in r.Mkdirs) sb.Append("    mkdir ").Append(m).Append('\n');
            foreach (var (src, dst) in r.Copies) sb.Append("    copy  ").Append(src).Append("\n       -> ").Append(dst).Append('\n');
            foreach (var (url, value) in r.PropSets)
            {
                sb.Append("    svn:externals on ").Append(url).Append('\n');
                foreach (var line in value.Split('\n').Where(l => l.Trim().Length > 0)) sb.Append("       ").Append(line).Append('\n');
            }
        }
        sb.Append("message: ").Append(Message);
        return sb.ToString();
    }
}

/// <summary>
/// What the new branch does with one working copy of the checkout: the root or one external. The
/// default is the plain rule under the name of the whole. An external can get a name of its own, or
/// stay where it is: then nothing is copied for it, and the svn:externals line that brings it in is
/// left alone, so the new branch reads the same engine, or tools, as the branch it came from.
/// </summary>
public sealed class BranchPart
{
    /// <summary>"" for the root, else the path of the external inside the checkout, like libs/tools.</summary>
    public string Wc = "";
    /// <summary>Where it points now.</summary>
    public string Url = "";
    /// <summary>A branch name for this one. Null or empty means the name of the whole.</summary>
    public string? Branch;
    /// <summary>Leave it where it is. The root cannot be kept, and an external inside a kept one is kept with it.</summary>
    public bool Keep;
}

/// <summary>Server branches and server checkouts. See DESIGN.md, "New server branch" and "New server checkout".</summary>
public static class Server
{
    static readonly UTF8Encoding Utf8 = new(false);

    // ---- new server branch ----

    /// <summary>
    /// Works out every copy and every rewritten svn:externals, one transaction per repository. Touches
    /// the server only to read. parts says what each external gets; one it does not name gets the
    /// plain rule under name.
    /// </summary>
    public static ServerBranchPlan PlanBranch(SgRoot root, CheckoutConfig co, string name, string? message = null, IReadOnlyList<BranchPart>? parts = null)
    {
        var svn = root.Svn;
        CheckName(name);
        var overrides = root.Config.BranchUrlOverrides;

        // What each working copy gets. The root is the branch itself, so it can be neither kept nor named apart.
        var choices = new Dictionary<string, BranchPart>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts ?? [])
        {
            var wc = PathUtil.Rel(p.Wc);
            if (wc.Length == 0 && (p.Keep || p.Branch is { Length: > 0 })) throw new SgException("the root of the checkout is the branch itself: it cannot be kept or given another name");
            if (p.Branch is { Length: > 0 } b) CheckName(b);
            choices[wc] = p;
        }
        string BranchOf(string wc) => choices.TryGetValue(wc, out var p) && p.Branch is { Length: > 0 } b ? b : name;
        bool KeptByChoice(string wc) => choices.TryGetValue(wc, out var p) && p.Keep;
        // An external inside a kept one goes with it: the line that brings it in lives in a working copy nobody copies.
        bool Kept(string wc) => wc.Length > 0 && (KeptByChoice(wc) || PathUtil.Ancestors(wc).Any(KeptByChoice));
        string Rule(string url, string wc) => BranchRule.NewUrl(url, BranchOf(wc), overrides);

        var status = svn.Status(co.Path, noIgnore: false);
        var wcs = new List<string> { "" };
        wcs.AddRange(status.Where(e => e.Item == "external" && e.Path.Length > 0).Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal));

        var plan = new ServerBranchPlan { Name = name, Source = co.Name, Message = message ?? "- Creating branch: " + name };

        // Repositories are told apart by UUID: the same server shows up under more than one host name.
        var wcInfos = wcs.Select(wc => (Wc: wc, Info: svn.Info(PathUtil.Join(co.Path, wc), "."))).ToList();
        var aliases = new Dictionary<string, List<string>>();
        foreach (var (_, info) in wcInfos)
        {
            if (!aliases.TryGetValue(info.Uuid, out var list)) aliases[info.Uuid] = list = new List<string>();
            var rootUrl = info.ReposRoot.TrimEnd('/');
            if (!list.Contains(rootUrl, StringComparer.OrdinalIgnoreCase)) list.Add(rootUrl);
        }
        var rootInfo = wcInfos.First(w => w.Wc.Length == 0).Info;
        var rootHost = HostOf(rootInfo.ReposRoot);
        string Canon(string uuid) => aliases[uuid].FirstOrDefault(a => HostOf(a).Equals(rootHost, StringComparison.OrdinalIgnoreCase)) ?? aliases[uuid][0];
        string Normalize(string url, string uuid)
        {
            foreach (var a in aliases[uuid])
                if (url.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return Canon(uuid) + url[a.Length..];
            return url;
        }
        string? UuidOf(string url) =>
            aliases.FirstOrDefault(kv => kv.Value.Any(a => url.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase) || url.Equals(a, StringComparison.OrdinalIgnoreCase))).Key;

        var copies = new List<(string Uuid, string Src, string Dst)>();
        var propsets = new List<(string Uuid, string Url, string Value)>();
        foreach (var (wc, info) in wcInfos)
        {
            if (Kept(wc)) { plan.Kept.Add((wc, info.Url.TrimEnd('/'))); continue; }
            var src = Normalize(info.Url.TrimEnd('/'), info.Uuid);
            var dst = Rule(src, wc);
            copies.Add((info.Uuid, src, dst));
            if (wc.Length == 0) plan.NewRootUrl = dst;
            foreach (var (dir, value) in svn.PropGetRecursive(PathUtil.Join(co.Path, wc), "svn:externals"))
            {
                // The folder the property sits on, inside the checkout: every line of it lands under there.
                var at = PathUtil.Rel(wc.Length == 0 ? dir : dir.Length == 0 ? wc : wc + "/" + dir);
                var changed = false;
                var rewritten = Externals.Rewrite(value, (url, local) =>
                {
                    var target = PathUtil.Rel(at.Length == 0 ? local : at + "/" + local);
                    if (Kept(target)) return null;
                    changed = true;
                    return Rule(url, target);
                });
                // A property whose every line is kept is already right on the copy.
                if (changed) propsets.Add((info.Uuid, dir.Length == 0 ? dst : dst + "/" + dir, rewritten));
            }
        }

        // A copy inside another copy of the same repository, landing inside that one's copy, is already covered by it.
        // Landing elsewhere, under a name of its own, it is a copy in its own right.
        var distinct = copies
            .Where(c => !copies.Any(o => o.Uuid == c.Uuid && !o.Src.Equals(c.Src, StringComparison.OrdinalIgnoreCase)
                                         && PathUtil.IsUnder(c.Src, o.Src) && PathUtil.IsUnder(c.Dst, o.Dst)))
            .GroupBy(c => (c.Uuid, Dst: c.Dst.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
        var groups = aliases.Keys.ToDictionary(uuid => uuid, uuid => new RepoPlan
        {
            ReposRoot = Canon(uuid),
            Copies = distinct.Where(k => k.Uuid == uuid).Select(k => (k.Src, k.Dst)).ToList(),
            PropSets = propsets.Where(p => p.Uuid == uuid).Select(p => (p.Url, p.Value)).ToList(),
        });

        // Repositories that others point at go first, the root's repository last.
        var deps = groups.Keys.ToDictionary(u => u, _ => new HashSet<string>());
        foreach (var (uuid, _, value) in propsets)
            foreach (var (url, _) in Externals.Definitions(value))
            {
                var target = UuidOf(url);
                if (target != null && target != uuid) deps[uuid].Add(target);
            }
        var ordered = new List<string>();
        var remaining = groups.Keys.OrderBy(u => groups[u].ReposRoot, StringComparer.OrdinalIgnoreCase).ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(u => u != rootInfo.Uuid && deps[u].All(d => !remaining.Contains(d)))
                       ?? remaining.FirstOrDefault(u => u != rootInfo.Uuid)
                       ?? remaining[0];
            ordered.Add(next);
            remaining.Remove(next);
        }
        // A repository every working copy of which is kept has nothing to commit.
        plan.Repos = ordered.Select(u => groups[u]).Where(rp => rp.Copies.Count > 0 || rp.PropSets.Count > 0).ToList();

        foreach (var rp in plan.Repos)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, dst) in rp.Copies)
            {
                if (svn.UrlExists(dst)) throw new SgException("already on the server: " + dst);
                var missing = new List<string>();
                var parent = ParentUrl(dst);
                while (parent != null && parent.Length > rp.ReposRoot.TrimEnd('/').Length && !known.Contains(parent) && !svn.UrlExists(parent))
                {
                    missing.Add(parent);
                    parent = ParentUrl(parent);
                }
                missing.Reverse();
                foreach (var m in missing) if (known.Add(m)) rp.Mkdirs.Add(m);
                known.Add(dst);
            }
        }
        return plan;
    }

    static void CheckName(string name)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(' ')) throw new SgException("bad branch name: " + name);
    }

    /// <summary>The root and every external of a checkout, each on the plain rule, for a caller to adjust before PlanBranch.</summary>
    public static List<BranchPart> Parts(SgRoot root, CheckoutConfig co)
    {
        var res = new List<BranchPart> { new() { Url = root.Svn.Info(co.Path, ".").Url.TrimEnd('/') } };
        res.AddRange(Ops.ExternalsOf(root, co).Select(e => new BranchPart { Wc = e.Rel, Url = e.Url.TrimEnd('/') }));
        return res;
    }

    static string? ParentUrl(string url)
    {
        var i = url.TrimEnd('/').LastIndexOf('/');
        return i > 0 ? url[..i] : null;
    }

    static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>Runs the plan. One svnmucc transaction per repository, in plan order. Stops at the first failure.</summary>
    public static void ExecuteBranch(SgRoot root, ServerBranchPlan plan)
    {
        foreach (var rp in plan.Repos)
        {
            var actions = new List<string>();
            foreach (var m in rp.Mkdirs) actions.AddRange(["mkdir", m]);
            foreach (var (src, dst) in rp.Copies) actions.AddRange(["cp", "HEAD", src, dst]);
            var files = new List<string>();
            foreach (var (url, value) in rp.PropSets)
            {
                var f = root.NewTempFile(".externals");
                File.WriteAllText(f, value.TrimEnd('\n') + "\n", Utf8);
                files.Add(f);
                actions.AddRange(["propsetf", "svn:externals", f, url]);
            }
            root.Log.Info($"{rp.ReposRoot}: {rp.Copies.Count} copies, {rp.PropSets.Count} externals");
            try
            {
                rp.Revision = root.Svn.Mucc(actions, plan.Message);
                rp.State = "committed";
                root.Log.Info($"  r{rp.Revision}");
            }
            catch (SgException ex)
            {
                rp.State = "failed";
                foreach (var rest in plan.Repos.Where(r => r.State == "planned")) rest.State = "skipped";
                var done = plan.Repos.Where(r => r.State == "committed").Select(r => r.ReposRoot).ToList();
                throw new SgException($"branch creation stopped at {rp.ReposRoot}: {ex.Message}"
                                      + (done.Count > 0 ? "\nThese repositories already have the new branch: " + string.Join(", ", done) : ""));
            }
            finally
            {
                foreach (var f in files) File.Delete(f);
            }
        }
    }

    // ---- new server checkout ----

    /// <summary>
    /// A new checkout of a server branch: copy the nearest checkout on disk, drop its local edits,
    /// svn switch it to the branch (only differences download), then register it like any checkout.
    /// </summary>
    public static CheckoutResult Checkout(SgRoot root, CheckoutConfig near, string target, string? name = null)
    {
        var svn = root.Svn;
        var log = root.Log;
        var url = target.Contains("://") || target.StartsWith("^/") ? target : BranchRule.NewUrl(near.Url.TrimEnd('/'), target, root.Config.BranchUrlOverrides);
        if (url.StartsWith("^/")) url = near.ReposRoot.TrimEnd('/') + url[1..];
        url = url.TrimEnd('/');
        name ??= url.Split('/').Last();
        root.Git.CheckBranchName(name);
        var dir = Path.Combine(root.RootPath, name);
        if (Directory.Exists(dir) || File.Exists(dir)) throw new SgException("folder exists: " + dir);
        if (root.Config.Checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new SgException("checkout name already in use: " + name);
        if (!svn.UrlExists(url)) throw new SgException("not on the server: " + url);
        using var _ = root.Lock();

        // 1. Copy the versioned content and the .svn folders. Leave out unversioned and ignored items and the .git file.
        var status = svn.Status(near.Path, noIgnore: true);
        var junk = status.Where(e => e.Path.Length > 0 && e.Item is "unversioned" or "ignored").Select(e => PathUtil.Join(near.Path, e.Path)).ToList();
        log.Info($"copying {near.Path} to {dir}");
        Robocopy(root, near.Path, dir, junk);

        // 2. The copy carries the local edits of the source. Drop them, in every working copy.
        var wcs = new List<string> { "" };
        wcs.AddRange(status.Where(e => e.Item == "external" && e.Path.Length > 0).Select(e => e.Path));
        foreach (var wc in wcs)
        {
            var r = svn.Revert(PathUtil.Join(dir, wc), ["."]);
            if (!r.Ok) log.Warn("svn revert in " + wc + ": " + r.StdErr.Trim());
        }

        // 3. Switch. Externals in the same repository switch in place.
        log.Info("svn switch " + url);
        var sw = svn.Switch(dir, url);
        if (sw.Conflicts > 0) log.Warn($"{sw.Conflicts} conflict(s) after the switch, check svn status in {dir}");

        // 4. Same skip list, shared folders and their mode, and optional folders as the source.
        return Ops.CheckoutAdd(root, dir, near.Skip, near.Junctions, near.Optional, name, near.Shared);
    }

    static void Robocopy(SgRoot root, string src, string dst, List<string> excludeAbs)
    {
        var args = new List<string> { src, dst, "/E", "/XJ", "/COPY:DAT", "/DCOPY:DAT", "/R:2", "/W:1", "/NJH", "/NJS", "/NDL", "/NP", "/MT:8" };
        var dirs = excludeAbs.Where(Directory.Exists).ToList();
        var files = excludeAbs.Where(File.Exists).ToList();
        if (dirs.Count + files.Count > 400)
        {
            root.Log.Warn("more than 400 unversioned items in the source, they get copied too");
            dirs.Clear();
            files.Clear();
        }
        args.Add("/XF");
        args.Add(Path.Combine(src, ".git"));
        args.AddRange(files);
        if (dirs.Count > 0) { args.Add("/XD"); args.AddRange(dirs); }

        var n = 0;
        var r = Proc.RunStreaming("robocopy", args, null, root.Log, line =>
        {
            if (!line.Contains('\t')) return;
            n++;
            root.Log.Progress("copying", n, 0, "files", null);
        }, null);
        root.Log.ProgressEnd("copying", n + " files");
        if (r.ExitCode >= 8) throw new SgException($"robocopy failed with code {r.ExitCode}:\n{r.StdErr}{r.StdOut}");
    }
}
