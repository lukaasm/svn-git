namespace Sg.Core;

/// <summary>One submodule as a commit's .gitmodules declares it, with the commit that commit pins it at.</summary>
public sealed record GitSubmoduleEntry(string Name, string Path, string Url, string? Branch, string Pin);

/// <summary>
/// One git repository of a checkout: the clone itself, or a submodule checked out inside it, at any
/// depth. A submodule is to a git checkout what an external is to an SVN one: a working copy of its
/// own, from a repository of its own, at a folder of the checkout. sg reads and writes each file in
/// the repository it belongs to, and a commit of it goes to that repository's server.
///
/// A submodule is pinned or switched. Pinned is how git leaves one: HEAD detached at the commit its
/// parent pins. Its content is that commit, and a commit sg makes in it goes to the branch .gitmodules
/// names, or the remote's default branch, with the parent pinning the new commit in the same push.
/// Switched is one checked out on a branch that tracks a remote branch: its content is that branch, a
/// commit goes there, and the parent's pin is left alone - the way an external moved with svn switch
/// changes nothing anyone else sees.
/// </summary>
public sealed class GitUnit
{
    readonly Lazy<(string? Local, string? Remote, string? Branch)> _tracking;
    readonly Lazy<string> _remote;
    readonly Lazy<string> _url;
    readonly Lazy<string> _branch;
    readonly string? _location;

    /// <summary>"" for the clone, else the submodule's folder from the checkout root, like libs/core.</summary>
    public string Wc { get; }
    public GitRepo Repo { get; }
    /// <summary>The repository it sits in. Null for the clone.</summary>
    public GitUnit? Parent { get; }
    /// <summary>What its parent's .gitmodules says about it. Null for the clone.</summary>
    public GitSubmoduleEntry? Entry { get; }

    GitUnit(string wc, GitRepo repo, GitUnit? parent, GitSubmoduleEntry? entry, Func<GitUnit, string> remote,
        Func<GitUnit, string> url, Func<GitUnit, string> branch, string? location)
    {
        Wc = wc;
        Repo = repo;
        Parent = parent;
        Entry = entry;
        _tracking = new(() => Repo.Tracking());
        _remote = new(() => remote(this));
        _url = new(() => url(this));
        _branch = new(() => branch(this));
        _location = location;
    }

    public static GitUnit Clone(SgRoot root, CheckoutConfig co) => new(
        "", new GitRepo(root.Config.GitExe, co.Path, root.Log), null, null,
        _ => co.Remote ?? "origin",
        _ => co.ReposRoot,
        _ => co.Branch is { Length: > 0 } b ? b : throw new SgException($"{co.Name} names no branch. Register it again."),
        co.Url);

    public static GitUnit Submodule(SgRoot root, GitUnit parent, GitSubmoduleEntry entry, string wc, string dir) => new(
        wc, new GitRepo(root.Config.GitExe, dir, root.Log), parent, entry,
        u => u.Tracking is { Remote: { } r, Branch: not null } ? r
            : Remotes(u.Repo) is { Count: > 0 } all ? (all.Contains("origin") ? "origin" : all[0])
            : throw new SgException($"submodule {wc} has no remote to send to"),
        u =>
        {
            var r = Remotes(u.Repo).Count > 0 ? u.Repo.Run("remote", "get-url", u.Remote) : null;
            return r is { Ok: true } && r.StdOut.Trim().Length > 0 ? r.StdOut.Trim().TrimEnd('/') : GitSubmodules.Resolve(entry.Url, parent.Url);
        },
        u => u.Tracking is { Local: not null, Remote: not null, Branch: { } tracked } ? tracked
            : entry.Branch == "." ? parent.Branch
            : entry.Branch is { Length: > 0 } declared ? declared
            : GitSubmodules.DefaultBranch(u.Repo, u.Remote, wc),
        null);

    static List<string> Remotes(GitRepo repo) =>
        repo.Run("remote").StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>The local branch HEAD is on and the remote branch it tracks, read once.</summary>
    public (string? Local, string? Remote, string? Branch) Tracking => _tracking.Value;

    /// <summary>The remote its commits go to.</summary>
    public string Remote => _remote.Value;

    /// <summary>The server repository.</summary>
    public string Url => _url.Value;

    /// <summary>The server branch its commits go to.</summary>
    public string Branch => _branch.Value;

    /// <summary>A submodule on a branch that tracks a remote branch, and not at the commit its parent pins.</summary>
    public bool Switched => Parent != null && Tracking is { Local: not null, Remote: not null, Branch: not null };

    /// <summary>A submodule whose content is the commit its parent pins.</summary>
    public bool Pinned => Parent != null && !Switched;

    /// <summary>The commit the parent pins it at. Empty for the clone.</summary>
    public string Pin => Entry?.Pin ?? "";

    public string TrackingRef => $"refs/remotes/{Remote}/{Branch}";

    /// <summary>
    /// Where it points, as a location: the clone's url#branch; a switched submodule's url#branch; a
    /// pinned one's bare URL, since what it holds is a commit and not a branch.
    /// </summary>
    public string Location => _location ?? (Switched ? GitLocation.Format(Url, Branch) : Url);

    public string Label => Wc.Length == 0 ? "root" : Wc;

    /// <summary>A path from the checkout root, as a path in this repository.</summary>
    public string RelOf(string path)
    {
        path = PathUtil.Rel(path);
        if (Wc.Length == 0) return path;
        return path.Length <= Wc.Length ? "" : path[(Wc.Length + 1)..];
    }

    /// <summary>A path in this repository, as a path from the checkout root.</summary>
    public string Full(string rel) => Wc.Length == 0 ? rel : rel.Length == 0 ? Wc : Wc + "/" + rel;

    /// <summary>The server branch, fetched into its remote-tracking ref.</summary>
    public void Fetch()
    {
        var r = Repo.Run("fetch", "--quiet", Remote, $"+refs/heads/{Branch}:{TrackingRef}");
        if (!r.Ok) throw new SgException($"git fetch {Remote} {Branch} failed in {Repo.Path}: " + r.StdErr.Trim());
    }
}

/// <summary>Reading submodules: what a commit declares, and which of them a checkout has on disk.</summary>
public static class GitSubmodules
{
    /// <summary>
    /// The submodules a commit declares: its .gitmodules, read out of the commit and not off the disk,
    /// each with the commit the same commit pins it at. An entry with no pin in the tree is not a
    /// submodule of that commit and is left out. run is the repository to ask, the store or a clone.
    /// </summary>
    public static List<GitSubmoduleEntry> Declared(Func<string[], ProcResult> run, string commit)
    {
        // What a commit declares never changes, whichever repository holds it: two processes each time the
        // checkout's submodules were listed, and every operation lists them, some of them twice.
        if (commit.Length == 40 && commit.All(Uri.IsHexDigit))
        {
            if (DeclaredBy.TryGetValue(commit, out var known)) return [.. known];
            var read = Read(run, commit);
            if (read != null)
            {
                if (DeclaredBy.Count > 10_000) DeclaredBy.Clear();
                DeclaredBy[commit] = read;
            }
            return read == null ? new() : [.. read];
        }
        return Read(run, commit) ?? new();
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<GitSubmoduleEntry>> DeclaredBy = new(StringComparer.Ordinal);

    /// <summary>The .gitmodules and the pins of one commit; null when git could not read them, which is not kept.</summary>
    static List<GitSubmoduleEntry>? Read(Func<string[], ProcResult> run, string commit)
    {
        var cfg = run(["config", "--blob", commit + ":.gitmodules", "-z", "--get-regexp", @"^submodule\."]);
        // No .gitmodules in the commit is an answer (none); anything else git could not read is not.
        if (!cfg.Ok) return cfg.ExitCode == 1 ? new() : null;
        var fields = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var rec in cfg.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = rec.IndexOf('\n');
            var key = nl < 0 ? rec : rec[..nl];
            var dot = key.LastIndexOf('.');
            if (dot <= "submodule.".Length) continue;
            var name = key["submodule.".Length..dot];
            if (!fields.TryGetValue(name, out var f)) fields[name] = f = new Dictionary<string, string>(StringComparer.Ordinal);
            f[key[(dot + 1)..].ToLowerInvariant()] = nl < 0 ? "" : rec[(nl + 1)..];
        }
        var byPath = new Dictionary<string, (string Name, Dictionary<string, string> Fields)>(StringComparer.Ordinal);
        foreach (var (name, f) in fields)
            if (f.TryGetValue("path", out var p) && PathUtil.Rel(p).Length > 0) byPath[PathUtil.Rel(p)] = (name, f);
        if (byPath.Count == 0) return new();

        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        var ls = run(new[] { "ls-tree", "-z", commit, "--" }.Concat(byPath.Keys).ToArray());
        if (!ls.Ok) return null;
        foreach (var line in ls.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var meta = line[..tab].Split(' ');
            if (meta.Length >= 3 && meta[0] == "160000") pins[line[(tab + 1)..]] = meta[2];
        }
        return byPath.Where(kv => pins.ContainsKey(kv.Key))
            .Select(kv => new GitSubmoduleEntry(kv.Value.Name, kv.Key, kv.Value.Fields.GetValueOrDefault("url", ""),
                kv.Value.Fields.GetValueOrDefault("branch"), pins[kv.Key]))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A folder with a clone in it: a .git folder, or the .git file a submodule gets.</summary>
    public static bool CheckedOut(string dir) =>
        File.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, ".git"));

    public static bool Skipped(CheckoutConfig co, string wc) => co.Skip.Any(s => PathUtil.IsUnder(wc, PathUtil.Rel(s)));

    /// <summary>
    /// The clone, then every submodule checked out in it at any depth, each after the repository it
    /// sits in. A skipped one is left out, and so is everything inside it. Read from each repository's
    /// HEAD, so a .gitmodules edited and not committed yet changes nothing.
    /// </summary>
    public static List<GitUnit> Units(SgRoot root, CheckoutConfig co)
    {
        var res = new List<GitUnit> { GitUnit.Clone(root, co) };
        for (var i = 0; i < res.Count; i++)
        {
            var u = res[i];
            if (!File.Exists(Path.Combine(u.Repo.Path, ".gitmodules"))) continue;
            foreach (var e in Declared(a => u.Repo.Run(a), u.Repo.HeadCommit()))
            {
                var wc = u.Full(e.Path);
                if (Skipped(co, wc)) continue;
                var dir = PathUtil.Join(co.Path, wc);
                if (CheckedOut(dir)) res.Add(GitUnit.Submodule(root, u, e, wc, dir));
            }
        }
        return res;
    }

    /// <summary>The innermost repository a path sits in. The clone always matches.</summary>
    public static GitUnit Innermost(IReadOnlyList<GitUnit> units, string path)
    {
        path = PathUtil.Rel(path);
        var best = units[0];
        foreach (var u in units)
            if (u.Wc.Length > best.Wc.Length && PathUtil.IsUnder(path, u.Wc)) best = u;
        return best;
    }

    /// <summary>The repository a working copy name stands for: its own when it is one, else the one it sits in.</summary>
    public static GitUnit Of(IReadOnlyList<GitUnit> units, string wc) =>
        units.FirstOrDefault(u => u.Wc.Equals(PathUtil.Rel(wc), StringComparison.OrdinalIgnoreCase)) ?? Innermost(units, wc);

    /// <summary>Paths from the checkout root, split by the repository each belongs to and made relative to it.</summary>
    public static List<(GitUnit Unit, List<string> Paths)> ByUnit(IReadOnlyList<GitUnit> units, IEnumerable<string> paths) =>
        paths.Select(p => (Unit: Innermost(units, p), Path: p))
            .GroupBy(x => x.Unit)
            .Select(g => (g.Key, g.Select(x => g.Key.RelOf(x.Path) is { Length: > 0 } r ? r : ".").Distinct(StringComparer.Ordinal).ToList()))
            .ToList();

    /// <summary>
    /// A .gitmodules URL made absolute the way git makes it: one starting ./ or ../ is relative to the
    /// parent repository's own URL, and each ../ takes one folder off it.
    /// </summary>
    public static string Resolve(string url, string parentUrl)
    {
        url = url.Trim();
        if (!url.StartsWith("./", StringComparison.Ordinal) && !url.StartsWith("../", StringComparison.Ordinal)) return url.TrimEnd('/');
        var at = parentUrl.TrimEnd('/');
        while (true)
        {
            if (url.StartsWith("./", StringComparison.Ordinal)) url = url[2..];
            else if (url.StartsWith("../", StringComparison.Ordinal))
            {
                var cut = at.LastIndexOf('/');
                if (cut > 0) at = at[..cut];
                url = url[3..];
            }
            else break;
        }
        return (at + "/" + url).TrimEnd('/');
    }

    /// <summary>
    /// The branch a remote's HEAD names: what a clone of it checks out, and where a pinned submodule's
    /// commits go when .gitmodules names none.
    /// </summary>
    public static string DefaultBranch(GitRepo repo, string remote, string wc)
    {
        var prefix = $"refs/remotes/{remote}/";
        var sym = repo.Run("symbolic-ref", "-q", prefix + "HEAD");
        if (sym.Ok && sym.StdOut.Trim().StartsWith(prefix, StringComparison.Ordinal)) return sym.StdOut.Trim()[prefix.Length..];
        var ls = repo.Run("ls-remote", "--symref", remote, "HEAD");
        if (ls.Ok)
            foreach (var line in ls.StdOut.Split('\n'))
            {
                var tab = line.IndexOf('\t');
                if (line.StartsWith("ref: refs/heads/", StringComparison.Ordinal) && tab > 16) return line[16..tab];
            }
        throw new SgException($"submodule {wc} names no branch in .gitmodules, and the default branch of {remote} could not be read. "
                              + "Name one: git config -f .gitmodules submodule.<name>.branch <branch>");
    }

    /// <summary>Two ways of writing one repository's URL: case, a trailing slash and a .git ending do not tell them apart.</summary>
    public static bool SameRepo(string a, string b)
    {
        static string N(string u)
        {
            u = u.Trim().TrimEnd('/');
            return u.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? u[..^4] : u;
        }
        return N(a).Equals(N(b), StringComparison.OrdinalIgnoreCase);
    }
}
