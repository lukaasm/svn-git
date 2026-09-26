using System.Text;

namespace Sg.Core;

/// <summary>
/// How a git checkout names a place on its server: the remote's URL and a branch, written url#branch.
/// An SVN URL is already one place - a branch folder of a repository - and every URL match in sg (import,
/// restore, drift, the backup marker) keys on that. A git URL names a whole repository, so the branch
/// goes after a #, which a clone URL never carries, and every one of those matches keeps working.
/// </summary>
public static class GitLocation
{
    public static string Format(string url, string branch) => url.TrimEnd('/') + "#" + branch;

    /// <summary>The repository URL and the branch. Branch is null when the location names none.</summary>
    public static (string Url, string? Branch) Parse(string location)
    {
        var i = location.LastIndexOf('#');
        if (i < 0) return (location.Trim().TrimEnd('/'), null);
        var branch = location[(i + 1)..].Trim();
        return (location[..i].Trim().TrimEnd('/'), branch.Length > 0 ? branch : null);
    }

    public static string WithBranch(string location, string branch) => Format(Parse(location).Url, branch);

    /// <summary>The repository's name: the last part of its URL without .git.</summary>
    public static string RepoName(string url)
    {
        var u = Parse(url).Url.TrimEnd('/', '\\');
        var last = u.Split('/', '\\', ':').Last();
        return last.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? last[..^4] : last;
    }

    static readonly string[] GitHosts = ["github.com", "gitlab.com", "bitbucket.org", "dev.azure.com", "ssh.dev.azure.com", "codeberg.org"];

    /// <summary>
    /// Whether a URL is a git server's. A git URL says so: a #branch, a .git on the end, git@ or git://,
    /// an scp-style host:path, one of the big hosts, or a folder that is a git repository. Anything else
    /// is taken for SVN, which is what every URL was before git checkouts.
    /// </summary>
    public static CheckoutKind KindOfUrl(string url)
    {
        var u = url.Trim();
        if (u.Contains('#')) return CheckoutKind.Git;
        var bare = u.TrimEnd('/');
        if (bare.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) return CheckoutKind.Git;
        if (u.StartsWith("git@", StringComparison.OrdinalIgnoreCase) || u.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
            return CheckoutKind.Git;
        var scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            // user@host:path, the scp form, but not C:\ on Windows.
            var colon = u.IndexOf(':');
            if (colon > 1 && u.IndexOf('@') is var at && at >= 0 && at < colon) return CheckoutKind.Git;
            try
            {
                if (Directory.Exists(Path.Combine(u, ".git")) || File.Exists(Path.Combine(u, "HEAD")) && Directory.Exists(Path.Combine(u, "objects")))
                    return CheckoutKind.Git;
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { }
            return CheckoutKind.Svn;
        }
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && GitHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
            return CheckoutKind.Git;
        return CheckoutKind.Svn;
    }
}

/// <summary>
/// The checkout's own git: the clone the user works in, with its own config, remotes and identity. It is
/// never the store, so none of the store's settings and none of sg's identity go into what it runs.
/// </summary>
public sealed class GitRepo
{
    static readonly UTF8Encoding Utf8 = new(false);
    readonly string _exe;
    readonly ILog _log;
    readonly Dictionary<string, string> Env;

    public string Path { get; }

    public GitRepo(string exe, string path, ILog log)
    {
        var git = GitExecutable.Resolve(exe);
        _exe = git.Exe;
        Path = path;
        _log = log;
        Env = new Dictionary<string, string>(git.Env)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["LC_ALL"] = "C",
            ["GIT_EDITOR"] = "true",
            ["GIT_PAGER"] = "cat",
            ["GIT_MERGE_AUTOEDIT"] = "no",
        };
    }

    public ProcResult Run(IEnumerable<string> args, byte[]? stdin = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var list = new List<string> { "-C", Path };
        list.AddRange(args);
        IReadOnlyDictionary<string, string> e = Env;
        if (env != null)
        {
            var d = new Dictionary<string, string>(Env);
            foreach (var kv in env) d[kv.Key] = kv.Value;
            e = d;
        }
        // While an operation runs, what a clone says of its own refs, config and objects is asked once:
        // every operation lists the checkout's submodules again, and each asked its HEAD, its remotes
        // and its commits anew. A write anywhere - here, in the store, in another clone - ends it.
        string? key = null;
        long writes = 0;
        if (stdin == null && env == null && Git.AnyRemembering && RepoRead(list.Skip(2)))
        {
            key = Path + "\0" + string.Join("\0", list.Skip(2));
            writes = Git.AnyWrites;
            if (Memo.TryGetValue(key, out var known) && known.Writes == writes) return known.Result;
        }
        var result = Proc.Run(_exe, list, Path, _log, stdin, e);
        // A clone's fetch or push can be the very remote or ref a store operation has read.
        if (Git.Changes(list, out _)) Git.Forget();
        else if (key != null && Git.AnyWrites == writes) Memo[key] = (writes, result);
        return result;
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Writes, ProcResult Result)> Memo = new(StringComparer.Ordinal);

    /// <summary>The last operation or read let go: nothing a clone said is kept past it.</summary>
    internal static void ForgetAll() => Memo.Clear();

    /// <summary>
    /// A command that reads only what the repository holds - refs, objects, config, remotes - never the
    /// working files, which change without a git command saying so.
    /// </summary>
    static bool RepoRead(IEnumerable<string> args)
    {
        var words = args.ToList();
        var verb = words.FirstOrDefault(w => !w.StartsWith('-'));
        var rest = verb == null ? [] : words.Skip(words.IndexOf(verb) + 1).Where(w => !w.StartsWith('-')).ToList();
        return verb switch
        {
            "rev-parse" or "cat-file" or "ls-tree" or "for-each-ref" or "show-ref" or "merge-base" or "rev-list" or "log" => true,
            "config" => words.Any(w => w is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l"),
            "symbolic-ref" => rest.Count == 1,
            "remote" => rest.Count == 0 || rest[0] == "get-url",
            _ => false,
        };
    }

    public ProcResult Run(params string[] args) => Run((IEnumerable<string>)args);
    public ProcResult Ok(params string[] args) => Run(args).EnsureOk();
    public string Out(params string[] args) => Ok(args).StdOut.Trim();

    /// <summary>A command over many paths, read from a file so no path list outgrows a command line.</summary>
    public ProcResult WithPaths(IEnumerable<string> args, IEnumerable<string> paths, IReadOnlyDictionary<string, string>? env = null)
    {
        var f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sg-ps-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllBytes(f, Utf8.GetBytes(string.Join("\0", paths.Select(p => ":(literal)" + p))));
        try { return Run(args.Concat(["--pathspec-from-file=" + f, "--pathspec-file-nul"]), null, env); }
        finally { File.Delete(f); }
    }

    /// <summary>The commit a name points at, or null when it does not name one.</summary>
    public string? Rev(string rev)
    {
        // HEAD is read from the files when they say plainly where it is.
        if (rev == "HEAD" && HeadCommit() is var head && head != "HEAD") return head;
        var r = Run("rev-parse", "--verify", "-q", rev + "^{commit}");
        return r.Ok ? r.StdOut.Trim() : null;
    }

    /// <summary>
    /// The commit HEAD is at, read from the repository's files as git reads them: the .git folder, or
    /// the folder a submodule's .git file names; HEAD, then the branch it names, loose or in
    /// packed-refs, in the common folder a linked worktree names. "HEAD" - for git to resolve - when
    /// the files say anything else, so the answer is never a guess.
    /// </summary>
    public string HeadCommit()
    {
        try
        {
            var dotGit = System.IO.Path.Combine(Path, ".git");
            string gitDir;
            if (Directory.Exists(dotGit)) gitDir = dotGit;
            else if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var line && line.StartsWith("gitdir: ", StringComparison.Ordinal))
                gitDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, line[8..].Trim()));
            else return "HEAD";
            var common = File.Exists(System.IO.Path.Combine(gitDir, "commondir"))
                ? System.IO.Path.GetFullPath(System.IO.Path.Combine(gitDir, File.ReadAllText(System.IO.Path.Combine(gitDir, "commondir")).Trim()))
                : gitDir;
            var head = File.ReadAllText(System.IO.Path.Combine(gitDir, "HEAD")).Trim();
            if (IsSha(head)) return head;
            if (!head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) return "HEAD";
            var name = head[5..];
            var loose = System.IO.Path.Combine(common, name.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(loose)) return File.ReadAllText(loose).Trim() is var sha && IsSha(sha) ? sha : "HEAD";
            var packed = System.IO.Path.Combine(common, "packed-refs");
            if (!File.Exists(packed)) return "HEAD";
            foreach (var entry in File.ReadLines(packed))
                if (entry.Length > 41 && entry[40] == ' ' && entry[41..] == name && IsSha(entry[..40])) return entry[..40];
            return "HEAD";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "HEAD";
        }
        static bool IsSha(string s) => s.Length == 40 && s.All(Uri.IsHexDigit);
    }

    public string? ConfigGet(string key)
    {
        var r = Run("config", "--get", key);
        return r.Ok && r.StdOut.Trim().Length > 0 ? r.StdOut.Trim() : null;
    }

    /// <summary>
    /// The branch HEAD names, read from the HEAD file as HeadCommit reads it: "" when HEAD is detached,
    /// null when the files say anything else and git has to be asked.
    /// </summary>
    string? HeadBranchFromFiles()
    {
        try
        {
            var dotGit = System.IO.Path.Combine(Path, ".git");
            string gitDir;
            if (Directory.Exists(dotGit)) gitDir = dotGit;
            else if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is var line && line.StartsWith("gitdir: ", StringComparison.Ordinal))
                gitDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, line[8..].Trim()));
            else return null;
            var head = File.ReadAllText(System.IO.Path.Combine(gitDir, "HEAD")).Trim();
            if (head.Length == 40 && head.All(Uri.IsHexDigit)) return "";
            if (!head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)) return null;
            var branch = head[16..];
            return branch.Length == 0 || branch == ".invalid" ? null : branch;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// branch.* and remote.* in one read: the values as `git config --get` gives them (the last one of a
    /// key), and the remotes as git remote lists them, sorted.
    /// Null when remotes are also defined the old way, in .git/remotes or .git/branches, which git remote
    /// reads too.
    /// </summary>
    (Dictionary<string, string> Values, List<string> Remotes)? RemoteConfig()
    {
        try
        {
            var dotGit = System.IO.Path.Combine(Path, ".git");
            if (Directory.Exists(dotGit)
                && (Directory.Exists(System.IO.Path.Combine(dotGit, "remotes")) && Directory.EnumerateFileSystemEntries(System.IO.Path.Combine(dotGit, "remotes")).Any()
                    || Directory.Exists(System.IO.Path.Combine(dotGit, "branches")) && Directory.EnumerateFileSystemEntries(System.IO.Path.Combine(dotGit, "branches")).Any()))
                return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var r = Run("config", "-z", "--get-regexp", @"^(branch|remote)\.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var remotes = new List<string>();
        if (!r.Ok) return r.ExitCode == 1 ? (values, remotes) : null;
        foreach (var entry in r.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = entry.IndexOf('\n');
            var key = nl < 0 ? entry : entry[..nl];
            values[key] = nl < 0 ? "" : entry[(nl + 1)..];
            var dot = key.LastIndexOf('.');
            if (key.StartsWith("remote.", StringComparison.Ordinal) && dot > 7 && !remotes.Contains(key[7..dot])) remotes.Add(key[7..dot]);
        }
        // git remote lists them sorted, byte by byte.
        remotes.Sort(StringComparer.Ordinal);
        return (values, remotes);
    }

    /// <summary>`git remote`: the remotes, in the order git lists them.</summary>
    public List<string> Remotes() =>
        RemoteConfig()?.Remotes ?? Run("remote").StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    public bool IsAncestor(string a, string b) => Run("merge-base", "--is-ancestor", a, b).ExitCode == 0;

    /// <summary>How many commits are on the first-parent line up to and including this one: a git commit's revision number.</summary>
    public long Height(string commit) => long.TryParse(Out("rev-list", "--count", "--first-parent", commit), out var n) ? n : 0;

    public int Count(string range) => int.TryParse(Out("rev-list", "--count", range), out var n) ? n : 0;

    public string TreeOf(string commit) => Out("rev-parse", commit + "^{tree}");

    /// <summary>The folder git keeps this clone's state in: .git, or a linked worktree's own folder.</summary>
    public string GitDir => System.IO.Path.GetFullPath(Out("rev-parse", "--absolute-git-dir"));

    /// <summary>A commit made the way the user's own git makes one: their name, their email, their dates.</summary>
    public string CommitTree(string tree, IEnumerable<string> parents, string message)
    {
        var args = new List<string> { "commit-tree", tree };
        foreach (var p in parents) { args.Add("-p"); args.Add(p); }
        args.Add("-F");
        args.Add("-");
        var r = Run(args, Utf8.GetBytes(message));
        if (!r.Ok && r.StdErr.Contains("Please tell me who you are", StringComparison.Ordinal))
            throw new SgException($"git has no name and email to commit under in {Path}. Set them with: git config --global user.name \"...\" and git config --global user.email \"...\"");
        return r.EnsureOk().StdOut.Trim();
    }

    /// <summary>A replay, merge, cherry-pick or revert this clone is half way through, or null.</summary>
    public string? InProgress()
    {
        string dir;
        try { dir = GitDir; }
        catch (SgException) { return null; }
        if (Directory.Exists(System.IO.Path.Combine(dir, "rebase-merge")) || Directory.Exists(System.IO.Path.Combine(dir, "rebase-apply"))) return "rebase";
        if (File.Exists(System.IO.Path.Combine(dir, "MERGE_HEAD"))) return "merge";
        if (File.Exists(System.IO.Path.Combine(dir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
        if (File.Exists(System.IO.Path.Combine(dir, "REVERT_HEAD"))) return "revert";
        return null;
    }

    public List<string> Conflicted() =>
        Out("diff", "--name-only", "-z", "--diff-filter=U").Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();

    /// <summary>
    /// git log with the paths each commit changed against its first parent, as svn log -v gives them.
    /// Paths come back the way svn writes a repository path: with a slash in front.
    /// </summary>
    public List<LogRevision> Log(IEnumerable<string> range, bool renames = true)
    {
        // Rename detection reads file contents, which a partial clone would fetch one batch at a time.
        var args = new List<string> { "log", renames ? "-M" : "--no-renames", "--name-status", "--diff-merges=first-parent", "--format=%x1e%H%x1f%an%x1f%aI%x1f%B%x1f" };
        args.AddRange(range);
        var r = Run(args);
        if (!r.Ok) throw new SgException("git log failed in " + Path + ": " + r.StdErr.Trim());
        var res = new List<LogRevision>();
        foreach (var rec in r.StdOut.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.Split('\x1f');
            if (f.Length < 5) continue;
            var e = new LogRevision { Commit = f[0].Trim(), Author = f[1], Date = f[2].Trim(), Message = f[3].TrimEnd() };
            foreach (var raw in f[4].Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                var p = line.Split('\t');
                if (p.Length < 2) continue;
                var action = p[0][..1];
                if (action == "R" && p.Length >= 3) e.Paths.Add(new ChangedPath("R", "/" + p[2], "file", "/" + p[1]));
                else if (action == "C" && p.Length >= 3) e.Paths.Add(new ChangedPath("A", "/" + p[2], "file", "/" + p[1]));
                else e.Paths.Add(new ChangedPath(action == "T" ? "M" : action, "/" + p[1], "file", null));
            }
            res.Add(e);
        }
        return res;
    }

    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>What a commit is compared against: its first parent, or the empty tree for the first commit of a history.</summary>
    public string ParentOf(string commit) => Rev(commit + "^1") ?? EmptyTree;

    /// <summary>What one commit changed, against its first parent, whole or under one folder.</summary>
    public string CommitDiff(string commit, string? folder = null)
    {
        var args = new List<string> { "diff", "--no-color", "-M", ParentOf(commit), commit };
        if (folder != null && folder.Trim('/').Length > 0) { args.Add("--"); args.Add(":(literal)" + folder.Trim('/')); }
        var r = Run(args);
        return r.Ok ? r.StdOut : r.StdErr;
    }

    /// <summary>One file of a log entry, before or after its commit. Empty when it did not exist then.</summary>
    public string FileAt(LogRevision rev, ChangedPath path, bool before)
    {
        if (before && path.Action == "A") return "";
        if (!before && path.Action == "D") return "";
        var at = before ? Rev(rev.Commit + "^1") : rev.Commit;
        if (at == null) return "";
        var p = (before ? path.CopyFrom ?? path.Path : path.Path).TrimStart('/');
        var r = Run("show", at + ":" + p);
        return r.Ok ? r.StdOut : "";
    }

    /// <summary>The local branch HEAD is on, and the remote branch it tracks. Nulls where there is none.</summary>
    public (string? Local, string? Remote, string? Branch) Tracking()
    {
        // The branch from the HEAD file and its remote and merge from one config read: it was three processes.
        var local = HeadBranchFromFiles();
        if (local == null)
        {
            var head = Run("symbolic-ref", "--short", "-q", "HEAD");
            if (!head.Ok) return (null, null, null);
            local = head.StdOut.Trim();
        }
        else if (local.Length == 0) return (null, null, null);
        string? remote, merge;
        if (RemoteConfig() is { } cfg)
        {
            remote = cfg.Values.GetValueOrDefault($"branch.{local}.remote") is { Length: > 0 } rv ? rv : null;
            merge = cfg.Values.GetValueOrDefault($"branch.{local}.merge") is { Length: > 0 } mv ? mv : null;
        }
        else
        {
            remote = ConfigGet($"branch.{local}.remote");
            merge = ConfigGet($"branch.{local}.merge");
        }
        if (remote == null || remote == "." || merge == null) return (local, null, null);
        return (local, remote, merge.StartsWith("refs/heads/", StringComparison.Ordinal) ? merge[11..] : merge);
    }
}

/// <summary>
/// A git checkout as the store sees it. The clone keeps its own .git, so a store command run in the
/// folder would find the clone and not the store; it is told instead. GIT_DIR is a small folder,
/// .sg/checkouts/&lt;name&gt;, that holds a HEAD and an index of its own and names the store as its common
/// directory, the way a linked worktree's folder does; GIT_WORK_TREE is the clone. HEAD there is the
/// snapshot, which is what an SVN checkout's HEAD is too, so a shelf, a push and a restore read and
/// write the clone's files through the same store commands they use on an SVN checkout.
///
/// The clone's own line-ending settings go along, so the store hashes and writes its files the way the
/// clone's git would, and a file the store writes does not show as changed to the clone. The store's
/// fsmonitor does not: it would start a watcher over a folder that is not the store's to watch.
/// </summary>
public sealed class GitTunnel
{
    public string WorkTree { get; }
    public string GitDir { get; }
    readonly Lazy<IReadOnlyDictionary<string, string>> _env;

    public GitTunnel(SgRoot root, CheckoutConfig co)
    {
        WorkTree = Path.GetFullPath(co.Path).TrimEnd('\\', '/');
        GitDir = DirOf(root, co.Name);
        var exe = root.Config.GitExe;
        var log = root.Log;
        _env = new Lazy<IReadOnlyDictionary<string, string>>(() => Build(exe, log));
    }

    public IReadOnlyDictionary<string, string> Env => _env.Value;

    public static string DirOf(SgRoot root, string name) => Path.Combine(root.StorePath, "checkouts", name);

    IReadOnlyDictionary<string, string> Build(string exe, ILog log)
    {
        var clone = new GitRepo(exe, WorkTree, log);
        var config = new List<(string, string)>
        {
            ("core.autocrlf", clone.ConfigGet("core.autocrlf") ?? "false"),
            ("core.fsmonitor", "false"),
            ("core.safecrlf", "false"),
        };
        var eol = clone.ConfigGet("core.eol");
        if (eol != null) config.Add(("core.eol", eol));
        var env = new Dictionary<string, string>
        {
            ["GIT_DIR"] = GitDir,
            ["GIT_WORK_TREE"] = WorkTree,
            ["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < config.Count; i++)
        {
            env["GIT_CONFIG_KEY_" + i] = config[i].Item1;
            env["GIT_CONFIG_VALUE_" + i] = config[i].Item2;
        }
        return env;
    }
}

/// <summary>
/// A git clone behind a checkout. The clone stays the user's: its own .git, its own remotes, its own
/// identity, and whatever else they run in it. The store copies the branch's history out of it with a
/// fetch, and a snapshot is a commit of the server branch's tree, so a branch worktree builds on
/// exactly what the server has. Sending goes the other way: the change is carried into the clone,
/// committed there on top of the server branch, and pushed, which is the git shape of one SVN commit.
///
/// Submodules are its externals. Each is a repository of its own (<see cref="GitUnit"/>), its tree goes
/// into the snapshot where its parent pins it, its files are read and written in it, and a push commits
/// in it first and pins the new commit in its parent after.
/// </summary>
public sealed class GitCheckoutVcs : ICheckoutVcs
{
    /// <summary>The server branch, fetched out of the clone into the store. The snapshot's tree is this commit's.</summary>
    public const string UpstreamPrefix = "refs/sg/upstream/";

    /// <summary>A commit holding only the files a push is about to write, for the clone to fetch.</summary>
    public const string OutgoingPrefix = "refs/sg/outgoing/";

    /// <summary>Where a submodule's commit lands in the store on its way into a snapshot, for as long as that takes.</summary>
    const string SubmoduleRef = "refs/sg/submodule";

    /// <summary>The file in the clone's git folder that names the root, for finding it from inside the clone.</summary>
    public const string RootMarker = "sg-root";

    public CheckoutKind Kind => CheckoutKind.Git;
    public string ServerName => "Git";

    static GitRepo Repo(SgRoot root, CheckoutConfig co) => new(root.Config.GitExe, co.Path, root.Log);
    static string Remote(CheckoutConfig co) => co.Remote ?? "origin";
    static string Branch(CheckoutConfig co) => co.Branch is { Length: > 0 } b ? b : throw new SgException($"{co.Name} names no branch. Register it again.");
    static string Tracking(CheckoutConfig co) => TrackingOf(co, Branch(co));
    static string TrackingOf(CheckoutConfig co, string branch) => $"refs/remotes/{Remote(co)}/{branch}";
    public static string UpstreamRef(CheckoutConfig co) => UpstreamPrefix + co.Name;
    static string OutgoingRef(CheckoutConfig co) => OutgoingPrefix + co.Name;
    static List<GitUnit> Units(SgRoot root, CheckoutConfig co) => GitSubmodules.Units(root, co);
    static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();

    /// <summary>The root a clone was registered in, from the file sg left in its git folder. Null when there is none.</summary>
    public static string? ReadRootMarker(string gitDir)
    {
        try
        {
            var f = Path.Combine(gitDir, RootMarker);
            if (!File.Exists(f)) return null;
            var text = File.ReadAllText(f).Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- registering ----

    public CheckoutIdentity Describe(SgRoot root, string folder)
    {
        var repo = new GitRepo(root.Config.GitExe, folder, root.Log);
        var top = repo.Run("rev-parse", "--show-toplevel");
        if (!top.Ok) throw new SgException(folder + " is not a git clone: " + top.StdErr.Trim().Split('\n')[0]);
        var topPath = Path.GetFullPath(top.StdOut.Trim()).TrimEnd('\\', '/');
        if (!topPath.Equals(folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new SgException($"{folder} is inside the git clone at {topPath}. Register the clone's own folder.");
        var common = Path.GetFullPath(repo.Out("rev-parse", "--path-format=absolute", "--git-common-dir")).TrimEnd('\\', '/');
        if (common.Equals(root.StorePath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new SgException(folder + " is a worktree of this root's own store, not a clone of a server.");
        // The root's store and worktrees would be files of the clone, and store commands would be read as the clone's.
        var rootPath = root.RootPath.TrimEnd('\\', '/');
        if (rootPath.Equals(topPath, StringComparison.OrdinalIgnoreCase) || rootPath.StartsWith(topPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SgException($"the sg root {rootPath} is inside the clone {topPath}. Make the root a folder outside the clone, like its parent.");
        if (repo.Out("rev-parse", "--is-shallow-repository") == "true")
            throw new SgException($"{folder} is a shallow clone. sg reads the branch's history; fetch it whole first: git -C \"{folder}\" fetch --unshallow");
        var (local, remote, branch) = repo.Tracking();
        if (local == null)
            throw new SgException($"HEAD is detached in {folder}. Check out the branch that tracks the server, then register it.");
        if (remote == null || branch == null)
            throw new SgException($"branch {local} in {folder} tracks no remote branch. Set one with: git -C \"{folder}\" branch -u origin/{local}");
        var url = repo.Run("remote", "get-url", remote);
        if (!url.Ok) throw new SgException($"remote {remote} of {folder} has no URL: " + url.StdErr.Trim());
        var remoteUrl = url.StdOut.Trim().TrimEnd('/');
        return new CheckoutIdentity
        {
            Kind = CheckoutKind.Git,
            Url = GitLocation.Format(remoteUrl, branch),
            ReposRoot = remoteUrl,
            Remote = remote,
            Branch = branch,
        };
    }

    /// <summary>git clone, submodules and all, the way svn checkout brings its externals along.</summary>
    public void CheckoutUrl(SgRoot root, string url, string folder)
    {
        var (repoUrl, branch) = GitLocation.Parse(url);
        root.Log.Info($"git clone {repoUrl}" + (branch != null ? $" ({branch})" : "") + $" into {folder}");
        var args = new List<string> { "clone", "--quiet", "--recurse-submodules" };
        if (branch != null) { args.Add("--branch"); args.Add(branch); }
        args.Add("--");
        args.Add(repoUrl);
        args.Add(Path.GetFullPath(folder));
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
        new GitRepo(root.Config.GitExe, parent, root.Log).Run(args).EnsureOk();
    }

    public bool UrlExists(SgRoot root, string url)
    {
        var (repoUrl, branch) = GitLocation.Parse(url);
        var r = LsRemoteHeads(root, Path.GetTempPath(), repoUrl, branch);
        return r.Ok && (branch == null || r.StdOut.Trim().Length > 0);
    }

    public string NameFromUrl(string url)
    {
        var (repoUrl, branch) = GitLocation.Parse(url);
        return (branch ?? GitLocation.RepoName(repoUrl)).Replace('/', '-').Replace('\\', '-');
    }

    public void Attach(SgRoot root, CheckoutConfig co)
    {
        var repo = Repo(root, co);
        File.WriteAllText(Path.Combine(repo.GitDir, RootMarker), root.RootPath + "\n");
        var dir = GitTunnel.DirOf(root, co.Name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(dir, "HEAD"), root.Git.EnsureRootCommit() + "\n");
        root.Git.Changed();
        root.RefreshTunnels();
    }

    public void Detach(SgRoot root, CheckoutConfig co)
    {
        try
        {
            var marker = Path.Combine(Repo(root, co).GitDir, RootMarker);
            if (ReadRootMarker(Path.GetDirectoryName(marker)!) is { } r && r.Equals(root.RootPath, StringComparison.OrdinalIgnoreCase)) File.Delete(marker);
        }
        catch (Exception e) when (e is SgException or IOException or UnauthorizedAccessException) { /* the message says what happened */ }
        SgRoot.SweepTempDir(GitTunnel.DirOf(root, co.Name));
        root.Git.DeleteRef(UpstreamRef(co));
        root.RefreshTunnels();
    }

    public void Renamed(SgRoot root, CheckoutConfig co, string oldName)
    {
        var from = GitTunnel.DirOf(root, oldName);
        var to = GitTunnel.DirOf(root, co.Name);
        if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
        var up = root.Git.RefSha(UpstreamPrefix + oldName);
        if (up != null)
        {
            root.Git.UpdateRef(UpstreamRef(co), up);
            root.Git.DeleteRef(UpstreamPrefix + oldName);
        }
        root.RefreshTunnels();
    }

    // ---- sync ----

    /// <summary>
    /// Fetches the server branch, then moves the clone onto it the way svn update moves a working copy:
    /// a fast-forward, with local edits stashed around it when they are in the way, and a conflict where
    /// the stash will not go back cleanly. A clone that has commits of its own is replayed onto the
    /// server, and left where it was when that does not go cleanly. A clone on another branch keeps its
    /// files; the snapshot is of the server branch either way. Then every submodule, the way svn update
    /// brings the externals along.
    /// </summary>
    public UpstreamUpdate Update(SgRoot root, CheckoutConfig co)
    {
        var repo = Repo(root, co);
        var res = new UpstreamUpdate();
        Fetch(root, co);
        var upstream = repo.Rev(Tracking(co)) ?? throw new SgException($"{Remote(co)}/{Branch(co)} is not on the server any more.");
        res.Commit = upstream;
        res.Revision = repo.Height(upstream);

        var (local, remote, branch) = repo.Tracking();
        var head = repo.Rev("HEAD");
        if (local == null || remote != Remote(co) || branch != Branch(co))
            res.Warnings.Add($"the clone is on {(local ?? "a detached HEAD")}, not on the branch that tracks {Remote(co)}/{Branch(co)}, so its files were left alone. "
                             + "The snapshot is of the server branch all the same.");
        else if (repo.InProgress() is { } op)
            res.Warnings.Add($"a {op} is in progress in the clone, so its files were left alone. Finish it with git, then sync again.");
        else if (head == null || head == upstream) { }
        else if (repo.IsAncestor(head, upstream)) FastForward(repo, upstream, res);
        else if (repo.IsAncestor(upstream, head))
            res.Warnings.Add($"the clone has {repo.Count(upstream + ".." + head)} commit(s) the server does not. They stay; push them with git, or take them back out.");
        else
        {
            var r = repo.Run("-c", "submodule.recurse=false", "rebase", "--autostash", "--quiet", upstream);
            if (r.Ok) root.Log.Info("the clone's own commits were replayed onto the server's");
            else
            {
                repo.Run("rebase", "--abort");
                res.Warnings.Add("the clone's own commits do not replay onto the server's cleanly, so the clone was left where it was. Rebase it with git.");
            }
        }
        res.Conflicts = Math.Max(res.Conflicts, repo.Conflicted().Count);
        UpdateSubmodules(root, co, res);
        root.Log.Info($"git fetch {co.Name}: {Rev.Short(upstream)}" + (res.Conflicts > 0 ? $", {res.Conflicts} conflict(s) in the checkout" : ""));
        return res;
    }

    /// <summary>
    /// Every submodule brought to where its parent says: one not cloned yet is cloned, a pinned one is
    /// moved to the commit its parent pins now, a switched one is moved up its branch the way the clone
    /// is. One with work in the way is left where it is, with a warning, rather than forced.
    /// </summary>
    static void UpdateSubmodules(SgRoot root, CheckoutConfig co, UpstreamUpdate res)
    {
        var queue = new List<GitUnit> { GitUnit.Clone(root, co) };
        for (var i = 0; i < queue.Count; i++)
        {
            var parent = queue[i];
            if (!File.Exists(Path.Combine(parent.Repo.Path, ".gitmodules"))) continue;
            foreach (var e in GitSubmodules.Declared(a => parent.Repo.Run(a), parent.Repo.HeadCommit()))
            {
                var wc = parent.Full(e.Path);
                if (GitSubmodules.Skipped(co, wc)) continue;
                var dir = PathUtil.Join(co.Path, wc);
                if (!GitSubmodules.CheckedOut(dir))
                {
                    root.Log.Info($"cloning submodule {wc}");
                    var init = parent.Repo.Run("submodule", "update", "--init", "--", e.Path);
                    if (!init.Ok || !GitSubmodules.CheckedOut(dir))
                    {
                        res.Warnings.Add($"submodule {wc} could not be cloned: {FirstLine(init.StdErr + "\n" + init.StdOut)}");
                        continue;
                    }
                }
                var unit = GitUnit.Submodule(root, parent, e, wc, dir);
                UpdateSubmodule(unit, res);
                res.Conflicts += unit.Repo.Conflicted().Count;
                queue.Add(unit);
            }
        }
    }

    static void UpdateSubmodule(GitUnit unit, UpstreamUpdate res)
    {
        var repo = unit.Repo;
        if (repo.InProgress() is { } op)
        {
            res.Warnings.Add($"a {op} is in progress in {unit.Wc}, so it was left alone. Finish it with git, then sync again.");
            return;
        }
        var head = repo.Rev("HEAD");
        if (unit.Switched)
        {
            var (_, remote, branch) = unit.Tracking;
            res.KeptSwitched.Add(unit.Wc);
            var f = repo.Run("fetch", "--quiet", remote!, $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}");
            if (!f.Ok)
            {
                res.Warnings.Add($"git fetch {remote} {branch} failed in {unit.Wc}, so it stays where it is: {FirstLine(f.StdErr)}");
                return;
            }
            var tip = repo.Rev($"refs/remotes/{remote}/{branch}");
            if (tip == null || head == tip) return;
            if (head == null || repo.IsAncestor(head, tip)) FastForward(repo, tip, res);
            else if (repo.IsAncestor(tip, head))
                res.Warnings.Add($"{unit.Wc} has {repo.Count(tip + ".." + head)} commit(s) {remote}/{branch} does not. They stay; push them with git, or take them back out.");
            else
                res.Warnings.Add($"{unit.Wc} and {remote}/{branch} went separate ways, so {unit.Wc} was left where it was. Rebase it with git.");
            return;
        }
        if (unit.Tracking.Local is { } localBranch)
        {
            res.Warnings.Add($"{unit.Wc} is on branch {localBranch}, which tracks no remote branch, so it was left alone. The snapshot holds the commit its parent pins.");
            return;
        }
        var pin = unit.Pin;
        if (head == pin) return;
        if (repo.Rev(pin) == null)
        {
            repo.Run("fetch", "--quiet", unit.Remote);
            if (repo.Rev(pin) == null) repo.Run("fetch", "--quiet", unit.Remote, pin);
            if (repo.Rev(pin) == null)
            {
                res.Warnings.Add($"{unit.Wc}: {Rev.Short(pin)}, the commit its parent pins, is not on {unit.Url}.");
                return;
            }
        }
        if (head != null && repo.IsAncestor(head, pin)) FastForward(repo, pin, res);
        else
        {
            var r = repo.Run("-c", "submodule.recurse=false", "checkout", "--detach", "--quiet", pin);
            if (!r.Ok) res.Warnings.Add($"{unit.Wc} could not be moved to {Rev.Short(pin)}, the commit its parent pins: {FirstLine(r.StdErr)}");
        }
    }

    static void FastForward(GitRepo repo, string target, UpstreamUpdate res)
    {
        // A submodule stays where sg puts it, not where a submodule.recurse setting would take it along.
        var r = repo.Run("-c", "submodule.recurse=false", "merge", "--ff-only", "--quiet", target);
        if (r.Ok) return;
        // Local edits on files the server changed. svn update merges into them; the nearest git has is
        // putting them aside for the move and back after it, and a conflict when they will not go back.
        r = repo.Run("-c", "submodule.recurse=false", "merge", "--ff-only", "--autostash", "--quiet", target);
        res.Output = (r.StdOut + r.StdErr).Trim();
        if (!r.Ok) res.Warnings.Add($"{repo.Path} could not be moved to the server's commit: " + res.Output);
        else if (res.Output.Contains("resulted in conflicts", StringComparison.Ordinal)) res.Conflicts = Math.Max(1, res.Conflicts);
    }

    static void Fetch(SgRoot root, CheckoutConfig co) => FetchBranch(root, co, Branch(co));

    static void FetchBranch(SgRoot root, CheckoutConfig co, string branch)
    {
        var r = Repo(root, co).Run("fetch", "--quiet", Remote(co), $"+refs/heads/{branch}:{TrackingOf(co, branch)}");
        if (!r.Ok) throw new SgException($"git fetch {Remote(co)} {branch} failed in {co.Path}: " + r.StdErr.Trim());
    }

    /// <summary>
    /// The server branch's tree, as a commit of the store, with every submodule's own tree where its
    /// parent pins it. The branch's history is fetched out of the clone first - all of it the first
    /// time, only what is new after that - and each submodule's commit out of its own clone, so the
    /// snapshot's objects are the store's own and outlive anything done to the clones. Skipped paths
    /// are taken out.
    /// </summary>
    public SnapshotInfo BuildSnapshot(SgRoot root, CheckoutConfig co, string? parentSha, Func<SnapshotInfo, string>? extraMessage)
    {
        var git = root.Git;
        var repo = Repo(root, co);
        var info = new SnapshotInfo { Url = co.Url };
        var tracking = Tracking(co);
        var upstream = repo.Rev(tracking)
                       ?? throw new SgException($"{co.Name} has no {Remote(co)}/{Branch(co)} yet. Fetch it: git -C \"{co.Path}\" fetch {Remote(co)}");

        var upRef = UpstreamRef(co);
        if (git.RefSha(upRef) != upstream)
        {
            if (parentSha == null) root.Log.Info($"copying the history of {Branch(co)} into the store");
            git.Ok(null, "fetch", "--no-tags", "--no-write-fetch-head", "--quiet", co.Path, $"+{tracking}:{upRef}");
        }
        info.Commit = upstream;
        info.Revision = long.TryParse(git.Out(null, "rev-list", "--count", "--first-parent", upstream), out var h) ? h : 0;

        var tree = Graft(root, co, upstream, git.TreeOf(upstream), info);
        if (co.Skip.Count > 0) tree = WithoutSkipped(root, co, tree);

        var snapRef = root.SnapshotRef(co);
        if (parentSha != null && git.TreeOf(parentSha) == tree && SameServerState(SnapshotMeta.Parse(git.Body(parentSha)), info))
        {
            info.Sha = parentSha;
            info.Unchanged = true;
            git.UpdateRef(snapRef, parentSha);
            git.SetHeadDetached(co.Path, parentSha);
            return info;
        }

        var extra = extraMessage?.Invoke(info) ?? "";
        var sha = git.CommitTree(tree, parentSha ?? git.EnsureRootCommit(), Message(co, info, extra));
        git.UpdateRef(snapRef, sha);
        git.SetHeadDetached(co.Path, sha);
        info.Sha = sha;
        return info;
    }

    /// <summary>The same commits, and the submodules at the same places: the snapshot need not be taken again.</summary>
    static bool SameServerState(SnapshotMeta prev, SnapshotInfo info) =>
        prev.Commit == info.Commit
        && prev.ExternalCommits.Count == info.Externals.Count
        && info.Externals.All(e => prev.ExternalCommits.TryGetValue(e.Rel, out var c) && c == e.Commit
                                   && prev.ExternalUrls.TryGetValue(e.Rel, out var u) && u == e.Url);

    /// <summary>
    /// The tree with every submodule's own tree where the commit pins it, at any depth: a switched one's
    /// branch, a pinned one's pinned commit. Each commit is fetched out of the submodule's clone into the
    /// store first. A submodule that cannot go in is taken out, with a warning that says why, rather
    /// than left as a pin nothing in a branch worktree could open.
    /// </summary>
    static string Graft(SgRoot root, CheckoutConfig co, string commit, string tree, SnapshotInfo info)
    {
        var git = root.Git;
        var queue = new List<(GitUnit Unit, string Commit)> { (GitUnit.Clone(root, co), commit) };
        for (var i = 0; i < queue.Count; i++)
        {
            var (parent, parentCommit) = queue[i];
            foreach (var e in GitSubmodules.Declared(a => git.Run(null, a), parentCommit))
            {
                var wc = parent.Full(e.Path);
                if (GitSubmodules.Skipped(co, wc)) continue;   // taken out with the rest of the skipped paths
                var dir = PathUtil.Join(co.Path, wc);
                GitUnit? unit = null;
                string used = e.Pin, why = "";
                try
                {
                    if (!GitSubmodules.CheckedOut(dir)) why = "it is not cloned. Sync clones it";
                    else
                    {
                        unit = GitUnit.Submodule(root, parent, e, wc, dir);
                        if (unit.Repo.Out("rev-parse", "--is-shallow-repository") == "true")
                            why = $"it is a shallow clone. Fetch it whole: git -C \"{dir}\" fetch --unshallow";
                        else if (unit.Switched && unit.Repo.Rev(unit.TrackingRef) == null)
                            why = $"{unit.Remote}/{unit.Branch} is not in it. Sync fetches it";
                        else
                        {
                            if (unit.Switched) used = unit.Repo.Rev(unit.TrackingRef)!;
                            if (unit.Repo.Rev(used) == null) why = $"it does not have {Rev.Short(used)}, the commit its parent pins. Sync fetches it";
                            else if (git.Run(null, "cat-file", "-e", used + "^{commit}").ExitCode != 0)
                            {
                                var f = git.Run(null, "fetch", "--no-tags", "--no-write-fetch-head", "--quiet", dir, $"+{used}:{SubmoduleRef}");
                                git.DeleteRef(SubmoduleRef);
                                if (!f.Ok) why = "its commit could not be copied into the store: " + FirstLine(f.StdErr);
                            }
                        }
                    }
                }
                catch (SgException ex) { why = ex.Message; }

                if (why.Length > 0 || unit == null)
                {
                    info.Warnings.Add($"submodule {wc} is left out of the snapshot: {why}.");
                    tree = git.ReplaceEntry(tree, wc, null, null);
                    continue;
                }
                tree = git.ReplaceEntry(tree, wc, "040000", git.TreeOf(used));
                var height = long.TryParse(git.Out(null, "rev-list", "--count", "--first-parent", used), out var n) ? n : 0;
                info.Externals.Add(new ExternalInfo(wc, height, unit.Location, used));
                queue.Add((unit, used));
            }
        }
        return tree;
    }

    /// <summary>The tree with every skipped path taken out.</summary>
    static string WithoutSkipped(SgRoot root, CheckoutConfig co, string tree)
    {
        var git = root.Git;
        var gone = new List<(string, string, string)>();
        foreach (var chunk in co.Skip.Chunk(100))
            foreach (var e in git.LsTree(tree, chunk.Select(PathUtil.Rel), recursive: true))
                gone.Add(("0", new string('0', 40), e.Path));
        return git.EditTree(tree, gone);
    }

    static string Message(CheckoutConfig co, SnapshotInfo info, string extra)
    {
        var sb = new StringBuilder();
        sb.Append(co.Name).Append(' ').Append(Rev.Short(info.Commit)).Append('\n');
        if (extra.Trim().Length > 0) sb.Append('\n').Append(extra.Trim()).Append('\n');
        sb.Append('\n');
        sb.Append(SnapshotMeta.GitRev).Append(info.Revision).Append('\n');
        sb.Append(SnapshotMeta.GitUrl).Append(info.Url).Append('\n');
        sb.Append(SnapshotMeta.GitCommit).Append(info.Commit).Append('\n');
        foreach (var e in info.Externals)
            sb.Append(SnapshotMeta.GitSubmodule).Append(e.Revision).Append(' ').Append(e.Commit).Append(' ').Append(e.Url.Replace(" ", "%20")).Append(' ').Append(e.Rel).Append('\n');
        return sb.ToString();
    }

    /// <summary>What came in since the last snapshot: the branch's new commits, then each submodule's.</summary>
    public string LogsSince(SgRoot root, CheckoutConfig co, SnapshotMeta? prev, SnapshotInfo info)
    {
        if (prev == null) return "";
        var sb = new StringBuilder();
        if (prev.Commit.Length > 0 && prev.Commit != info.Commit)
            LogBetween(Repo(root, co), "root", Branch(co), prev.Commit, info.Commit, sb);
        foreach (var e in info.Externals)
        {
            if (!prev.ExternalCommits.TryGetValue(e.Rel, out var was) || was == e.Commit) continue;
            LogBetween(new GitRepo(root.Config.GitExe, PathUtil.Join(co.Path, e.Rel), root.Log), e.Rel, e.Rel, was, e.Commit, sb);
        }
        return sb.ToString();
    }

    static void LogBetween(GitRepo repo, string label, string what, string from, string to, StringBuilder sb)
    {
        if (!repo.IsAncestor(from, to))
        {
            sb.Append($"== {label}: moved from {Rev.Short(from)} to {Rev.Short(to)}, which is not further along the history of {what}\n");
            return;
        }
        var r = repo.Run("log", "--first-parent", "-n", "30", "--format=%H%x1f%an%x1f%s", from + ".." + to);
        if (!r.Ok) return;
        sb.Append("== ").Append(label).Append(' ').Append(Rev.Short(from)).Append("..").Append(Rev.Short(to)).Append('\n');
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split('\x1f');
            if (p.Length < 3) continue;
            sb.Append(Rev.Short(p[0])).Append(' ').Append(p[1]).Append(": ").Append(p[2].Trim()).Append('\n');
        }
    }

    /// <summary>
    /// ls-remote says where the branch is on the server without fetching anything. Only when it moved is
    /// it fetched, to count what came in and to know its height. A switched submodule is asked the same
    /// about its branch; a pinned one moves only when the clone's branch pins it elsewhere.
    /// </summary>
    public RemoteCheckResult RemoteCheck(SgRoot root, CheckoutConfig co, SnapshotMeta snapshot, bool countCommits)
    {
        var res = new RemoteCheckResult { Checkout = co.Name };
        foreach (var unit in Units(root, co))
        {
            if (unit.Pinned) continue;
            var isRoot = unit.Wc.Length == 0;
            var snapRev = isRoot ? snapshot.Revision : snapshot.Externals.GetValueOrDefault(unit.Wc);
            var snapCommit = isRoot ? snapshot.Commit : snapshot.ExternalCommits.GetValueOrDefault(unit.Wc, "");
            var entry = new RemoteEntry { Rel = unit.Wc, Url = unit.Location, Snapshot = snapRev, Server = snapRev };
            var r = LsRemoteHeads(root, unit.Repo.Path, unit.Remote, unit.Branch);
            if (!r.Ok)
            {
                if (isRoot) throw new SgException($"git ls-remote {unit.Remote} failed in {co.Path}: " + r.StdErr.Trim());
                root.Log.Warn($"git ls-remote {unit.Remote} failed in {unit.Wc}: " + FirstLine(r.StdErr));
                continue;
            }
            var sha = r.StdOut.Split('\t', '\n')[0].Trim();
            if (sha.Length > 0 && !sha.Equals(snapCommit, StringComparison.OrdinalIgnoreCase))
            {
                unit.Fetch();
                var now = unit.Repo.Rev(unit.TrackingRef) ?? sha;
                entry.Server = Math.Max(unit.Repo.Height(now), snapRev + 1);
                if (countCommits)
                    entry.Commits = Math.Max(1, snapCommit.Length > 0 && unit.Repo.Rev(snapCommit) != null
                        ? unit.Repo.Count($"{snapCommit}..{now}")
                        : 1);
            }
            res.Entries.Add(entry);
        }
        return res;
    }

    static ProcResult LsRemoteHeads(SgRoot root, string cwd, string remote, string? branch)
    {
        var args = new List<string> { "ls-remote", "--heads", remote };
        if (branch != null) args.Add("refs/heads/" + branch);
        return new GitRepo(root.Config.GitExe, cwd, root.Log).Run(args);
    }

    /// <summary>A git checkout's ignore rules are its .gitignore files, and those are in its tree already.</summary>
    public List<string> IgnoreLines(SgRoot root, CheckoutConfig co) => new();

    // ---- local changes ----

    /// <summary>
    /// git status, in svn's words, in the clone and in every submodule, each change with the repository
    /// it belongs to. A staged new file is added, a staged removal deleted, a file gone from disk without
    /// git being told is missing, an untracked one unversioned, one git cannot merge is conflicted. A
    /// rename is its two halves: the new path added, the old one deleted. Untracked files are listed one
    /// by one, so a folder of them never brings along what .gitignore keeps out of it. A submodule at
    /// another commit than its parent pins is not a change of the parent's: that is sync's to settle.
    /// </summary>
    public List<CheckoutChange> Changes(SgRoot root, CheckoutConfig co) => ChangesIn(Units(root, co));

    static List<CheckoutChange> ChangesIn(List<GitUnit> units) =>
        Fan.Map(units, StatusOf).SelectMany(x => x).OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();

    static List<CheckoutChange> StatusOf(GitUnit unit)
    {
        var r = unit.Repo.Run("status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--ignore-submodules=all");
        r.EnsureOk();
        var res = new List<CheckoutChange>();
        var parts = r.StdOut.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var e = parts[i];
            if (e.Length < 4) continue;
            char x = e[0], y = e[1];
            var path = PathUtil.Rel(e[3..]);
            if (x is 'R' or 'C') i++;   // the old path follows; --no-renames keeps this from happening
            var item = (x, y) switch
            {
                ('?', '?') => "unversioned",
                ('!', '!') => "ignored",
                _ when x == 'U' || y == 'U' || (x, y) is ('A', 'A') or ('D', 'D') => "conflicted",
                ('A', 'D') => "missing",
                ('A', _) => "added",
                ('D', _) => "deleted",
                (_, 'D') => "missing",
                _ => "modified",
            };
            if (item == "ignored") continue;
            res.Add(new CheckoutChange { Path = unit.Full(path), Item = item, Props = "none", Wc = unit.Wc });
        }
        return res;
    }

    public int LocalEditCount(SgRoot root, CheckoutConfig co) => Changes(root, co).Count;

    public CheckoutScan Scan(SgRoot root, CheckoutConfig co)
    {
        var units = Units(root, co);
        return new(ChangesIn(units).Where(c => c.Versioned).Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase),
            units.Skip(1).Select(u => u.Wc).ToList());
    }

    public HashSet<string> LocalEditsOn(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths)
    {
        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Scan(root, co).LocalEdits.Where(wanted.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool HasConflicts(SgRoot root, CheckoutConfig co) => Units(root, co).Any(u => u.Repo.Conflicted().Count > 0);

    /// <summary>
    /// What would make a commit from the clone send more, or other, than it was asked to: HEAD off the
    /// branch that tracks the server, a replay half done, or commits of the clone's own that the server
    /// does not have yet, which a push of the branch would carry along unasked. The same of every
    /// submodule, where a pinned one has to be at the commit its parent pins.
    /// </summary>
    public List<string> WriteBlockers(SgRoot root, CheckoutConfig co)
    {
        var res = new List<string>();
        foreach (var unit in Units(root, co))
        {
            var repo = unit.Repo;
            var (local, remote, branch) = unit.Tracking;
            var head = repo.Rev("HEAD");
            if (unit.Wc.Length == 0)
            {
                if (local == null)
                    res.Add($"HEAD is detached in {co.Path}. Check out the branch that tracks {Remote(co)}/{Branch(co)}.");
                else if (remote != Remote(co) || branch != Branch(co))
                    res.Add($"the clone is on {local}, which does not track {Remote(co)}/{Branch(co)}. Check that branch out again.");
            }
            else if (unit.Pinned && local != null)
                res.Add($"{unit.Wc} is on branch {local}, which tracks no remote branch. Check out the commit its parent pins, or a branch that tracks one.");
            else if (unit.Pinned && head != unit.Pin)
                res.Add($"{unit.Wc} is at {Rev.Short(head ?? "")}, not at {Rev.Short(unit.Pin)}, the commit its parent pins. Sync moves it back.");
            if (repo.InProgress() is { } op) res.Add($"a {op} is in progress in {(unit.Wc.Length == 0 ? "the clone" : unit.Wc)}. Finish or abort it with git first.");
            if (unit.Pinned || local == null || head == null) continue;
            var tracking = repo.Rev($"refs/remotes/{remote}/{branch}");
            if (tracking == null || head == tracking) continue;
            var ahead = repo.Count(tracking + ".." + head);
            if (ahead > 0)
                res.Add($"{(unit.Wc.Length == 0 ? "the clone" : unit.Wc)} has {ahead} commit(s) {remote}/{branch} does not. Push them or take them back out with git first, so a commit from sg sends only what it was asked to.");
        }
        return res;
    }

    public void Add(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        foreach (var (unit, rels) in GitSubmodules.ByUnit(Units(root, co), paths))
            unit.Repo.WithPaths(["add", "-f"], rels).EnsureOk();
    }

    public void Remove(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        foreach (var (unit, rels) in GitSubmodules.ByUnit(Units(root, co), paths))
            unit.Repo.WithPaths(["rm", "-r", "-f", "-q", "--ignore-unmatch"], rels).EnsureOk();
        foreach (var p in paths)
        {
            var abs = PathUtil.Join(co.Path, p);
            if (File.Exists(abs)) File.Delete(abs);
        }
    }

    /// <summary>
    /// Back to what the repository's HEAD holds, which is what the server has. A file the server does
    /// not have is only unstaged and stays on disk, the way svn revert leaves an added file behind.
    /// </summary>
    public string? Revert(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var said = new List<string>();
        foreach (var (unit, rels) in GitSubmodules.ByUnit(Units(root, co), paths))
        {
            var repo = unit.Repo;
            var unstage = repo.WithPaths(["reset", "-q", "HEAD"], rels);
            if (!unstage.Ok) said.Add(unstage.StdErr.Trim());
            var inHead = InTree(repo, "HEAD", rels);
            if (inHead.Count > 0)
            {
                var restore = repo.WithPaths(["checkout", "HEAD"], inHead);
                if (!restore.Ok) said.Add(restore.StdErr.Trim());
            }
        }
        return said.Count == 0 ? null : string.Join("\n", said);
    }

    /// <summary>Which of the paths a commit holds, as files or as folders with files under them.</summary>
    static List<string> InTree(GitRepo repo, string commit, IReadOnlyCollection<string> paths)
    {
        var files = ListUnder(repo, ["ls-tree", "-r", "-z", "--name-only", commit], paths);
        return paths.Where(p => p == "." ? files.Count > 0 : files.Any(f => PathUtil.IsUnder(f, p))).ToList();
    }

    /// <summary>
    /// The paths a listing command names under these paths. A short list goes as pathspecs; a long one
    /// lists everything and filters here, since ls-tree and ls-files take no pathspec file.
    /// </summary>
    static List<string> ListUnder(GitRepo repo, string[] args, IReadOnlyCollection<string> paths)
    {
        var small = paths.Count <= 100;
        var a = args.ToList();
        if (small) { a.Add("--"); a.AddRange(paths.Select(p => ":(literal)" + p)); }
        var r = repo.Run(a);
        if (!r.Ok) return new();
        var all = r.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\n', '\r')).ToList();
        return small ? all : all.Where(f => paths.Any(p => PathUtil.IsUnder(TreePath(f), p))).ToList();
    }

    /// <summary>The path of an ls-files -s or ls-tree line, which has its metadata before a tab.</summary>
    static string TreePath(string line)
    {
        var tab = line.IndexOf('\t');
        return tab < 0 ? line : line[(tab + 1)..];
    }

    /// <summary>
    /// git diff against HEAD, in the repository the path belongs to, with paths written from the
    /// checkout root. A folder with submodules inside it takes in their diffs too.
    /// </summary>
    public string DiffLocal(SgRoot root, CheckoutConfig co, string path)
    {
        var units = Units(root, co);
        var at = path.Length == 0 || path == "." ? "" : PathUtil.Rel(path);
        var owner = GitSubmodules.Innermost(units, at);
        var sb = new StringBuilder();
        foreach (var unit in units.Where(u => u == owner || (u.Wc.Length > owner.Wc.Length && PathUtil.IsUnder(u.Wc, at))))
        {
            var prefix = unit.Wc.Length == 0 ? "" : unit.Wc + "/";
            var args = new List<string> { "diff", "--no-color", "--ignore-submodules=all", "--src-prefix=a/" + prefix, "--dst-prefix=b/" + prefix, "HEAD" };
            var rel = unit == owner ? unit.RelOf(at) : "";
            if (rel.Length > 0) { args.Add("--"); args.Add(":(literal)" + rel); }
            var r = unit.Repo.Run(args);
            sb.Append(r.Ok ? r.StdOut : r.StdErr);
        }
        return sb.ToString();
    }

    /// <summary>The server's version, written the way the clone would write it to disk, so a diff against the file lines up.</summary>
    public string BaseText(SgRoot root, CheckoutConfig co, string path)
    {
        var unit = GitSubmodules.Innermost(Units(root, co), path);
        var r = unit.Repo.Run("cat-file", "--filters", "HEAD:" + unit.RelOf(path));
        return r.Ok ? r.StdOut : "";
    }

    /// <summary>A line per name in the folder's own .gitignore, anchored there: what svn:ignore on the folder says.</summary>
    public void Ignore(SgRoot root, CheckoutConfig co, string folder, IEnumerable<string> names)
    {
        var file = PathUtil.Join(PathUtil.Join(co.Path, PathUtil.Rel(folder)), ".gitignore");
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var have = text.Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        var add = names.Where(n => n.Length > 0).Select(n => "/" + n).Where(l => !have.Contains(l)).Distinct().ToList();
        if (add.Count == 0) return;
        var sb = new StringBuilder(text);
        if (text.Length > 0 && !text.EndsWith('\n')) sb.Append(nl);
        foreach (var l in add) sb.Append(l).Append(nl);
        File.WriteAllText(file, sb.ToString());
    }

    /// <summary>
    /// The chosen changes staged in their repository, then one commit of them on its server branch,
    /// pushed, with the new commits of the submodules it pins.
    /// </summary>
    public CommitId CommitChanges(SgRoot root, CheckoutConfig co, string wc, IReadOnlyList<CheckoutChange> changes, string message,
        IReadOnlyDictionary<string, string> pins)
    {
        var unit = GitSubmodules.Of(Units(root, co), wc);
        var paths = changes.Select(c => unit.RelOf(c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count > 0) unit.Repo.WithPaths(["add", "-A"], paths).EnsureOk();
        return CommitIn(root, unit, paths, PinsIn(unit, pins), message);
    }

    /// <summary>Submodule pins keyed by their path from the checkout root, as paths in the repository that pins them.</summary>
    static Dictionary<string, string> PinsIn(GitUnit unit, IReadOnlyDictionary<string, string> pins) =>
        pins.ToDictionary(kv => unit.RelOf(kv.Key), kv => kv.Value, StringComparer.Ordinal);

    /// <summary>
    /// The working copies to commit, submodules before what pins them. A pinned submodule's new commit
    /// is only half a change until its parent pins it, so the parent commits too, and so on up while
    /// the parent is itself pinned. A switched one is pinned by nobody: its parent is left alone.
    /// </summary>
    public List<(string Wc, string? PinnedIn)> CommitOrder(SgRoot root, CheckoutConfig co, IReadOnlyList<string> wcs)
    {
        if (wcs.All(w => w.Length == 0)) return wcs.Select(w => (w, (string?)null)).ToList();
        var units = Units(root, co);
        var want = new Dictionary<string, (string? PinnedIn, int Depth)>(StringComparer.OrdinalIgnoreCase);
        int Depth(GitUnit u) => u.Parent == null ? 0 : Depth(u.Parent) + 1;
        foreach (var wc in wcs)
        {
            var u = GitSubmodules.Of(units, wc);
            if (!want.ContainsKey(wc)) want[wc] = (null, Depth(u));
            var key = wc;
            while (u.Parent != null && u.Pinned && u.Wc.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                want[key] = (u.Parent.Wc, Depth(u));
                key = u.Parent.Wc;
                if (!want.ContainsKey(key)) want[key] = (null, Depth(u.Parent));
                u = u.Parent;
            }
        }
        return want.OrderByDescending(kv => kv.Value.Depth).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value.PinnedIn)).ToList();
    }

    /// <summary>
    /// One commit on the repository's server branch holding exactly what its index holds for these
    /// paths, and nothing else of it: parent the server's tip, tree the server's tree with these paths
    /// as staged and the submodules at their new pins. Other staged or unstaged edits stay where they
    /// are, the way svn commits only its targets. A push the server turns down because someone else got
    /// there first is made again on top of theirs, unless they touched the same files: that is svn's
    /// "out of date", and it stops. A pinned submodule's files are those of the commit its parent pins,
    /// which need not be its branch's newest, so a file changed on the branch since then is out of date
    /// from the start.
    /// </summary>
    static CommitId CommitIn(SgRoot root, GitUnit unit, IReadOnlyCollection<string> targets, IReadOnlyDictionary<string, string> pins, string message)
    {
        var repo = unit.Repo;
        var tracking = unit.TrackingRef;
        var at = unit.Remote + "/" + unit.Branch;
        if (unit.Pinned || repo.Rev(tracking) == null) unit.Fetch();
        var basis = unit.Pinned ? repo.Rev("HEAD") : null;
        for (var attempt = 0; ; attempt++)
        {
            var parent = repo.Rev(tracking) ?? throw new SgException($"{at} is not in {unit.Label}. Sync first.");
            var tree = TreeWith(root, repo, parent, targets, pins);
            if (tree == repo.TreeOf(parent))
            {
                // Pins the branch has already, or a submodule change an earlier push sent without its
                // parent's pin: what is wanted is on the server, and that commit is the one to pin.
                if (unit.Pinned || targets.Count == 0) return new CommitId(repo.Height(parent), parent);
                throw new SgException("nothing to commit: those paths hold what the server has already");
            }
            if (basis != null && basis != parent)
            {
                var moved = ListUnder(repo, ["diff", "--name-only", "-z", basis, parent], targets);
                if (moved.Count > 0)
                    throw new SgException($"out of date: {Few(moved.Select(unit.Full).ToList())} changed on {at} after {Rev.Short(basis)}, the commit {unit.Wc} is pinned at. "
                                          + $"Switch {unit.Wc} to {unit.Branch} in Edit checkout, sync, then commit again.");
            }
            var commit = repo.CommitTree(tree, [parent], message.TrimEnd() + "\n");
            var push = repo.Run("push", "--porcelain", unit.Remote, $"{commit}:refs/heads/{unit.Branch}");
            if (push.Ok)
            {
                repo.Run("update-ref", "-m", "sg: pushed", tracking, commit);
                if (MoveHead(root, unit, parent, commit))
                    foreach (var (rel, pin) in pins) repo.Run("update-index", "--cacheinfo", $"160000,{pin},{rel}");
                return new CommitId(repo.Height(commit), commit);
            }
            var said = (push.StdErr + "\n" + push.StdOut).Trim();
            var behind = said.Contains("[rejected]", StringComparison.Ordinal) || said.Contains("non-fast-forward", StringComparison.Ordinal)
                         || said.Contains("fetch first", StringComparison.Ordinal);
            if (!behind || attempt >= 2) throw new SgException($"git push to {at} failed: " + said);

            unit.Fetch();
            var now = repo.Rev(tracking)!;
            var touched = ListUnder(repo, ["diff", "--name-only", "-z", parent, now], targets.Concat(pins.Keys).ToList());
            if (touched.Count > 0)
                throw new SgException($"out of date: {Few(touched.Select(unit.Full).ToList())} changed on the server since the last sync. Sync, then commit again.");
            root.Log.Info($"{at} moved on; committing on top of it again");
        }
    }

    static string Few(List<string> paths) =>
        string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? $" and {paths.Count - 5} more" : "");

    /// <summary>
    /// The parent's tree with the index's version of every path under the targets, and every pin as a
    /// submodule entry at its new commit, built in an index of its own.
    /// </summary>
    static string TreeWith(SgRoot root, GitRepo repo, string parent, IReadOnlyCollection<string> targets, IReadOnlyDictionary<string, string> pins)
    {
        var idx = root.NewTempFile(".index");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = idx };
        try
        {
            repo.Run(["read-tree", parent], null, env).EnsureOk();
            var sb = new StringBuilder();
            if (targets.Count > 0)
            {
                var staged = ListUnder(repo, ["ls-files", "-s", "-z"], targets);
                var inIndex = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in staged)
                {
                    // "<mode> <sha> <stage>\t<path>"
                    var tab = line.IndexOf('\t');
                    var meta = line[..tab].Split(' ');
                    var path = line[(tab + 1)..];
                    if (meta.Length < 3 || meta[2] != "0") throw new SgException(path + " is in conflict in the clone. Settle it first.");
                    if (meta[0] == "160000") continue;   // a submodule's pin moves only with a commit of it
                    inIndex.Add(path);
                    sb.Append(meta[0]).Append(' ').Append(meta[1]).Append('\t').Append(path).Append('\0');
                }
                foreach (var line in ListUnder(repo, ["ls-tree", "-r", "-z", parent], targets))
                {
                    var path = TreePath(line);
                    if (!line.StartsWith("160000 ", StringComparison.Ordinal) && !inIndex.Contains(path))
                        sb.Append("0 ").Append(new string('0', 40)).Append('\t').Append(path).Append('\0');
                }
            }
            foreach (var (rel, pin) in pins) sb.Append("160000 ").Append(pin).Append('\t').Append(rel).Append('\0');
            if (sb.Length > 0) repo.Run(["update-index", "-z", "--index-info"], Encoding.UTF8.GetBytes(sb.ToString()), env).EnsureOk();
            return repo.Run(["write-tree"], null, env).EnsureOk().StdOut.Trim();
        }
        finally
        {
            if (File.Exists(idx)) File.Delete(idx);
        }
    }

    /// <summary>
    /// Puts the repository on the commit it just pushed, and says whether it got there. When HEAD was
    /// the commit's parent, it simply moves: the index already holds the committed files, so they read
    /// as unchanged and every other edit stays an edit. Otherwise it takes the whole way through a
    /// fast-forward: the server moved first, or a pinned submodule's pin was behind its branch. A clone
    /// off its branch, or a submodule off the state the commit was made for, is left alone.
    /// </summary>
    static bool MoveHead(SgRoot root, GitUnit unit, string parent, string commit)
    {
        var repo = unit.Repo;
        var (local, remote, branch) = repo.Tracking();
        if (unit.Pinned ? local != null : local == null || remote != unit.Remote || branch != unit.Branch) return false;
        var head = repo.Rev("HEAD");
        if (head == parent)
        {
            repo.Ok("update-ref", "-m", $"sg: commit to {unit.Remote}/{unit.Branch}", "HEAD", commit, parent);
            return true;
        }
        var res = new UpstreamUpdate();
        if (head != null && repo.IsAncestor(head, commit)) FastForward(repo, commit, res);
        foreach (var w in res.Warnings) root.Log.Warn(w);
        return repo.Rev("HEAD") == commit;
    }

    // ---- push ----

    public void FillRepositories(SgRoot root, CheckoutConfig co, List<PushGroup> groups)
    {
        var units = Units(root, co);
        foreach (var g in groups) g.ReposRoot = GitSubmodules.Of(units, g.Wc).Url;
    }

    /// <summary>
    /// Carries the branch's files into the repository they belong to and stages them: deletions
    /// removed, the new content written. The bytes come across as a commit of the store's that holds
    /// nothing but those files, which the repository fetches: its git then writes them the way it
    /// writes any checkout, and stages exactly the blobs the branch has.
    /// </summary>
    public void WriteInto(SgRoot root, CheckoutConfig co, PushGroup g, string tip)
    {
        var unit = GitSubmodules.Of(Units(root, co), g.Wc);
        var repo = unit.Repo;
        var renames = g.Entries.Where(e => e.Status == 'R' && e.OldPath != null).ToList();
        var adds = g.Entries.Where(e => e.Status == 'A').Select(e => e.Path).ToList();
        var mods = g.Entries.Where(e => e.Status is 'M' or 'T').Select(e => e.Path).ToList();
        var dels = g.Entries.Where(e => e.Status == 'D').Select(e => e.Path).ToList();
        var gone = dels.Concat(renames.Select(r => r.OldPath!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var write = adds.Concat(mods).Concat(renames.Select(r => r.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        g.Targets = new List<string>();
        if (gone.Count > 0)
        {
            var rels = gone.Select(unit.RelOf).ToList();
            repo.WithPaths(["rm", "-f", "-q", "--ignore-unmatch"], rels).EnsureOk();
            g.Targets.AddRange(rels);
        }
        if (write.Count > 0)
        {
            var carrier = Carrier(root, co, unit, tip, write);
            repo.Run("fetch", "--no-tags", "--quiet", root.StorePath, OutgoingRef(co)).EnsureOk();
            var rels = write.Select(unit.RelOf).ToList();
            repo.WithPaths(["checkout", carrier], rels).EnsureOk();
            g.Targets.AddRange(rels);
        }
    }

    /// <summary>A parentless commit of the store holding the tip's version of these files and nothing else, at their paths in the repository.</summary>
    static string Carrier(SgRoot root, CheckoutConfig co, GitUnit unit, string tip, IReadOnlyList<string> paths)
    {
        var git = root.Git;
        var entries = new List<(string, string, string)>();
        foreach (var chunk in paths.Chunk(100))
            foreach (var e in git.LsTree(tip, chunk).Where(e => e.Type == "blob"))
                entries.Add((e.Mode, e.Sha, unit.RelOf(e.Path)));
        var sha = git.CommitTree(git.EditTree(null, entries), null, "sg: files for " + co.Name + "\n");
        git.UpdateRef(OutgoingRef(co), sha);
        return sha;
    }

    public CommitId CommitWritten(SgRoot root, CheckoutConfig co, PushGroup g, string message)
    {
        var unit = GitSubmodules.Of(Units(root, co), g.Wc);
        return CommitIn(root, unit, g.Targets, PinsIn(unit, g.Pins), message);
    }

    /// <summary>
    /// Back to the repository's HEAD, which is where this step started: a step that went through moved
    /// HEAD with it, so an earlier batch that reached the server stays. restoreFrom is the store's name
    /// for the same content, and is not needed here.
    /// </summary>
    public void Rollback(SgRoot root, CheckoutConfig co, PushGroup g, string restoreFrom, List<string> warnings)
    {
        var label = g.Wc.Length == 0 ? "root" : g.Wc;
        try
        {
            var unit = GitSubmodules.Of(Units(root, co), g.Wc);
            var repo = unit.Repo;
            var all = g.Targets
                .Concat(g.Entries.Select(e => unit.RelOf(e.Path)))
                .Concat(g.Entries.Where(e => e.OldPath != null).Select(e => unit.RelOf(e.OldPath!)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0) return;
            repo.WithPaths(["reset", "-q", "HEAD"], all);
            var inHead = InTree(repo, "HEAD", all);
            if (inHead.Count > 0) repo.WithPaths(["checkout", "HEAD"], inHead).EnsureOk();
            foreach (var p in all.Except(inHead, StringComparer.OrdinalIgnoreCase))
            {
                var abs = PathUtil.Join(repo.Path, p);
                if (File.Exists(abs)) File.Delete(abs);
            }
        }
        catch (Exception ex) when (ex is SgException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"rollback of {label} hit a problem: {ex.Message}. Check 'git status' in {PathUtil.Join(co.Path, g.Wc)}.");
        }
    }

    // ---- history ----

    /// <summary>The clone and every submodule, each with where it is and where the snapshot has it.</summary>
    public List<HistorySource> HistorySources(SgRoot root, CheckoutConfig co)
    {
        var snapSha = root.Git.RefSha(root.SnapshotRef(co));
        var meta = snapSha != null ? SnapshotMeta.Parse(root.Git.Body(snapSha)) : null;
        return Units(root, co).Select(u =>
        {
            var head = u.Repo.Rev("HEAD") ?? "";
            long? snap = meta == null ? null : u.Wc.Length == 0 ? meta.Revision : meta.Externals.TryGetValue(u.Wc, out var r) ? r : null;
            return new HistorySource(u.Wc, u.Location, u.Url, head.Length > 0 ? u.Repo.Height(head) : 0, head, snap);
        }).ToList();
    }

    /// <summary>
    /// The server branch's first-parent history, newest first, fetched fresh the way svn log reads the
    /// server. A pinned submodule's is the branch its commits go to, which is where its pins come from.
    /// </summary>
    public List<LogRevision> Log(SgRoot root, CheckoutConfig co, HistorySource source, int limit)
    {
        var unit = GitSubmodules.Of(Units(root, co), source.Wc);
        unit.Fetch();
        var repo = unit.Repo;
        var tip = repo.Rev(unit.TrackingRef) ?? throw new SgException($"{unit.Remote}/{unit.Branch} is not in {unit.Label}.");
        var list = repo.Log(["--first-parent", "-n", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), tip]);
        var height = repo.Height(tip);
        for (var i = 0; i < list.Count; i++) list[i].Revision = height - i;
        return list;
    }

    public string RevisionDiff(SgRoot root, CheckoutConfig co, HistorySource source, LogRevision rev, string? folder) =>
        GitSubmodules.Of(Units(root, co), source.Wc).Repo.CommitDiff(rev.Commit, folder);

    public string FileAt(SgRoot root, CheckoutConfig co, HistorySource source, LogRevision rev, ChangedPath path, bool before) =>
        GitSubmodules.Of(Units(root, co), source.Wc).Repo.FileAt(rev, path, before);

    /// <summary>
    /// git blame in the repository the file belongs to. In the snapshot's reading it blames the server
    /// commit the snapshot holds for that repository, so its line numbers are the snapshot's; otherwise
    /// the file on disk, where a line nobody committed yet has no answer.
    /// </summary>
    public List<ServerBlameLine> Blame(SgRoot root, CheckoutConfig co, string path, bool asInSnapshot)
    {
        var unit = GitSubmodules.Innermost(Units(root, co), path);
        var args = new List<string> { "blame", "--line-porcelain" };
        if (asInSnapshot)
        {
            var snap = root.Git.RefSha(root.SnapshotRef(co));
            var meta = snap != null ? SnapshotMeta.Parse(root.Git.Body(snap)) : null;
            var commit = meta == null ? "" : unit.Wc.Length == 0 ? meta.Commit : meta.ExternalCommits.GetValueOrDefault(unit.Wc, "");
            if (commit.Length > 0) args.Add(commit);
        }
        args.Add("--");
        args.Add(unit.RelOf(path));
        var r = unit.Repo.Run(args);
        if (!r.Ok) return new();
        return Git.ParseBlame(r.StdOut).Select(l => l.Sha.All(c => c == '0')
            ? new ServerBlameLine(l.Number, 0, "", "")
            : new ServerBlameLine(l.Number, 0, l.Author, l.Date, l.Sha)).ToList();
    }

    public (LogRevision? Log, string Diff) BlameDetails(SgRoot root, CheckoutConfig co, string path, ServerBlameLine line)
    {
        if (line.Commit.Length == 0) return (null, "");
        var repo = GitSubmodules.Innermost(Units(root, co), path).Repo;
        var log = repo.Log(["-1", line.Commit]).FirstOrDefault();
        return (log, repo.CommitDiff(line.Commit));
    }

    // ---- externals ----

    /// <summary>
    /// Every submodule checked out: where it points, and its repository as what is declared, which for a
    /// git submodule means the commit its parent pins. One on a branch of its own is switched.
    /// </summary>
    public List<Ops.ExternalState> Externals(SgRoot root, CheckoutConfig co) =>
        Units(root, co).Skip(1).Select(u =>
        {
            var head = u.Repo.Rev("HEAD") ?? "";
            return new Ops.ExternalState
            {
                Rel = u.Wc,
                Url = u.Location,
                Declared = u.Url,
                ReposRoot = u.Url,
                Commit = head,
                Revision = head.Length > 0 ? u.Repo.Height(head) : 0,
            };
        }).ToList();

    /// <summary>
    /// Points one submodule elsewhere, here only: onto a branch of its repository, which it then follows
    /// the way the clone follows its own, or, given the bare repository, back to the commit its parent
    /// pins. Nothing is committed; the parent's pin stays what it was.
    /// </summary>
    public UpstreamUpdate SwitchExternal(SgRoot root, CheckoutConfig co, string rel, string url)
    {
        rel = PathUtil.Rel(rel);
        if (rel.Length == 0) throw new SgException("the checkout root is not a submodule");
        var unit = Units(root, co).FirstOrDefault(u => u.Wc.Equals(rel, StringComparison.OrdinalIgnoreCase))
                   ?? throw new SgException("no such submodule checked out in the checkout: " + rel);
        var (repoUrl, branch) = GitLocation.Parse(url.Trim());
        if (!GitSubmodules.SameRepo(repoUrl, unit.Url))
            throw new SgException($"{rel} stays on its own repository, {unit.Url}. Name one of its branches: {unit.Url}#<branch>");
        var repo = unit.Repo;
        if (repo.InProgress() is { } op) throw new SgException($"a {op} is in progress in {rel}. Finish or abort it with git first.");
        using var _ = root.Lock();
        var res = new UpstreamUpdate();
        ProcResult r;
        if (branch == null)
        {
            var pin = unit.Pin;
            root.Log.Info($"putting {rel} back on {Rev.Short(pin)}, the commit its parent pins");
            if (repo.Rev(pin) == null) repo.Run("fetch", "--quiet", unit.Remote);
            r = repo.Run("-c", "submodule.recurse=false", "checkout", "--detach", "--quiet", pin);
        }
        else
        {
            root.Log.Info($"switching {rel} to {branch}");
            var remote = unit.Remote;
            var tracking = $"refs/remotes/{remote}/{branch}";
            var f = repo.Run("fetch", "--quiet", remote, $"+refs/heads/{branch}:{tracking}");
            if (!f.Ok) throw new SgException($"{branch} is not on {unit.Url}: " + FirstLine(f.StdErr));
            if (repo.Rev("refs/heads/" + branch) != null)
            {
                r = repo.Run("-c", "submodule.recurse=false", "checkout", "--quiet", branch);
                if (r.Ok)
                {
                    repo.Run("branch", "--quiet", "--set-upstream-to=" + remote + "/" + branch, branch);
                    var head = repo.Rev("HEAD");
                    var tip = repo.Rev(tracking)!;
                    if (head != tip && head != null && repo.IsAncestor(head, tip)) FastForward(repo, tip, res);
                    else if (head != tip) res.Warnings.Add($"{rel} is on its own {branch}, which is not where {remote}/{branch} is. Bring them together with git.");
                }
            }
            else r = repo.Run("-c", "submodule.recurse=false", "checkout", "--quiet", "--track", "-b", branch, remote + "/" + branch);
        }
        if (!r.Ok) throw new SgException($"git checkout in {rel} failed: " + (r.StdErr + "\n" + r.StdOut).Trim());
        var now = repo.Rev("HEAD") ?? "";
        res.Commit = now;
        res.Revision = now.Length > 0 ? repo.Height(now) : null;
        res.Conflicts = repo.Conflicted().Count;
        root.Log.Info($"{rel} is now at {(branch == null ? unit.Url : GitLocation.Format(unit.Url, branch))}, {Rev.Short(now)}");
        return res;
    }

    /// <summary>The branches of the repository a location names: the clone's remote, a submodule's, or any other.</summary>
    public List<string> BranchNames(SgRoot root, CheckoutConfig co, string url)
    {
        var repoUrl = GitLocation.Parse(url).Url;
        if (repoUrl.Length == 0 || GitSubmodules.SameRepo(repoUrl, co.ReposRoot)) return RemoteBranches(root, co.Path, Remote(co));
        var unit = Units(root, co).FirstOrDefault(u => GitSubmodules.SameRepo(u.Url, repoUrl));
        return unit != null ? RemoteBranches(root, unit.Repo.Path, unit.Remote) : RemoteBranches(root, Path.GetTempPath(), repoUrl);
    }

    public string UrlForBranch(SgRoot root, CheckoutConfig co, string url, string branch) => GitLocation.WithBranch(url, branch);

    static List<string> RemoteBranches(SgRoot root, CheckoutConfig co) => RemoteBranches(root, co.Path, Remote(co));

    static List<string> RemoteBranches(SgRoot root, string cwd, string remote)
    {
        var r = LsRemoteHeads(root, cwd, remote, null);
        if (!r.Ok) throw new SgException($"git ls-remote {remote} failed in {cwd}: " + r.StdErr.Trim());
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(p => p.Length == 2 && p[1].Trim().StartsWith("refs/heads/", StringComparison.Ordinal))
            .Select(p => p[1].Trim()[11..])
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- server branches and checkouts ----

    public List<BranchPart> Parts(SgRoot root, CheckoutConfig co) =>
        Units(root, co).Select(u => new BranchPart { Wc = u.Wc, Url = u.Location }).ToList();

    /// <summary>
    /// A new git branch is one ref on the remote, at the server branch's tip. Git takes no message for
    /// it. Each submodule not kept gets a branch of its own in its own repository, at the commit the
    /// checkout has it at, and the repository around it names that branch in .gitmodules on its own new
    /// branch, so commits made from the new branch go to the submodule's new branch too.
    /// </summary>
    public ServerBranchPlan PlanBranch(SgRoot root, CheckoutConfig co, string name, string? message, IReadOnlyList<BranchPart>? parts)
    {
        root.Git.CheckBranchName(name);
        parts ??= [];
        if (parts.Any(p => PathUtil.Rel(p.Wc).Length == 0 && (p.Keep || p.Branch is { Length: > 0 })))
            throw new SgException("the root of the checkout is the branch itself: it cannot be kept or given another name");
        var units = Units(root, co);
        foreach (var p in parts.Where(p => PathUtil.Rel(p.Wc).Length > 0))
            if (!units.Any(u => u.Wc.Equals(PathUtil.Rel(p.Wc), StringComparison.OrdinalIgnoreCase)))
                throw new SgException($"{p.Wc} is not a submodule checked out in {co.Name}");

        var plan = new ServerBranchPlan
        {
            Kind = CheckoutKind.Git,
            Name = name,
            Source = co.Name,
            NewRootUrl = GitLocation.WithBranch(co.Url, name),
            Message = message ?? "",
        };
        var branched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var u in units.Skip(1))
        {
            var part = parts.FirstOrDefault(p => PathUtil.Rel(p.Wc).Equals(u.Wc, StringComparison.OrdinalIgnoreCase));
            // Inside a kept submodule is kept with it: nothing would name a branch of it.
            if (part is { Keep: true } || !branched.Contains(u.Parent!.Wc))
            {
                plan.Kept.Add((u.Wc, u.Location));
                continue;
            }
            var own = part?.Branch is { Length: > 0 } b ? b.Trim() : name;
            root.Git.CheckBranchName(own);
            var dst = GitLocation.Format(u.Url, own);
            if (RemoteBranches(root, u.Repo.Path, u.Remote).Contains(own, StringComparer.Ordinal)) throw new SgException("already on the server: " + dst);
            plan.Repos.Add(new RepoPlan { Wc = u.Wc, ReposRoot = u.Url, Copies = [(u.Location, dst)] });
            branched.Add(u.Wc);
        }
        if (RemoteBranches(root, co).Contains(name, StringComparer.Ordinal)) throw new SgException("already on the server: " + plan.NewRootUrl);
        plan.Repos.Add(new RepoPlan { Wc = "", ReposRoot = co.ReposRoot, Copies = [(co.Url, plan.NewRootUrl)] });
        // Deepest first and the root last: a repository's new branch names its submodules' new ones, so those go first.
        plan.Repos = plan.Repos.OrderByDescending(r => r.Wc.Length == 0 ? -1 : r.Wc.Count(c => c == '/')).ToList();
        return plan;
    }

    /// <summary>
    /// Pushes each new branch, and only where the name is still free: a lease on "not there" is git's way
    /// of saying copy, never overwrite. A repository whose submodules got branches too starts its new
    /// branch with one commit on top of the tip that names them in .gitmodules.
    /// </summary>
    public void ExecuteBranch(SgRoot root, CheckoutConfig co, ServerBranchPlan plan)
    {
        var units = Units(root, co);
        var made = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rp in plan.Repos)
        {
            try
            {
                var unit = GitSubmodules.Of(units, rp.Wc);
                var repo = unit.Repo;
                var own = GitLocation.Parse(rp.Copies[0].Dst).Branch ?? plan.Name;
                string at;
                if (unit.Pinned) at = repo.Rev("HEAD") ?? throw new SgException($"{unit.Wc} has no commit checked out.");
                else
                {
                    unit.Fetch();
                    at = repo.Rev(unit.TrackingRef) ?? throw new SgException($"{unit.Remote}/{unit.Branch} is not in {unit.Label}.");
                }
                var children = units.Where(u => u.Parent == unit && made.ContainsKey(u.Wc)).ToList();
                if (children.Count > 0) at = NamingBranches(root, unit, at, children.Select(c => (c, made[c.Wc])).ToList(), plan.Name);
                root.Log.Info($"{rp.ReposRoot}: branch {own} at {Rev.Short(at)}");
                var r = repo.Run("push", "--porcelain", $"--force-with-lease=refs/heads/{own}:", unit.Remote, $"{at}:refs/heads/{own}");
                if (!r.Ok) throw new SgException((r.StdErr + "\n" + r.StdOut).Trim());
                repo.Run("update-ref", $"refs/remotes/{unit.Remote}/{own}", at);
                made[unit.Wc] = own;
                rp.Commit = at;
                rp.Revision = repo.Height(at);
                rp.State = "committed";
            }
            catch (SgException ex)
            {
                rp.State = "failed";
                foreach (var rest in plan.Repos.Where(x => x.State == "planned")) rest.State = "skipped";
                throw new SgException($"branch creation stopped at {rp.ReposRoot}: {ex.Message}");
            }
        }
    }

    /// <summary>A commit on top of at whose .gitmodules sends each of these submodules to its new branch.</summary>
    static string NamingBranches(SgRoot root, GitUnit unit, string at, List<(GitUnit Sub, string Branch)> subs, string name)
    {
        var repo = unit.Repo;
        var file = root.NewTempFile(".gitmodules");
        var idx = root.NewTempFile(".index");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = idx };
        try
        {
            var blob = repo.Run("cat-file", "blob", at + ":.gitmodules");
            File.WriteAllText(file, blob.Ok ? blob.StdOut : "");
            foreach (var (sub, branch) in subs)
                repo.Ok("config", "-f", file, $"submodule.{sub.Entry!.Name}.branch", branch);
            var sha = repo.Out("hash-object", "-w", "--no-filters", file);
            repo.Run(["read-tree", at], null, env).EnsureOk();
            repo.Run(["update-index", "--cacheinfo", "100644," + sha + ",.gitmodules"], null, env).EnsureOk();
            var tree = repo.Run(["write-tree"], null, env).EnsureOk().StdOut.Trim();
            return repo.CommitTree(tree, [at], $"Point submodules at the {name} branches\n\n"
                                                + string.Join("\n", subs.Select(s => $"{s.Sub.Entry!.Path}: {s.Branch}")) + "\n");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (File.Exists(idx)) File.Delete(idx);
        }
    }

    /// <summary>
    /// A new clone of a server branch that borrows every object the nearest clone already has, so only
    /// what differs comes over the network, then keeps its own copies of them (git clone --reference
    /// --dissociate): the git shape of copying the nearest checkout and switching it. Its submodules are
    /// cloned with it.
    /// </summary>
    public CheckoutResult ServerCheckout(SgRoot root, CheckoutConfig near, string target, string? name)
    {
        string location;
        if (target.Contains('#')) location = target.Trim();
        else if (GitLocation.KindOfUrl(target) == CheckoutKind.Git && (target.Contains("://") || target.Contains('@')))
            throw new SgException($"name the branch of {target}: {target}#<branch>");
        else location = GitLocation.WithBranch(near.Url, target.Trim());
        var (url, branch) = GitLocation.Parse(location);
        if (branch == null) throw new SgException("name the branch: " + location);
        name ??= branch.Replace('/', '-').Replace('\\', '-');
        root.Git.CheckBranchName(name);
        var dir = Path.Combine(root.RootPath, name);
        if (Directory.Exists(dir) || File.Exists(dir)) throw new SgException("folder exists: " + dir);
        if (root.Config.Checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new SgException("checkout name already in use: " + name);
        if (!UrlExists(root, location)) throw new SgException("not on the server: " + location);
        using var _ = root.Lock();

        root.Log.Info($"cloning {location} into {dir}, borrowing what {near.Name} already has");
        var args = new List<string>
        {
            "clone", "--quiet", "--recurse-submodules", "--reference-if-able", near.Path, "--dissociate",
            "--origin", Remote(near), "--branch", branch, "--", url, dir,
        };
        new GitRepo(root.Config.GitExe, root.RootPath, root.Log).Run(args).EnsureOk();
        return Ops.CheckoutAdd(root, dir, near.Skip, near.Junctions, near.Optional, name, near.Shared, CheckoutKind.Git);
    }

    // ---- merging ----

    /// <summary>The clone and every submodule: a merge goes into one repository, from another branch of the same one.</summary>
    public List<MergeTarget> MergeTargets(SgRoot root, CheckoutConfig co) =>
        Units(root, co).Select(u => new MergeTarget(u.Wc, u.Location, u.Url)).ToList();

    public List<MergeSource> MergeSources(SgRoot root, CheckoutConfig co, MergeTarget target)
    {
        var unit = GitSubmodules.Of(Units(root, co), target.Wc);
        var current = unit.Pinned ? null : unit.Branch;
        return RemoteBranches(root, unit.Repo.Path, unit.Remote).Where(b => b != current)
            .Select(b => new MergeSource(b, GitLocation.Format(unit.Url, b))).ToList();
    }

    public List<MergePair> MergePairs(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl) => [new MergePair(target, sourceUrl)];

    /// <summary>
    /// The source branch's commits, newest first, merges left out: a merge commit is not a change one can
    /// pick. One is marked merged when the repository has it, or has a commit with the same change, which
    /// is how git cherry tells a cherry-picked commit apart from one still to take.
    /// </summary>
    public List<MergeRevision> MergeOffered(SgRoot root, CheckoutConfig co, IReadOnlyList<MergePair> pairs, int limit)
    {
        var units = Units(root, co);
        var res = new List<MergeRevision>();
        foreach (var pair in pairs)
        {
            var unit = GitSubmodules.Of(units, pair.Target.Wc);
            var repo = unit.Repo;
            var branch = GitLocation.Parse(pair.SourceUrl).Branch ?? throw new SgException("no branch in " + pair.SourceUrl);
            var src = FetchSource(unit, branch);
            var log = repo.Log(["--no-merges", "-n", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), src]);
            var cherry = repo.Run("cherry", "HEAD", src);
            var eligible = cherry.Ok
                ? cherry.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith('+')).Select(l => l[1..].Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A position, newest highest, so the list sorts and tells rows apart the way revision numbers do.
            for (var i = 0; i < log.Count; i++) log[i].Revision = log.Count - i;
            res.AddRange(log.Select(e => new MergeRevision(pair, e, !eligible.Contains(e.Commit))));
        }
        return res.OrderByDescending(r => r.Entry.Date, StringComparer.Ordinal).ThenByDescending(r => r.Revision).ToList();
    }

    /// <summary>The source branch fetched into the repository's remote-tracking refs, and its name there.</summary>
    static string FetchSource(GitUnit unit, string branch)
    {
        var src = $"refs/remotes/{unit.Remote}/{branch}";
        var r = unit.Repo.Run("fetch", "--quiet", unit.Remote, $"+refs/heads/{branch}:{src}");
        if (!r.Ok) throw new SgException($"git fetch {unit.Remote} {branch} failed in {unit.Repo.Path}: " + r.StdErr.Trim());
        return src;
    }

    public List<string> MergeProblems(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl)
    {
        var problems = new List<string>();
        var (sourceRepo, branch) = GitLocation.Parse(sourceUrl);
        if (branch == null) { problems.Add("no branch in " + sourceUrl); return problems; }
        var units = Units(root, co);
        var unit = GitSubmodules.Of(units, target.Wc);
        if (!unit.Pinned && branch == unit.Branch) problems.Add("the source and the target are the same branch");
        if (!GitSubmodules.SameRepo(sourceRepo, unit.Url))
            problems.Add($"{sourceUrl} is in another repository than {unit.Label}. A merge stays inside one repository.");
        var repo = unit.Repo;
        var (local, remote, tracked) = unit.Tracking;
        if (unit.Wc.Length == 0 && (local == null || remote != Remote(co) || tracked != Branch(co)))
            problems.Add($"the clone is not on the branch that tracks {Remote(co)}/{Branch(co)}");
        if (unit.Pinned && (local != null || repo.Rev("HEAD") != unit.Pin))
            problems.Add($"{unit.Wc} is not at {Rev.Short(unit.Pin)}, the commit its parent pins. Sync first.");
        if (repo.InProgress() is { } op) problems.Add($"a {op} is in progress in {(unit.Wc.Length == 0 ? "the clone" : unit.Wc)}. Finish or abort it with git first.");
        var local4 = StatusOf(unit).Where(c => c.Versioned).Select(c => c.Path).Take(4).ToList();
        if (local4.Count > 0)
            problems.Add($"{unit.Label} has local changes ({string.Join(", ", local4)}). Commit or revert them first, so what the merge brings in stands on its own.");
        return problems;
    }

    /// <summary>
    /// Everything: git merge --squash, which leaves the source's changes staged and records no merge, the
    /// way svn merge leaves local changes for the changes window to send. Picked commits: cherry-pick, or
    /// revert when taking them back out, with --no-commit either way. A conflict stops it with the files
    /// in conflict and the sequencer let go, so what is on disk is all there is. A dry run asks git
    /// merge-tree the same questions and touches no file. It runs in the target's own repository, and
    /// the paths it reports are from the checkout root.
    /// </summary>
    public MergeResult MergeRun(SgRoot root, CheckoutConfig co, MergePair pair, IReadOnlyList<LogRevision>? picked, bool dryRun, bool reverse)
    {
        using var operation = root.Lock();
        if (reverse && (picked == null || picked.Count == 0))
            throw new SgException("taking a revision back out needs the revisions named. Pick them in the list first.");
        var problems = MergeProblems(root, co, pair.Target, pair.SourceUrl);
        if (!dryRun && problems.Count > 0) throw new SgException("merge refused:\n  " + string.Join("\n  ", problems));

        var unit = GitSubmodules.Of(Units(root, co), pair.Target.Wc);
        var repo = unit.Repo;
        var src = FetchSource(unit, GitLocation.Parse(pair.SourceUrl).Branch!);
        var result = new MergeResult { DryRun = dryRun, SourceUrl = pair.SourceUrl, Target = pair.Label, Reverse = reverse };

        var commits = Ordered(repo, (picked ?? []).Select(p => p.Commit).Where(c => c.Length > 0).Distinct().ToList(), reverse);
        result.Commits = commits;
        string output;
        if (dryRun)
        {
            var tree = commits.Count == 0 ? MergeTree(repo, null, "HEAD", src, result) : PickTrees(repo, commits, reverse, result);
            output = result.Output;
            foreach (var line in repo.Out("diff-tree", "-r", "--name-status", "--no-renames", "HEAD", tree).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length >= 2) result.Changed.Add((p[0][0], unit.Full(p[1])));
            }
        }
        else
        {
            var r = commits.Count == 0
                ? repo.Run("merge", "--squash", "--quiet", src)
                : repo.Run(new[] { reverse ? "revert" : "cherry-pick", "--no-commit" }.Concat(commits));
            output = (r.StdOut + "\n" + r.StdErr).Trim();
            if (!r.Ok && commits.Count > 0) repo.Run(reverse ? "revert" : "cherry-pick", "--quit");
            result.Conflicts.AddRange(repo.Conflicted());
            if (!r.Ok && result.Conflicts.Count == 0) throw new SgException("git " + (commits.Count == 0 ? "merge" : reverse ? "revert" : "cherry-pick") + " failed:\n" + output);
            foreach (var line in repo.Out("diff", "--cached", "--name-status", "--no-renames", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('\t');
                if (p.Length >= 2) result.Changed.Add((result.Conflicts.Contains(p[1]) ? 'C' : p[0][0], unit.Full(p[1])));
            }
        }
        for (var i = 0; i < result.Conflicts.Count; i++) result.Conflicts[i] = unit.Full(result.Conflicts[i]);
        result.Output = output;
        return result;
    }

    /// <summary>Oldest first for taking commits in, newest first for taking them back out.</summary>
    static List<string> Ordered(GitRepo repo, List<string> commits, bool reverse)
    {
        if (commits.Count < 2) return commits;
        var sorted = repo.Out(new[] { "rev-list", "--no-walk=sorted", "--topo-order" }.Concat(commits).ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        return reverse ? sorted : Enumerable.Reverse(sorted).ToList();
    }

    /// <summary>git merge-tree: the tree a merge would write, and the files it would leave in conflict.</summary>
    static string MergeTree(GitRepo repo, string? mergeBase, string ours, string theirs, MergeResult result)
    {
        var args = new List<string> { "merge-tree", "--write-tree", "--name-only", "--no-messages" };
        if (mergeBase != null) args.Add("--merge-base=" + mergeBase);
        args.Add(ours);
        args.Add(theirs);
        var r = repo.Run(args);
        if (r.ExitCode is not (0 or 1)) throw new SgException("git merge-tree failed:\n" + r.StdErr.Trim());
        var lines = r.StdOut.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        foreach (var path in lines.Skip(1).Where(l => l.Length > 0))
            if (!result.Conflicts.Contains(path)) result.Conflicts.Add(path);
        result.Output += (result.Output.Length > 0 ? "\n" : "") + (r.ExitCode == 0 ? "clean: " : "conflicts: ") + theirs;
        return lines[0].Trim();
    }

    /// <summary>The picks one after another, each on the tree the one before it wrote, the way the real run stacks them.</summary>
    static string PickTrees(GitRepo repo, List<string> commits, bool reverse, MergeResult result)
    {
        var current = repo.Rev("HEAD")!;
        var tree = repo.TreeOf(current);
        foreach (var c in commits)
        {
            var parent = repo.Rev(c + "^1") ?? throw new SgException(Rev.Short(c) + " is the first commit of its history; there is nothing to take it against.");
            tree = reverse ? MergeTree(repo, c, current, parent, result) : MergeTree(repo, parent, current, c, result);
            current = repo.CommitTree(tree, [current], "sg dry run\n");
        }
        return tree;
    }

    // ---- identity ----

    /// <summary>The repository's first commit, which every clone of it shares however it is reached, and the branch.</summary>
    public (string Uuid, string Path)? Identity(SgRoot root, CheckoutConfig co)
    {
        var repo = Repo(root, co);
        var tip = repo.Rev(Tracking(co)) ?? repo.Rev("HEAD");
        if (tip == null) return null;
        var roots = repo.Out("rev-list", "--max-parents=0", tip).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        if (roots.Count == 0) return null;
        return (roots.Min(StringComparer.Ordinal)!, Branch(co));
    }
}
