using System.Text;
using System.Text.RegularExpressions;

namespace Sg.Core;

public sealed record DiffEntry(char Status, string Path, string? OldPath);
public sealed record TreeEntry(string Mode, string Type, string Sha, string Path);
public sealed record WorktreeInfo(string Path, string? Head, string? Branch, bool Bare);

/// <summary>
/// The two kinds of half done replay a worktree can be holding. Both stop the same way - some files
/// in conflict, and a commit waiting to be made - and both are carried on, skipped or undone by the
/// same three verbs, so everything above here can treat them as one thing that knows which it is.
/// </summary>
public enum Replay
{
    None,

    /// <summary>The branch is being moved onto a newer snapshot.</summary>
    Rebase,

    /// <summary>A patch series from an export is being replayed onto the branch.</summary>
    Import,
}

/// <summary>Where a stopped replay got to: patch 3 of 20, and the subject of the one it stopped on.</summary>
public sealed record ReplayProgress(int At, int Of, string Subject);

/// <summary>A ref read in bulk: its commit, that commit's whole message, and when it was committed.</summary>
public sealed record RefInfo(string Sha, string Message, DateTimeOffset? Committed = null)
{
    public string Subject
    {
        get
        {
            var i = Message.IndexOf('\n');
            return (i < 0 ? Message : Message[..i]).Trim();
        }
    }
}

/// <summary>Thin wrapper over git.exe. Every call names the folder it runs in. The store is a bare repo.</summary>
public sealed class Git
{
    static readonly Regex InvalidPath = new(@"invalid path '([^']+)'", RegexOptions.Compiled);
    // Runs against every line git checkout prints, so it is built once.
    static readonly Regex CheckoutCounts = new(@"\((\d+)/(\d+)\)", RegexOptions.Compiled);

    readonly string _exe;
    readonly ILog _log;
    readonly Dictionary<string, string> _env;
    public string Store { get; }

    public Git(string exe, string store, ILog log)
    {
        _exe = exe;
        Store = store;
        _log = log;
        _env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["LC_ALL"] = "C",
            ["GIT_AUTHOR_NAME"] = "sg",
            ["GIT_AUTHOR_EMAIL"] = "sg@localhost",
            ["GIT_COMMITTER_NAME"] = "sg",
            ["GIT_COMMITTER_EMAIL"] = "sg@localhost",
            ["GIT_EDITOR"] = "true",
            ["GIT_PAGER"] = "cat",
        };
    }

    // ---- running ----

    public ProcResult Run(string? cwd, IEnumerable<string> args, byte[]? stdin = null, IReadOnlyDictionary<string, string>? extraEnv = null,
        bool asUser = false, string? stdoutToFile = null)
    {
        cwd ??= Store;
        var list = new List<string> { "-C", cwd };
        list.AddRange(args);
        IReadOnlyDictionary<string, string> env = _env;
        if (extraEnv != null || asUser)
        {
            var d = new Dictionary<string, string>(_env);
            if (asUser && HasUserIdentity) foreach (var k in Ident) d.Remove(k);
            if (extraEnv != null) foreach (var kv in extraEnv) d[kv.Key] = kv.Value;
            env = d;
        }
        return Proc.Run(_exe, list, cwd, _log, stdin, env, stdoutToFile);
    }

    public ProcResult Run(string? cwd, params string[] args) => Run(cwd, (IEnumerable<string>)args);
    public ProcResult Ok(string? cwd, params string[] args) => Run(cwd, args).EnsureOk();
    public string Out(string? cwd, params string[] args) => Ok(cwd, args).StdOut.Trim();

    static readonly string[] Ident = ["GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL"];

    /// <summary>
    /// Whether this machine has a git identity of its own. asUser means "the person at the keyboard owns
    /// this commit", and it works by taking sg's own identity out of the environment and letting git find
    /// one. A machine that never set one makes git refuse outright rather than guess, and importing a
    /// branch happens on exactly that machine: a fresh PC that has only the archive. So when there is
    /// nothing to find, sg's identity stays and the commit is made rather than lost.
    ///
    /// git is asked, rather than user.name and user.email being read. It builds the ident from more than
    /// those two keys - EMAIL in the environment is one - so reading the keys called a machine nameless
    /// that git would have committed for, and threw away a name its owner had set. The answer is not
    /// kept: nothing in sg writes the identity, so the only way it appears is from outside this process,
    /// and a held answer would stay wrong until the app was restarted.
    /// </summary>
    bool HasUserIdentity
    {
        get
        {
            var d = new Dictionary<string, string>(_env);
            foreach (var k in Ident) d.Remove(k);
            return Proc.Run(_exe, ["-C", Store, "var", "GIT_COMMITTER_IDENT"], Store, _log, null, d).Ok;
        }
    }

    static Dictionary<string, string>? IndexEnv(string? indexFile) =>
        indexFile == null ? null : new Dictionary<string, string> { ["GIT_INDEX_FILE"] = indexFile };

    // ---- store ----

    public void InitBare()
    {
        Directory.CreateDirectory(Store);
        Proc.Run(_exe, ["init", "--bare", "-q", PathUtil.Git(Store)], Path.GetDirectoryName(Store), _log, null, _env).EnsureOk();
    }

    public void Config(string key, string value) => Ok(null, "config", key, value);

    public string? ConfigGet(string key)
    {
        var r = Run(null, "config", "--get", key);
        return r.Ok ? r.StdOut.Trim() : null;
    }

    public void ConfigUnset(string key) => Run(null, "config", "--unset", key);

    public string Version() => Out(null, "--version");

    public string EnsureRootCommit()
    {
        var sha = RefSha(SgRoot.RootRef);
        if (sha != null) return sha;
        var tree = Run(null, ["mktree"], Array.Empty<byte>()).EnsureOk().StdOut.Trim();
        var commit = CommitTree(tree, null, "sg root\n");
        UpdateRef(SgRoot.RootRef, commit);
        return commit;
    }

    // ---- refs ----

    public string? RefSha(string refName)
    {
        var r = Run(null, "rev-parse", "--verify", "--quiet", refName + "^{commit}");
        return r.Ok ? r.StdOut.Trim() : null;
    }

    /// <summary>
    /// Every ref under the given prefixes, with its sha and its whole message, in one git call.
    /// Status used to spend a git process per ref; a root with ten worktrees paid for thirty of them.
    /// </summary>
    public Dictionary<string, RefInfo> RefIndex(params string[] prefixes)
    {
        var a = new List<string> { "for-each-ref", "--format=%(refname)%1f%(objectname)%1f%(committerdate:unix)%1f%(contents)%1e" };
        a.AddRange(prefixes);
        var r = Ok(null, a.ToArray());
        var res = new Dictionary<string, RefInfo>(StringComparer.Ordinal);
        foreach (var record in r.StdOut.Split('\x1e'))
        {
            // Four fields at most, so a separator inside a commit message stays part of the message.
            var p = record.TrimStart('\n', '\r').Split('\x1f', 4);
            if (p.Length < 4 || p[0].Length == 0) continue;
            // A tag or a tree has no committer, and the field comes back empty for it.
            DateTimeOffset? when = long.TryParse(p[2], out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
            res[p[0]] = new RefInfo(p[1], p[3], when);
        }
        return res;
    }

    /// <summary>Branch name to sg base checkout, from one git call instead of one per branch.</summary>
    public Dictionary<string, string> BranchBases() => BranchConfig("sgBase");

    /// <summary>Branch name to the value of branch.&lt;name&gt;.&lt;key&gt;, for every branch that has one, in one git call.</summary>
    public Dictionary<string, string> BranchConfig(string key)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // git config exits 1 when nothing matches, which is not an error here.
        var r = Run(null, "config", "--get-regexp", @"^branch\..*\." + key + "$");
        if (!r.Ok) return res;
        var suffix = "." + key;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var k = line[..sp];
            // git lowercases the section and the variable but keeps the branch name as it is.
            if (!k.StartsWith("branch.", StringComparison.OrdinalIgnoreCase)
                || !k.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var branch = k[7..^suffix.Length];
            if (branch.Length > 0) res[branch] = line[(sp + 1)..];
        }
        return res;
    }

    /// <summary>Commits each side has that the other has not, for "left...right". One call instead of two.</summary>
    public (int Left, int Right) CountBoth(string left, string right)
    {
        var r = Run(null, "rev-list", "--left-right", "--count", left + "..." + right);
        if (!r.Ok) return (0, 0);
        var p = r.StdOut.Split(['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 2 && int.TryParse(p[0], out var l) && int.TryParse(p[1], out var rr) ? (l, rr) : (0, 0);
    }

    /// <summary>
    /// The git folder of a worktree, read from its .git file. A linked worktree points at
    /// &lt;store&gt;/worktrees/&lt;name&gt;, which is what "rev-parse --git-path" would answer, without the process.
    /// </summary>
    public static string? GitDirOf(string worktree)
    {
        var dotGit = Path.Combine(worktree, ".git");
        try
        {
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;
            var line = File.ReadAllText(dotGit).Trim();
            if (!line.StartsWith("gitdir:")) return null;
            var gd = line[7..].Trim();
            return Path.GetFullPath(Path.IsPathRooted(gd) ? gd : Path.Combine(worktree, gd));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A path inside the worktree's git folder. Falls back to git when the .git file cannot be read.</summary>
    string GitPath(string worktree, string name)
    {
        var dir = GitDirOf(worktree);
        if (dir != null) return Path.Combine(dir, name);
        var p = Out(worktree, "rev-parse", "--git-path", name);
        return Path.IsPathRooted(p) ? p : Path.Combine(worktree, p);
    }

    /// <summary>The sha a revision names, or null when it names nothing. Runs where the branch is.</summary>
    public string? ResolveCommit(string? cwd, string rev)
    {
        var r = Run(cwd, "rev-parse", "--verify", "--quiet", rev + "^{commit}");
        return r.Ok ? r.StdOut.Trim() : null;
    }

    public string TreeOf(string commitish) => Out(null, "rev-parse", commitish + "^{tree}");
    public void UpdateRef(string refName, string sha) => Ok(null, "update-ref", refName, sha);
    public void DeleteRef(string refName) => Run(null, "update-ref", "-d", refName);
    public void SetHeadDetached(string worktree, string sha) => Ok(worktree, "update-ref", "--no-deref", "HEAD", sha);
    public string HeadSha(string worktree) => Out(worktree, "rev-parse", "HEAD");
    public string Body(string sha) => Ok(null, "log", "-1", "--format=%B", sha).StdOut;
    /// <summary>The first line of a commit message. %s cuts at a \n and not at a lone \r, so Msg does the rest.</summary>
    public string Subject(string sha) => Msg.Subject(Out(null, "log", "-1", "--format=%s", sha));

    public string CurrentBranch(string worktree)
    {
        var r = Run(worktree, "symbolic-ref", "--short", "-q", "HEAD");
        if (!r.Ok) throw new SgException("not on a branch: " + worktree);
        return r.StdOut.Trim();
    }

    public void CheckBranchName(string name)
    {
        if (!Run(null, "check-ref-format", "--branch", name).Ok) throw new SgException("bad branch name: " + name);
    }

    // ---- worktrees ----

    public void WorktreeAddDetachedNoCheckout(string path, string commitish) =>
        Ok(null, "worktree", "add", "--no-checkout", "--detach", PathUtil.Git(path), commitish);

    public void WorktreeAddBranchNoCheckout(string path, string branch, string startPoint) =>
        Ok(null, "worktree", "add", "--no-checkout", "--no-track", "-b", branch, PathUtil.Git(path), startPoint);

    public void WorktreeRepair(string path) => Ok(null, "worktree", "repair", PathUtil.Git(path));

    public void WorktreeRemove(string path, bool force)
    {
        var a = new List<string> { "worktree", "remove" };
        if (force) a.Add("--force");
        a.Add(PathUtil.Git(path));
        Ok(null, a.ToArray());
    }

    public void WorktreePrune() => Run(null, "worktree", "prune");
    public void BranchDelete(string name) => Ok(null, "branch", "-D", name);

    public List<WorktreeInfo> WorktreeList()
    {
        var r = Ok(null, "worktree", "list", "--porcelain");
        var res = new List<WorktreeInfo>();
        string? path = null, head = null, branch = null;
        var bare = false;
        void Flush()
        {
            if (path != null) res.Add(new WorktreeInfo(path, head, branch, bare));
            path = null; head = null; branch = null; bare = false;
        }
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ")) path = Path.GetFullPath(line[9..]);
            else if (line.StartsWith("HEAD ")) head = line[5..];
            else if (line.StartsWith("branch refs/heads/")) branch = line[18..];
            else if (line == "bare") bare = true;
        }
        Flush();
        return res;
    }

    public string Toplevel(string cwd) => Path.GetFullPath(Out(cwd, "rev-parse", "--show-toplevel"));

    // ---- index and trees ----

    string WritePathspecFile(IEnumerable<string> specs)
    {
        var f = Path.Combine(Path.GetTempPath(), "sg-ps-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllBytes(f, Encoding.UTF8.GetBytes(string.Join("\0", specs)));
        return f;
    }

    /// <summary>
    /// git add -A -f with exclusions. Streams each added path to onAdded as git reports it.
    /// Retries around paths git refuses (reserved Windows names) and returns those paths.
    /// </summary>
    public List<string> AddAllForced(string worktree, IEnumerable<string> excludeRelPaths, Action<string>? onAdded = null)
    {
        var excludes = excludeRelPaths.ToList();
        var invalid = new List<string>();
        var lockRemoved = false;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var specs = new List<string> { "." };
            specs.AddRange(excludes.Concat(invalid).Select(p => ":(exclude,literal)" + p));
            var f = WritePathspecFile(specs);
            try
            {
                var args = new List<string> { "-C", worktree, "add", "-A", "-f", "--pathspec-from-file=" + f, "--pathspec-file-nul" };
                if (onAdded != null) args.Add("-v");
                var r = Proc.RunStreaming(_exe, args, worktree, _log,
                    onAdded == null ? null : line =>
                    {
                        if (line.StartsWith("add '") && line.EndsWith('\'')) onAdded(line[5..^1]);
                    },
                    null, _env, keepStdout: false);
                if (r.Ok) return invalid;
                var m = InvalidPath.Match(r.StdErr);
                if (m.Success && !invalid.Contains(m.Groups[1].Value))
                {
                    invalid.Add(m.Groups[1].Value);
                    continue;
                }
                if (!lockRemoved && r.StdErr.Contains("index.lock") && RemoveStaleIndexLock(worktree))
                {
                    lockRemoved = true;
                    continue;
                }
                r.EnsureOk();
            }
            finally { File.Delete(f); }
        }
        throw new SgException("git add gave up: too many invalid paths in " + worktree);
    }

    /// <summary>A killed git add leaves index.lock behind. Remove it when it is older than a minute.</summary>
    bool RemoveStaleIndexLock(string worktree)
    {
        var p = GitPath(worktree, "index.lock");
        if (!File.Exists(p) || DateTime.UtcNow - File.GetLastWriteTimeUtc(p) < TimeSpan.FromMinutes(1)) return false;
        _log.Warn("removing a stale " + p);
        File.Delete(p);
        return true;
    }

    /// <summary>Writes every file of the branch into an empty worktree. Reports git's "Updating files" progress.</summary>
    public void CheckoutForceProgress(string worktree, string branch, Action<long, long>? onProgress)
    {
        var args = new List<string> { "-C", worktree, "checkout", "--progress", "-f", branch };
        var r = Proc.RunStreaming(_exe, args, worktree, _log, null, line =>
        {
            var m = CheckoutCounts.Match(line);
            if (m.Success && onProgress != null) onProgress(long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value));
        }, _env);
        r.EnsureOk();
    }

    public void RmCached(string worktree, IEnumerable<string> relPaths)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        var f = WritePathspecFile(list.Select(p => ":(literal)" + p));
        try { Ok(worktree, "rm", "--cached", "-r", "-q", "--ignore-unmatch", "--pathspec-from-file=" + f, "--pathspec-file-nul"); }
        finally { File.Delete(f); }
    }

    /// <summary>Entries with mode "0" remove the path from the index.</summary>
    public void UpdateIndexInfo(string worktree, IEnumerable<(string Mode, string Sha, string Path)> entries, string? indexFile = null)
    {
        var sb = new StringBuilder();
        foreach (var e in entries) sb.Append(e.Mode).Append(' ').Append(e.Sha).Append('\t').Append(e.Path).Append('\0');
        if (sb.Length == 0) return;
        Run(worktree, ["update-index", "-z", "--index-info"], Encoding.UTF8.GetBytes(sb.ToString()), IndexEnv(indexFile)).EnsureOk();
    }

    public string WriteTree(string worktree, string? indexFile = null) =>
        Run(worktree, ["write-tree"], null, IndexEnv(indexFile)).EnsureOk().StdOut.Trim();

    public void ReadTree(string worktree, string treeish, string indexFile) =>
        Run(worktree, ["read-tree", treeish], null, IndexEnv(indexFile)).EnsureOk();

    public string CommitTree(string tree, string? parent, string message)
    {
        var args = new List<string> { "commit-tree", tree };
        if (parent != null) { args.Add("-p"); args.Add(parent); }
        args.Add("-F"); args.Add("-");
        return Run(null, args, Encoding.UTF8.GetBytes(message)).EnsureOk().StdOut.Trim();
    }

    /// <summary>Who wrote a commit, and when. A commit that replaces others keeps the first one's author.</summary>
    public Author AuthorOf(string sha)
    {
        var p = Ok(null, "log", "-1", "--format=%an%x1f%ae%x1f%aI", sha).StdOut.Trim().Split('\x1f');
        return p.Length >= 3 ? new Author(p[0], p[1], p[2]) : new Author("", "", "");
    }

    /// <summary>
    /// The same as CommitTree, with the author kept and the committer taken from the user's own git
    /// config. That is what git does when it squashes: the work stays yours, the rewrite is theirs.
    /// </summary>
    public string CommitTreeAs(string tree, string? parent, string message, Author author)
    {
        var args = new List<string> { "commit-tree", tree };
        if (parent != null) { args.Add("-p"); args.Add(parent); }
        args.Add("-F"); args.Add("-");
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = author.Name,
            ["GIT_AUTHOR_EMAIL"] = author.Email,
            ["GIT_AUTHOR_DATE"] = author.Date,
        };
        return Run(null, args, Encoding.UTF8.GetBytes(message), env, asUser: true).EnsureOk().StdOut.Trim();
    }

    /// <summary>The commit under this one, or null when it is a root commit.</summary>
    public string? ParentOf(string sha)
    {
        var r = Run(null, "rev-parse", "--verify", "--quiet", sha + "^");
        return r.Ok ? r.StdOut.Trim() : null;
    }

    /// <summary>
    /// The commits of a range, newest first. Used to tell whether a picked set is one unbroken run, and
    /// to count what a branch has that its snapshot does not. Null runs it in the store, which is where a
    /// question about a branch with no worktree has to be asked.
    /// </summary>
    public List<string> RevList(string? worktree, string range) =>
        Ok(worktree, "rev-list", range).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

    /// <summary>Replays the commits after upstream onto newBase and puts the branch there. Nothing to replay just moves it.</summary>
    public ProcResult RebaseOnto(string worktree, string newBase, string upstream, string branch) =>
        Run(worktree, "rebase", "--onto", newBase, upstream, branch);

    /// <summary>
    /// Takes the changes of these commits back out, into the worktree and the index, without committing.
    /// The caller commits, so a run of reverts becomes one commit that says what it undid.
    /// </summary>
    public ProcResult RevertNoCommit(string worktree, IEnumerable<string> shas)
    {
        var a = new List<string> { "revert", "--no-commit", "--no-edit" };
        a.AddRange(shas);
        return Run(worktree, a);
    }

    /// <summary>
    /// Who last changed each line of a file, and which line it was in the commit that changed it. That
    /// original line number is what lets a line traced back to a snapshot be looked up in svn's answer
    /// for the same file, without diffing the two by hand.
    /// </summary>
    public List<GitBlameLine> BlamePorcelain(string worktree, string relPath)
    {
        var r = Run(worktree, "blame", "--line-porcelain", "--", relPath);
        if (!r.Ok) return new();
        var res = new List<GitBlameLine>();
        string sha = "", author = "", date = "", summary = "";
        var original = 0;
        var number = 0;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            // The content line is the only one that starts with a tab, and it ends each entry.
            if (line[0] == '\t')
            {
                res.Add(new GitBlameLine(number, original, sha, author, date, summary, line[1..]));
                continue;
            }
            // "<sha> <original line> <final line> [<lines in this group>]" opens every entry.
            if (line.Length > 40 && Uri.IsHexDigit(line[0]) && line[40] == ' ')
            {
                var p = line.Split(' ');
                sha = p[0];
                original = p.Length > 1 && int.TryParse(p[1], out var o) ? o : 0;
                number = p.Length > 2 && int.TryParse(p[2], out var f) ? f : res.Count + 1;
                author = date = summary = "";
                continue;
            }
            if (line.StartsWith("author ", StringComparison.Ordinal)) author = line[7..];
            else if (line.StartsWith("summary ", StringComparison.Ordinal)) summary = line[8..];
            else if (line.StartsWith("author-time ", StringComparison.Ordinal)
                     && long.TryParse(line[12..], out var when))
                date = DateTimeOffset.FromUnixTimeSeconds(when).ToString("yyyy-MM-dd");
        }
        return res;
    }

    /// <summary>Puts the worktree back the way it was when a revert did not apply, or was not wanted.</summary>
    public void RevertAbort(string worktree)
    {
        // "revert --abort" only works while git thinks a revert is in progress; the reset covers the
        // rest, including a revert that applied and is only sitting in the index.
        Run(worktree, "revert", "--abort");
        Run(worktree, "reset", "-q", "--hard", "HEAD");
    }

    public List<string> HashObjects(IEnumerable<string> files)
    {
        var list = files.ToList();
        if (list.Count == 0) return new();
        var stdin = Encoding.UTF8.GetBytes(string.Join("\n", list) + "\n");
        var r = Run(null, ["hash-object", "-w", "--no-filters", "--stdin-paths"], stdin).EnsureOk();
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    /// <summary>What git would call these files, without writing anything into the store. Answers whether a
    /// file on disk still holds what a tree says it held.</summary>
    public Dictionary<string, string> HashFiles(IEnumerable<string> absolutePaths)
    {
        var list = absolutePaths.ToList();
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list.Count == 0) return res;
        var stdin = Encoding.UTF8.GetBytes(string.Join("\n", list) + "\n");
        var r = Run(null, ["hash-object", "--no-filters", "--stdin-paths"], stdin);
        if (r.Ok)
        {
            var shas = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < shas.Length && i < list.Count; i++) res[list[i]] = shas[i].Trim();
            return res;
        }
        // One file another process is holding open fails the whole batch, and an empty answer says every
        // file changed. Ask again one at a time, so the one that cannot be read is the only one missing.
        foreach (var file in list)
        {
            var one = Run(null, "hash-object", "--no-filters", PathUtil.Git(file));
            if (one.Ok) res[file] = one.StdOut.Trim();
        }
        return res;
    }

    /// <summary>
    /// Adds these paths to an index of its own. Forced, because a shelf has to be able to hold a file the
    /// ignore rules cover: svn calls it unversioned, the snapshot leaves it out, and it is still the
    /// reader's work. -A, so a path that is gone from disk goes in as a deletion.
    /// </summary>
    public void AddPathsForced(string worktree, IEnumerable<string> relPaths, string? indexFile = null)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        var f = WritePathspecFile(list.Select(p => ":(literal)" + p));
        try { Run(worktree, ["add", "-A", "-f", "--pathspec-from-file=" + f, "--pathspec-file-nul"], null, IndexEnv(indexFile)).EnsureOk(); }
        finally { File.Delete(f); }
    }

    /// <summary>The same as CheckoutPaths, into an index of its own, so the real one is left as it was.</summary>
    public void CheckoutPathsWithIndex(string worktree, string treeish, IEnumerable<string> relPaths, string indexFile)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        var f = WritePathspecFile(list.Select(p => ":(literal)" + p));
        try { Run(worktree, ["checkout", treeish, "--pathspec-from-file=" + f, "--pathspec-file-nul"], null, IndexEnv(indexFile)).EnsureOk(); }
        finally { File.Delete(f); }
    }

    /// <summary>One blob, byte for byte, into a file. A merge needs the real bytes, not a decoded string.</summary>
    public void BlobToFile(string sha, string file) =>
        Proc.Run(_exe, ["-C", Store, "cat-file", "blob", sha], Store, _log, null, _env, file).EnsureOk();

    /// <summary>
    /// The three way merge itself, written into current. Returns the number of parts it could not settle,
    /// which it leaves as conflict markers, and -1 when it could not run at all. This is what git uses for
    /// a merge and what svn uses for an update, so a file that comes back from a shelf reads like both.
    /// </summary>
    public int MergeFile(string current, string ancestor, string other, string labelCurrent, string labelOther)
    {
        var r = Run(null, ["merge-file", "-L", labelCurrent, "-L", "the file when it was shelved", "-L", labelOther,
            PathUtil.Git(current), PathUtil.Git(ancestor), PathUtil.Git(other)]);
        if (r.ExitCode < 0 || r.ExitCode > 127) return -1;
        return r.ExitCode;
    }

    public List<DiffEntry> DiffNameStatus(string worktree, string from, string to, bool renames = true)
    {
        // A patch that is going to be applied again must not carry rename headers: git apply takes such
        // a file only as a whole, and a shelf is written back file by file.
        var r = Ok(worktree, "diff", "--name-status", "-z", renames ? "-M" : "--no-renames", "--no-color", from, to);
        var tok = r.StdOut.Split('\0');
        var res = new List<DiffEntry>();
        var i = 0;
        while (i < tok.Length)
        {
            var s = tok[i++];
            if (s.Length == 0) break;
            var c = s[0];
            if (c is 'R' or 'C')
            {
                var old = tok[i++];
                var nw = tok[i++];
                res.Add(new DiffEntry(c, PathUtil.Rel(nw), PathUtil.Rel(old)));
            }
            else res.Add(new DiffEntry(c, PathUtil.Rel(tok[i++]), null));
        }
        return res;
    }

    /// <summary>Writes the given paths from a tree-ish into the worktree and its index.</summary>
    public void CheckoutPaths(string worktree, string treeish, IEnumerable<string> relPaths)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        var f = WritePathspecFile(list.Select(p => ":(literal)" + p));
        try { Ok(worktree, "checkout", treeish, "--pathspec-from-file=" + f, "--pathspec-file-nul"); }
        finally { File.Delete(f); }
    }

    public List<TreeEntry> LsTree(string treeish, IEnumerable<string>? paths = null, bool recursive = false)
    {
        var a = new List<string> { "ls-tree", "-z" };
        if (recursive) a.Add("-r");
        a.Add(treeish);
        if (paths != null) a.AddRange(paths);
        var r = Ok(null, a.ToArray());
        var res = new List<TreeEntry>();
        foreach (var line in r.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var meta = line[..tab].Split(' ');
            res.Add(new TreeEntry(meta[0], meta[1], meta[2], line[(tab + 1)..]));
        }
        return res;
    }

    public List<TreeEntry> LsTreeChildren(string treeish, string dirRel) =>
        dirRel.Length == 0 ? LsTree(treeish) : LsTree(treeish, [dirRel + "/"]);

    // ---- worktree state ----

    public bool IsClean(string worktree) => DirtyCount(worktree) == 0;

    /// <summary>
    /// How many tracked files are changed and not committed. Untracked files do not count, the same
    /// way they do not stop a push; this is the number the overview puts on the card.
    /// </summary>
    public int DirtyCount(string worktree) => StatusEntries(worktree, untracked: false).Count;

    public bool IsAncestor(string a, string b) => Run(null, "merge-base", "--is-ancestor", a, b).ExitCode == 0;
    public int CountCommits(string from, string to) => int.Parse(Out(null, "rev-list", "--count", from + ".." + to));
    public string LogBodies(string from, string to) => Ok(null, "log", "--reverse", "--format=%B", from + ".." + to).StdOut;

    public ProcResult Rebase(string worktree, string onto) => Run(worktree, "rebase", onto);
    public void RebaseAbort(string worktree) => Run(worktree, "rebase", "--abort");
    public ProcResult RebaseContinue(string worktree) => Run(worktree, "rebase", "--continue");

    /// <summary>Drops the commit the rebase stopped on and goes on with the ones after it.</summary>
    public ProcResult RebaseSkip(string worktree) => Run(worktree, "rebase", "--skip");

    /// <summary>During a rebase HEAD is detached. This gives the branch the rebase works on.</summary>
    public string? RebaseHeadName(string worktree)
    {
        foreach (var dir in new[] { "rebase-merge", "rebase-apply" })
        {
            var p = GitPath(worktree, dir + "/head-name");
            if (!File.Exists(p)) continue;
            var name = File.ReadAllText(p).Trim();
            return name.StartsWith("refs/heads/") ? name[11..] : name;
        }
        return null;
    }

    /// <summary>The branch, also in the middle of a rebase.</summary>
    public string BranchOrRebaseHead(string worktree)
    {
        var r = Run(worktree, "symbolic-ref", "--short", "-q", "HEAD");
        if (r.Ok) return r.StdOut.Trim();
        return RebaseHeadName(worktree) ?? throw new SgException("not on a branch: " + worktree);
    }

    // ---- conflicts ----

    public List<string> ConflictedFiles(string worktree) =>
        Ok(worktree, "diff", "--name-only", "--diff-filter=U", "-z").StdOut
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(PathUtil.Rel).ToList();

    /// <summary>
    /// Stage 1 is the common base, 2 is "ours" and 3 is "theirs". Which side is which depends on the
    /// replay: during a rebase ours is the snapshot and theirs is the branch commit being replayed,
    /// during an import ours is the branch as it stands and theirs is the patch coming in.
    /// </summary>
    public string ShowStage(string worktree, int stage, string path)
    {
        var r = Run(worktree, "show", $":{stage}:{path}");
        return r.Ok ? r.StdOut : "";
    }

    /// <summary>Resolves conflicts by taking one whole side of them. ShowStage says which side is which.</summary>
    public void TakeSide(string worktree, IEnumerable<string> relPaths, bool ours)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        var f = WritePathspecFile(list.Select(p => ":(literal)" + p));
        try
        {
            Run(worktree, ["checkout", ours ? "--ours" : "--theirs", "--pathspec-from-file=" + f, "--pathspec-file-nul"]).EnsureOk();
            Run(worktree, ["add", "--pathspec-from-file=" + f, "--pathspec-file-nul"]).EnsureOk();
        }
        finally { File.Delete(f); }
    }

    public void MarkResolved(string worktree, IEnumerable<string> relPaths) => AddPaths(worktree, relPaths);

    /// <summary>
    /// Which replay this worktree stopped in the middle of, if any. git keeps a rebase and an "am" in
    /// the same rebase-apply folder, and only the "applying" file inside it tells them apart, so that
    /// file is what gets asked. A modern rebase writes rebase-merge instead; the older backend still
    /// writes rebase-apply, and both of those are a rebase.
    /// </summary>
    public Replay ReplayInProgress(string worktree)
    {
        if (File.Exists(GitPath(worktree, Path.Combine("rebase-apply", "applying")))) return Replay.Import;
        foreach (var name in new[] { "rebase-merge", "rebase-apply" })
            if (Directory.Exists(GitPath(worktree, name))) return Replay.Rebase;
        return Replay.None;
    }

    /// <summary>Anything half done is in the way of the same things, so every refusal asks this one question.</summary>
    public bool RebaseInProgress(string worktree) => ReplayInProgress(worktree) != Replay.None;

    /// <summary>
    /// How far a stopped replay got, read out of the folder git left behind. A rebase names the commit
    /// it is on and how many are left; a patch series names its place in the series, which is the
    /// number a person waits to see move. Zeroes and an empty subject when git wrote nothing to read.
    /// </summary>
    public ReplayProgress Progress(string worktree)
    {
        var kind = ReplayInProgress(worktree);
        if (kind == Replay.None) return new ReplayProgress(0, 0, "");
        var dir = Directory.Exists(GitPath(worktree, "rebase-apply")) ? "rebase-apply" : "rebase-merge";
        var series = dir == "rebase-apply";
        return new ReplayProgress(
            Number(worktree, Path.Combine(dir, series ? "next" : "msgnum")),
            Number(worktree, Path.Combine(dir, series ? "last" : "end")),
            StoppedSubject(worktree, dir));
    }

    int Number(string worktree, string name) =>
        int.TryParse(ReadGitFile(worktree, name).Trim(), out var n) ? n : 0;

    /// <summary>
    /// The subject of the commit or patch the replay stopped on. git writes the message it was about to
    /// use beside the counters, under one of a few names depending on the backend; when none of them is
    /// there the numbered patch file still carries the header.
    /// </summary>
    string StoppedSubject(string worktree, string dir)
    {
        foreach (var name in new[] { "msg-clean", "final-commit", "message" })
        {
            var first = ReadGitFile(worktree, Path.Combine(dir, name))
                .Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
            if (first.Length > 0) return first;
        }
        var at = Number(worktree, Path.Combine(dir, "next"));
        if (at <= 0) return "";
        foreach (var line in ReadGitFile(worktree, Path.Combine(dir, at.ToString("D4"))).Split('\n'))
        {
            if (line.StartsWith("Subject: ", StringComparison.Ordinal)) return line[9..].Trim();
            if (line.Trim().Length == 0) break;
        }
        return "";
    }

    string ReadGitFile(string worktree, string name)
    {
        try
        {
            var p = GitPath(worktree, name);
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    public void ResetHard(string worktree, string? target = null)
    {
        if (target == null) Ok(worktree, "reset", "--hard", "-q");
        else Ok(worktree, "reset", "--hard", "-q", target);
    }

    public void SparseSetCone(string worktree, IEnumerable<string> dirs)
    {
        var a = new List<string> { "sparse-checkout", "set", "--cone" };
        a.AddRange(dirs);
        Ok(worktree, a.ToArray());
    }

    public string Editor()
    {
        var r = Run(null, "var", "GIT_EDITOR");
        return r.Ok ? r.StdOut.Trim() : "notepad";
    }

    // ---- reading for the GUI ----

    /// <summary>git status --porcelain: X is the index column, Y the worktree column, "??" is untracked.</summary>
    public List<StatusEntry> StatusEntries(string worktree, bool untracked = true) => Status(worktree, untracked).Entries;

    /// <summary>
    /// The status and the branch it belongs to, from one git process. On a checkout of this size the
    /// status walk is the slow part of every refresh, and asking for the branch name separately paid
    /// for a second walk of the same index.
    /// </summary>
    public StatusSnapshot Status(string worktree, bool untracked = true)
    {
        var r = Ok(worktree, "status", "--porcelain=v1", "-b", "-z", untracked ? "--untracked-files=all" : "--untracked-files=no");
        var tok = r.StdOut.Split('\0');
        var res = new List<StatusEntry>();
        string? branch = null;
        var i = 0;
        // With -b the first record is "## <branch>", or "## <branch>...<upstream> [ahead N]", or
        // "## HEAD (no branch)" when nothing is checked out by name.
        if (tok.Length > 0 && tok[0].StartsWith("## ", StringComparison.Ordinal))
        {
            var head = tok[0][3..];
            var dots = head.IndexOf("...", StringComparison.Ordinal);
            if (dots >= 0) head = head[..dots];
            branch = head.Trim();
            if (branch.StartsWith("HEAD (", StringComparison.Ordinal)) branch = null;
            i = 1;
        }
        while (i < tok.Length)
        {
            var s = tok[i++];
            if (s.Length < 4) break;
            var x = s[0].ToString();
            var y = s[1].ToString();
            var path = PathUtil.Rel(s[3..]);
            string? old = null;
            if (x is "R" or "C") old = PathUtil.Rel(tok[i++]);
            res.Add(new StatusEntry(x, y, path, old));
        }
        return new StatusSnapshot(branch, res);
    }

    /// <summary>The parents and the whole message of HEAD, from one git process instead of two.</summary>
    public HeadSummary HeadSummary(string worktree)
    {
        var r = Run(worktree, "log", "-1", "--format=%P%x1f%B", "HEAD");
        if (!r.Ok) return new HeadSummary("", "");
        var p = r.StdOut.Split('\x1f', 2);
        return new HeadSummary(p[0].Trim(), p.Length > 1 ? p[1].TrimEnd() : "");
    }

    /// <summary>
    /// Lines added and removed per file, without the file contents. This is what the numbers beside
    /// each row of a commit list need, and it is a fraction of the cost of the patch they used to
    /// be counted from: on a monorepo this size that patch is tens of megabytes and these are a few hundred bytes.
    /// </summary>
    public Dictionary<string, DiffStats.Count> NumStat(string worktree, string from, string? to = null)
    {
        var a = new List<string> { "diff", "--numstat", "--no-color", "--no-ext-diff", "-M", from };
        if (to != null) a.Add(to);
        var r = Run(worktree, a);
        var res = new Dictionary<string, DiffStats.Count>(StringComparer.OrdinalIgnoreCase);
        if (!r.Ok) return res;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            // A binary file is reported as "-\t-\tpath"; it has no line counts, so it counts as zero.
            var added = int.TryParse(parts[0], out var na) ? na : 0;
            var removed = int.TryParse(parts[1], out var nr) ? nr : 0;
            // A rename is "old => new", or "dir/{old => new}/rest". The name a row wears is the new one.
            res[PathUtil.Rel(NewSideOf(parts[2]))] = new DiffStats.Count(added, removed);
        }
        return res;
    }

    /// <summary>The path a renamed file has now, out of the shorthand --numstat writes for one.</summary>
    static string NewSideOf(string path)
    {
        var open = path.IndexOf('{');
        var arrow = path.IndexOf(" => ", StringComparison.Ordinal);
        if (arrow < 0) return path;
        if (open < 0 || open > arrow) return path[(arrow + 4)..];
        var close = path.IndexOf('}', arrow);
        if (close < 0) return path[(arrow + 4)..];
        return path[..open] + path[(arrow + 4)..close] + path[(close + 1)..];
    }

    /// <summary>Content of a path at a revision, as text. Empty when the path is not there. NUL bytes mean binary.</summary>
    public string ShowText(string rev, string path) => ShowTextIn(null, rev, path);

    /// <summary>
    /// The same, read where the revision means what the caller thinks it means. HEAD is a different
    /// commit in every worktree, so anything but a sha has to name the folder it is read from.
    /// </summary>
    public string ShowTextIn(string? cwd, string rev, string path)
    {
        var r = Run(cwd, "show", rev + ":" + PathUtil.Rel(path));
        return r.Ok ? r.StdOut : "";
    }

    /// <summary>The content of a path as the index holds it: what a commit would take for it right now.</summary>
    public string ShowIndexText(string worktree, string path)
    {
        var r = Run(worktree, "show", ":" + PathUtil.Rel(path));
        return r.Ok ? r.StdOut : "";
    }

    public string UnifiedDiff(string worktree, string from, string? to, string? path = null)
    {
        var a = new List<string> { "diff", "--no-color", "-M", from };
        if (to != null) a.Add(to);
        if (path != null) { a.Add("--"); a.Add(path); }
        var r = Run(worktree, a);
        return r.Ok ? r.StdOut : r.StdErr;
    }

    // ---- carrying a branch to another machine ----

    /// <summary>
    /// The commits in a range, one patch file each, named so that sorting them by name is the order
    /// they must be replayed in. --keep-subject leaves every subject exactly as its commit has it, so
    /// one that starts with a bracket is not read as a "[PATCH]" tag at the far end. --full-index names
    /// the blob each change starts from, which is what lets the far end merge rather than only apply.
    /// </summary>
    public List<string> FormatPatch(string worktree, string range, string outDir)
    {
        Directory.CreateDirectory(outDir);
        Ok(worktree, "format-patch", "--keep-subject", "--binary", "--full-index", "--no-signature", "-o", outDir, range);
        return Directory.GetFiles(outDir, "*.patch").OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Packs the objects named on stdin into "&lt;baseName&gt;-&lt;hash&gt;.pack", and answers the file it wrote.
    /// git writes the pack itself rather than handing it back down a pipe, because everything this
    /// class reads from a process it reads as UTF-8 text and a pack is not text.
    /// </summary>
    public string PackObjects(IEnumerable<string> ids, string baseName)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) throw new SgException("nothing to pack");
        var r = Run(Store, ["pack-objects", "-q", baseName], Encoding.UTF8.GetBytes(string.Join("\n", list) + "\n")).EnsureOk();
        var hash = r.StdOut.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        var pack = baseName + "-" + hash + ".pack";
        if (hash.Length == 0 || !File.Exists(pack)) throw new SgException("git pack-objects wrote no pack");
        return pack;
    }

    /// <summary>
    /// Reads a pack into the store as loose objects. Nothing points at them, which is the point: they
    /// are the versions a three way merge starts from, and without them git can only apply a patch
    /// whose base it already has, which a machine that never saw this branch does not.
    /// </summary>
    public void UnpackObjects(string packFile) =>
        Run(Store, ["unpack-objects", "-q"], File.ReadAllBytes(packFile)).EnsureOk();

    /// <summary>
    /// Replays a patch series onto the branch that is checked out, merging where the base has moved.
    /// --keep-non-patch is the other half of format-patch --keep-subject. asUser so the commits are
    /// made by whoever is importing; each patch carries its own author and that is kept.
    ///
    /// The whole series goes in one call. git splits it into its own folder before it applies the
    /// first one, so a run that stops in the middle still knows about the patches after it, and
    /// carrying on finishes them. Handing them over one at a time meant a stop was the end of the
    /// series, and the only way out of it was to throw the rest away.
    /// </summary>
    public ProcResult ApplyMailbox(string worktree, IEnumerable<string> patchFiles)
    {
        var args = new List<string> { "am", "--3way", "--keep-non-patch", "--keep-cr", "--quoted-cr=nowarn" };
        args.AddRange(patchFiles);
        return Run(worktree, args, asUser: true);
    }

    /// <summary>
    /// Nothing is staged: the index holds exactly what HEAD holds. During a replay that means the step
    /// has nothing to commit, which is what tells a patch that would not apply at all apart from one
    /// whose conflicts have all been resolved. Both look the same from the conflict list, which is empty.
    /// </summary>
    public bool NothingStaged(string worktree) => Run(worktree, "diff", "--cached", "--quiet").ExitCode == 0;

    /// <summary>The patch a stopped series is standing on, as git kept it. Null when there is none to read.</summary>
    public string? StoppedPatchFile(string worktree)
    {
        var p = GitPath(worktree, Path.Combine("rebase-apply", "patch"));
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// Applies what of a patch still fits and writes the rest beside each file as "&lt;name&gt;.rej".
    /// The way on from a patch git will not take at all: the hunks that land are in the working tree to
    /// read, and the ones that did not are there in full to put in by hand. Nothing is staged by it,
    /// because half a patch is not a thing to commit without looking at it first.
    /// </summary>
    public ProcResult ApplyReject(string worktree, string patchFile) =>
        Run(worktree, ["apply", "--reject", "--verbose", patchFile]);

    /// <summary>
    /// Commits what is staged for the patch that stopped, then goes on to the rest of the series.
    /// asUser for the same reason applying it was: the person importing owns the commit.
    /// </summary>
    public ProcResult ContinueMailbox(string worktree) => Run(worktree, ["am", "--continue"], asUser: true);

    /// <summary>Drops the patch that stopped and goes on with the ones after it.</summary>
    public ProcResult SkipMailbox(string worktree) => Run(worktree, ["am", "--skip"], asUser: true);

    /// <summary>Puts back what a stopped series left behind, all of it, so the branch is usable again.</summary>
    public void AbortMailbox(string worktree) => Run(worktree, "am", "--abort");

    /// <summary>
    /// The blob each of these paths held at a commit, in one cat-file rather than one process each:
    /// a branch that touched a hundred files would otherwise cost a hundred git runs. A path the
    /// commit does not have is left out, which is exactly what a file the branch added looks like.
    /// </summary>
    public List<string> BlobIdsAt(string rev, IEnumerable<string> paths)
    {
        var list = paths.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return new List<string>();
        var stdin = string.Join("\n", list.Select(p => rev + ":" + p)) + "\n";
        var r = Run(Store, ["cat-file", "--batch-check=%(objectname) %(objecttype)"], Encoding.UTF8.GetBytes(stdin));
        if (!r.Ok) return new List<string>();
        var ids = new List<string>();
        foreach (var line in r.StdOut.Split('\n'))
        {
            var parts = line.Trim().Split(' ');
            if (parts.Length == 2 && parts[1] == "blob" && parts[0].Length >= 40) ids.Add(parts[0]);
        }
        return ids;
    }

    /// <summary>The whole patch of one commit, also for a root commit.</summary>
    public string CommitPatch(string sha)
    {
        var r = Run(null, "show", "--format=", "--no-color", "-M", sha);
        return r.Ok ? r.StdOut : r.StdErr;
    }

    public List<LogEntry> Log(string worktree, string range, int limit)
    {
        var r = Run(worktree, "log", "--format=%H%x1f%an%x1f%ad%x1f%s", "--date=short", "-n", limit.ToString(), range);
        if (!r.Ok) return new();
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\x1f'))
            .Where(p => p.Length >= 4)
            .Select(p => new LogEntry(p[0], p[1], p[2], Msg.Subject(p[3])))
            .ToList();
    }

    public CommitDetails Details(string sha)
    {
        var r = Ok(null, "show", "-s", "--format=%H%x1f%an%x1f%ae%x1f%ad%x1f%P%x1f%B", "--date=iso", sha);
        var p = r.StdOut.Split('\x1f');
        return p.Length >= 6
            ? new CommitDetails(p[0].Trim(), p[1], p[2], p[3], p[4].Trim(), Msg.Body(p[5]))
            : new CommitDetails(sha, "", "", "", "", r.StdOut);
    }

    public List<DiffEntry> ShowNameStatus(string sha)
    {
        var r = Ok(null, "show", "--name-status", "-z", "--format=", "-M", "--no-color", sha);
        return ParseNameStatus(r.StdOut);
    }

    static List<DiffEntry> ParseNameStatus(string text)
    {
        var tok = text.Split('\0');
        var res = new List<DiffEntry>();
        var i = 0;
        while (i < tok.Length)
        {
            var s = tok[i++];
            if (s.Length == 0) continue;
            var c = s[0];
            if (c is 'R' or 'C')
            {
                if (i + 1 >= tok.Length) break;
                var old = tok[i++];
                var nw = tok[i++];
                res.Add(new DiffEntry(c, PathUtil.Rel(nw), PathUtil.Rel(old)));
            }
            else if (i < tok.Length) res.Add(new DiffEntry(c, PathUtil.Rel(tok[i++]), null));
        }
        return res;
    }

    // ---- writing for the GUI, under the user's own git identity ----

    public void AddPaths(string worktree, IEnumerable<string> relPaths)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        var f = WritePathspecFile(list.Select(p => ":(literal)" + p));
        try { Run(worktree, ["add", "-A", "--pathspec-from-file=" + f, "--pathspec-file-nul"]).EnsureOk(); }
        finally { File.Delete(f); }
    }

    public string CommitAsUser(string worktree, string message)
    {
        Run(worktree, ["commit", "-q", "-F", "-"], Encoding.UTF8.GetBytes(message), null, asUser: true).EnsureOk();
        return HeadSha(worktree);
    }

    // ---- the index, so a commit can hold part of a file ----

    /// <summary>HEAD against the index: what is staged and would go in a commit as it stands.</summary>
    public string DiffStaged(string worktree, string? path = null) => DiffFor(worktree, path, cached: true);

    /// <summary>The index against the working tree: what is changed and not staged yet.</summary>
    public string DiffUnstaged(string worktree, string? path = null) => DiffFor(worktree, path, cached: false);

    string DiffFor(string worktree, string? path, bool cached)
    {
        // No rename detection: these patches are fed back to git apply, and a rename header makes a
        // patch that only applies as a whole. Three lines of context is what git add --patch uses.
        var a = new List<string> { "diff", "--no-color", "--no-ext-diff", "--no-renames", "-U3" };
        if (cached) a.Add("--cached");
        if (path != null) { a.Add("--"); a.Add(path); }
        var r = Run(worktree, a);
        return r.Ok ? r.StdOut : r.StdErr;
    }

    /// <summary>
    /// Feeds a patch to git apply. cached writes it to the index and leaves the working tree alone, which
    /// is how one block is staged; reverse takes it away again. Throws with git's own words when it will
    /// not apply, which is what happens when the file moved on since the diff was read.
    /// </summary>
    public void ApplyPatch(string worktree, string patch, bool cached, bool reverse)
    {
        if (patch.Length == 0) return;
        if (patch.Contains('\uFFFD'))
            throw new SgException("this file is not UTF-8, so a single block of it cannot be staged without changing the other bytes. Use the whole file instead.");
        var a = new List<string> { "apply", "--recount", "--whitespace=nowarn" };
        if (cached) a.Add("--cached");
        if (reverse) a.Add("--reverse");
        a.Add("-");
        var r = Run(worktree, a, Encoding.UTF8.GetBytes(patch));
        if (!r.Ok)
        {
            var why = (r.StdErr.Trim() + "\n" + r.StdOut.Trim()).Trim();
            throw new SgException("git could not apply the block: " + (why.Length == 0 ? "no reason given" : why.Split('\n')[0]));
        }
    }

    /// <summary>Takes the given paths out of the index, back to what HEAD has. The working tree is untouched.</summary>
    public void Unstage(string worktree, IEnumerable<string> relPaths)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        var f = WritePathspecFile(list.Select(p => ":(literal)" + p));
        try { Run(worktree, ["reset", "-q", "HEAD", "--pathspec-from-file=" + f, "--pathspec-file-nul"]).EnsureOk(); }
        finally { File.Delete(f); }
    }

    /// <summary>
    /// Commits exactly these paths and nothing else. A path with something staged goes in as the index has
    /// it, which is how a commit holds only the blocks that were staged; every other path goes in as the
    /// working tree has it. Whatever is staged for a path that is not named stays staged and out of the
    /// commit. amend replaces HEAD instead of adding a commit under it.
    /// </summary>
    public string CommitPathsAsUser(string worktree, IEnumerable<string> relPaths, IEnumerable<string> stagedPaths, string message, bool amend = false)
    {
        var paths = relPaths.Distinct(StringComparer.Ordinal).ToList();
        var staged = new HashSet<string>(stagedPaths, StringComparer.Ordinal);

        // A commit is built in an index of its own, so what the real one holds for the files nobody
        // picked survives it. It starts as a copy, so a picked file's staged blocks are already in.
        var real = GitPath(worktree, "index");
        var temp = GitPath(worktree, "sg-commit-index");
        try
        {
            File.Copy(real, temp, overwrite: true);
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temp };

            var unpicked = StatusEntries(worktree, untracked: false)
                .Where(e => e.X.Trim().Length > 0 && e.X != "?" && !paths.Contains(e.Path, StringComparer.Ordinal))
                .SelectMany(e => e.OldPath != null ? new[] { e.Path, e.OldPath } : new[] { e.Path })
                .Distinct(StringComparer.Ordinal).ToList();
            if (unpicked.Count > 0) WithPathspec(worktree, unpicked, f =>
                Run(worktree, ["reset", "-q", "HEAD", "--pathspec-from-file=" + f, "--pathspec-file-nul"], null, env).EnsureOk());

            // A picked file with nothing staged goes in as it is on disk. One with staged blocks is
            // already right in the copy, and adding it would drag the rest of the file in with it.
            var whole = paths.Where(p => !staged.Contains(p)).ToList();
            if (whole.Count > 0) WithPathspec(worktree, whole, f =>
                Run(worktree, ["add", "-A", "--pathspec-from-file=" + f, "--pathspec-file-nul"], null, env).EnsureOk());

            var args = new List<string> { "commit", "-q", "--allow-empty", "-F", "-" };
            if (amend) args.Add("--amend");
            Run(worktree, args, Encoding.UTF8.GetBytes(message), env, asUser: true).EnsureOk();
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { /* it goes on the next commit */ }
        }

        // The real index still holds what was staged before the commit. For the files that went in it
        // now says the same as HEAD, so putting those back to HEAD costs nothing and keeps git status right.
        Unstage(worktree, paths);
        return HeadSha(worktree);
    }

    void WithPathspec(string worktree, IEnumerable<string> paths, Action<string> use)
    {
        var f = WritePathspecFile(paths.Select(p => ":(literal)" + p));
        try { use(f); }
        finally { File.Delete(f); }
    }

    /// <summary>Deletes the file and stages the deletion. Used by "delete" on a line of the commit list.</summary>
    public void RemovePaths(string worktree, IEnumerable<string> relPaths)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        WithPathspec(worktree, list, f =>
            Run(worktree, ["rm", "-q", "-f", "-r", "--pathspec-from-file=" + f, "--pathspec-file-nul"]).EnsureOk());
    }

    /// <summary>Throws away changes to tracked paths, staged or not.</summary>
    public void RestoreFromHead(string worktree, IEnumerable<string> relPaths)
    {
        var list = relPaths.ToList();
        if (list.Count == 0) return;
        var f = WritePathspecFile(list.Select(p => ":(literal)" + p));
        try
        {
            Run(worktree, ["reset", "-q", "HEAD", "--pathspec-from-file=" + f, "--pathspec-file-nul"]);
            Run(worktree, ["checkout", "HEAD", "--pathspec-from-file=" + f, "--pathspec-file-nul"]).EnsureOk();
        }
        finally { File.Delete(f); }
    }

    // ---- backups: thin histories and the repository they go to ----

    /// <summary>The tree with nothing in it, written into the store so a commit can point at it.</summary>
    public string EmptyTree() => Run(null, ["mktree"], Array.Empty<byte>()).EnsureOk().StdOut.Trim();

    /// <summary>Who wrote a commit and who committed it, both dates in the form git reads back, and its whole message.</summary>
    public CommitIdentity IdentityOf(string sha)
    {
        var p = Ok(null, "log", "-1", "--format=%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%B", sha).StdOut.Split('\x1f', 7);
        if (p.Length < 7) throw new SgException("cannot read commit " + sha);
        return new CommitIdentity(p[0], p[1], p[2], p[3], p[4], p[5], p[6]);
    }

    /// <summary>
    /// A commit with every field taken from the caller: author, committer and both dates. The same
    /// inputs make the same sha, which is what lets a thin history be built again on another day and
    /// land on the very objects the remote already holds.
    /// </summary>
    public string CommitTreeExact(string tree, string? parent, string message, CommitIdentity who)
    {
        var args = new List<string> { "commit-tree", tree };
        if (parent != null) { args.Add("-p"); args.Add(parent); }
        args.Add("-F"); args.Add("-");
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = who.AuthorName, ["GIT_AUTHOR_EMAIL"] = who.AuthorEmail, ["GIT_AUTHOR_DATE"] = who.AuthorDate,
            ["GIT_COMMITTER_NAME"] = who.CommitterName, ["GIT_COMMITTER_EMAIL"] = who.CommitterEmail, ["GIT_COMMITTER_DATE"] = who.CommitterDate,
        };
        return Run(null, args, Encoding.UTF8.GetBytes(message), env).EnsureOk().StdOut.Trim();
    }

    public bool HasCommit(string sha) => Run(null, "cat-file", "-e", sha + "^{commit}").Ok;

    /// <summary>The commits of a range oldest first, along the first parent only: a merge counts as one commit whose change is its whole diff.</summary>
    public List<string> RevListFirstParent(string range) =>
        Ok(null, "rev-list", "--reverse", "--first-parent", range).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();

    public string? MergeBase(string a, string b)
    {
        var r = Run(null, "merge-base", a, b);
        return r.Ok ? r.StdOut.Trim() : null;
    }

    /// <summary>The blob at each of these paths in a tree-ish, with its mode. Paths the tree does not have are left out.</summary>
    public List<TreeEntry> BlobsAt(string treeish, IEnumerable<string> paths)
    {
        var list = paths.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return new List<TreeEntry>();
        // A handful of paths go on the command line. A branch that touched thousands would blow the line
        // on Windows, so past that the whole tree is listed once and filtered here.
        if (list.Count <= 100) return LsTree(treeish, list, recursive: true).Where(e => e.Type == "blob").ToList();
        var want = new HashSet<string>(list, StringComparer.Ordinal);
        return LsTree(treeish, null, recursive: true).Where(e => e.Type == "blob" && want.Contains(e.Path)).ToList();
    }

    /// <summary>Every ref the repository at a URL advertises, name to sha, in one round trip.</summary>
    public Dictionary<string, string> LsRemote(string url)
    {
        var r = Run(null, "ls-remote", "--refs", url);
        if (!r.Ok) throw new SgException($"cannot reach {url}: {FirstLine(r.StdErr)}");
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab > 0) res[line[(tab + 1)..]] = line[..tab];
        }
        return res;
    }

    /// <summary>
    /// Pushes several refs in one connection, each under its own lease: the sha the remote is expected
    /// to hold, or "" for a ref that must not exist there yet. The answer is per ref, read from
    /// --porcelain, so one branch another machine wrote does not stop the rest. A null source deletes.
    /// </summary>
    public Dictionary<string, PushRefResult> PushRefs(string url, IEnumerable<PushRef> refs, bool force)
    {
        var list = refs.ToList();
        var res = new Dictionary<string, PushRefResult>(StringComparer.Ordinal);
        if (list.Count == 0) return res;
        var args = new List<string> { "push", "--porcelain", "--no-verify" };
        if (force) args.Add("--force");
        else foreach (var p in list) args.Add("--force-with-lease=" + p.Dst + ":" + (p.Lease ?? ""));
        args.Add(url);
        foreach (var p in list) args.Add((p.Src ?? "") + ":" + p.Dst);
        var r = Run(null, args);
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3 || line[1] != '\t') continue;
            var flag = line[0];
            var rest = line[2..];
            var tab = rest.IndexOf('\t');
            var spec = tab < 0 ? rest : rest[..tab];
            var summary = tab < 0 ? "" : rest[(tab + 1)..];
            var colon = spec.LastIndexOf(':');
            var dst = colon < 0 ? spec : spec[(colon + 1)..];
            res[dst] = new PushRefResult(flag != '!', flag, summary);
        }
        if (!r.Ok && res.Count == 0) throw new SgException($"push to {url} failed: {FirstLine(r.StdErr)}");
        foreach (var p in list) res.TryAdd(p.Dst, new PushRefResult(false, '?', FirstLine(r.StdErr)));
        return res;
    }

    /// <summary>Fetches the given refspecs from a URL. Nothing is written to FETCH_HEAD and no tag follows.</summary>
    public void FetchRefs(string url, IEnumerable<string> refspecs)
    {
        var list = refspecs.ToList();
        if (list.Count == 0) return;
        var args = new List<string> { "fetch", "--quiet", "--no-tags", "--no-write-fetch-head", url };
        args.AddRange(list);
        var r = Run(null, args);
        if (!r.Ok) throw new SgException($"fetch from {url} failed: {FirstLine(r.StdErr)}");
    }

    /// <summary>
    /// A three way merge of trees with no worktree: what "theirs" changed against "base", put onto "ours".
    /// The base may be a tree that holds only some of the paths. A path in ours alone is ours untouched,
    /// one absent from the base is an add, and one absent from theirs is a delete - which is exactly the
    /// shape of a thin history's commits, so this is how a backup comes back.
    /// </summary>
    public MergeTreeResult MergeTree(string baseCommit, string ours, string theirs)
    {
        var r = Run(null, "-c", "core.quotePath=false", "merge-tree", "--write-tree", "--name-only", "--merge-base=" + baseCommit, ours, theirs);
        var lines = r.StdOut.Replace("\r", "").Split('\n');
        if (r.ExitCode > 1 || lines.Length == 0 || lines[0].Trim().Length < 40)
            throw new SgException("git merge-tree: " + FirstLine(r.StdErr.Length > 0 ? r.StdErr : r.StdOut));
        var tree = lines[0].Trim();
        var conflicted = new List<string>();
        var messages = new StringBuilder();
        if (r.ExitCode == 1)
        {
            var i = 1;
            for (; i < lines.Length && lines[i].Length > 0; i++) conflicted.Add(lines[i]);
            for (i++; i < lines.Length; i++) if (lines[i].Length > 0) messages.AppendLine(lines[i]);
        }
        return new MergeTreeResult(tree, conflicted, messages.ToString().Trim());
    }

    static string FirstLine(string s)
    {
        var t = s.Trim();
        var nl = t.IndexOf('\n');
        return (nl < 0 ? t : t[..nl]).TrimEnd('\r');
    }
}

public sealed record StatusEntry(string X, string Y, string Path, string? OldPath)
{
    public bool Untracked => X == "?";
    public bool Tracked => !Untracked && X != "!";
    public string Code => Untracked ? "?" : (X.Trim().Length > 0 ? X : Y);

    /// <summary>Something of this file is in the index already, so a commit would take that and not the file on disk.</summary>
    public bool Staged => Tracked && X.Trim().Length > 0;

    /// <summary>The file on disk says something the index does not.</summary>
    public bool HasUnstaged => Untracked || Y.Trim().Length > 0;

    /// <summary>Both columns when both say something, so a list can show "MM" for a file only half staged.</summary>
    public string BothCodes => Staged && Y.Trim().Length > 0 ? X + Y : Code;
}

/// <summary>Who wrote a commit: the name, the address, and the date in the form git reads back.</summary>
public sealed record Author(string Name, string Email, string Date);

/// <summary>The status of a worktree and the branch it is on, read together. Branch is null on a detached HEAD.</summary>
public sealed record StatusSnapshot(string? Branch, List<StatusEntry> Entries);

/// <summary>What HEAD is, in the two facts a commit window asks for: whether it has a parent, and its message.</summary>
public sealed record HeadSummary(string Parents, string Message)
{
    /// <summary>Replacing it would leave a commit behind. The store's root commit would not.</summary>
    public bool HasParent => Parents.Length > 0;
}

public sealed record LogEntry(string Sha, string Author, string Date, string Subject);
public sealed record CommitDetails(string Sha, string Author, string Email, string Date, string Parents, string Body);

/// <summary>Both names, both addresses, both dates, and the message: everything a commit is besides its tree and parents.</summary>
public sealed record CommitIdentity(string AuthorName, string AuthorEmail, string AuthorDate,
    string CommitterName, string CommitterEmail, string CommitterDate, string Body)
{
    public Author Author => new(AuthorName, AuthorEmail, AuthorDate);
}

/// <summary>One ref to push: the sha to send (null deletes), the ref to land on, and the sha the remote must hold now ("" means not exist).</summary>
public sealed record PushRef(string? Src, string Dst, string? Lease);
public sealed record PushRefResult(bool Ok, char Flag, string Summary);

/// <summary>What merge-tree wrote: the tree, and the paths it could not settle, which hold conflict markers in that tree.</summary>
public sealed record MergeTreeResult(string Tree, List<string> Conflicted, string Messages)
{
    public bool Clean => Conflicted.Count == 0;
}
