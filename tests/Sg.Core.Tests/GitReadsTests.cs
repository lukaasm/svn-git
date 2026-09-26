using System.Text;

namespace Sg.Core.Tests;

/// <summary>
/// Reads an operation answers in-process - the diff between two trees, who wrote a commit and when,
/// the sizes of objects - against git's own answers to the same questions.
/// </summary>
public sealed class GitReadsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sgr-" + Guid.NewGuid().ToString("N")[..8]);
    readonly CollectingLog _log = new();

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch { /* leftovers in temp are acceptable */ }
    }

    static readonly string Zero = new('0', 40);

    [Fact]
    public void Tree_diffs_are_git_s()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        var git = root.Git;
        string Blob(string s) => git.HashBlob(Encoding.UTF8.GetBytes(s));
        var baseTree = git.EditTree(null, [
            ("100644", Blob("a"), "a.txt"), ("100644", Blob("c"), "a/b/c.txt"), ("100644", Blob("e"), "a/b/d/e.txt"),
            ("100644", Blob("x"), "ab/x.txt"), ("100644", Blob("f"), "file"), ("100644", Blob("s"), "same/s.txt"),
            ("100644", Blob("u"), "żółć/ü.txt"), ("100644", Blob("l"), "to-link"), ("100644", Blob("m"), "to-sub"),
        ]);
        string Edit(params (string, string, string)[] edits) => git.EditTree(baseTree, edits);
        var pairs = new List<(string Name, string From, string To)>
        {
            ("identical", baseTree, baseTree),
            ("add", baseTree, Edit(("100644", Blob("n"), "new.txt"))),
            ("add deep", baseTree, Edit(("100644", Blob("n"), "new/deep/n.txt"))),
            ("change", baseTree, Edit(("100644", Blob("c2"), "a/b/c.txt"))),
            ("delete", baseTree, Edit(("0", Zero, "a/b/d/e.txt"))),
            ("exec bit", baseTree, Edit(("100755", Blob("a"), "a.txt"))),
            ("file to link", baseTree, Edit(("120000", Blob("a.txt"), "to-link"))),
            ("file to submodule", baseTree, Edit(("160000", new string('1', 40), "to-sub"))),
            ("file to folder", baseTree, Edit(("100644", Blob("in"), "file/inner.txt"))),
            ("folder to file", baseTree, Edit(("100644", Blob("flat"), "a"))),
            ("names", baseTree, Edit(("100644", Blob("u2"), "żółć/ü.txt"), ("100644", Blob("sp"), "with space/x y.txt"))),
            ("everything", baseTree, git.EmptyTree()),
            ("from nothing", git.EmptyTree(), baseTree),
            ("many", baseTree, Edit(("0", Zero, "a.txt"), ("100644", Blob("q"), "ab/q.txt"), ("100755", Blob("x"), "ab/x.txt"), ("0", Zero, "same/s.txt"))),
        };
        foreach (var (name, from, to) in pairs)
        {
            var byGit = git.DiffNameStatus(git.Store, from, to, renames: false);
            _log.Clear();
            List<DiffEntry> here;
            using (root.Lock()) here = git.DiffNameStatus(git.Store, from, to, renames: false);
            Assert.True(byGit.SequenceEqual(here), $"{name}\ngit:\n{string.Join("\n", byGit)}\nhere:\n{string.Join("\n", here)}");
            Assert.DoesNotContain(_log.Lines, l => l.StartsWith("cmd: ") && l.Contains(" diff --name-status"));
        }
    }

    [Fact]
    public void Commit_identities_are_git_s()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        var git = root.Git;
        var tree = git.EmptyTree();
        string Commit(string message, string name, string email, string date, string? encoding = null)
        {
            var env = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = name, ["GIT_AUTHOR_EMAIL"] = email, ["GIT_AUTHOR_DATE"] = date,
                ["GIT_COMMITTER_NAME"] = "C " + name, ["GIT_COMMITTER_EMAIL"] = "c" + email, ["GIT_COMMITTER_DATE"] = date,
            };
            string[] args = encoding == null ? ["commit-tree", tree, "-F", "-"] : ["-c", "i18n.commitEncoding=" + encoding, "commit-tree", tree, "-F", "-"];
            return git.Run(null, args, Encoding.UTF8.GetBytes(message), env).EnsureOk().StdOut.Trim();
        }
        var commits = new[]
        {
            Commit("plain\n", "Ann", "ann@x", "1790000000 +0000"),
            Commit("minus zero\n", "Bob", "bob@x", "1790000000 -0000"),
            Commit("crlf\r\nbody\r\n", "Zoë Łukasz", "z@x", "1790000000 +0530"),
            Commit("no newline", "Dee", "d@x", "1790000000 -0800"),
            Commit("", "Eve", "e@x", "1000000000 +1400"),
            Commit("subject\n\nbody\n\ntrailer: x\n", "Fay", "f@x", "1790000123 -0330"),
            Commit("latin\n", "Gus", "g@x", "1790000000 +0100", encoding: "ISO-8859-2"),
        };
        _log.Clear();
        foreach (var c in commits)
        {
            CommitIdentity here;
            using (root.Lock()) here = git.IdentityOf(c);
            Assert.Equal(git.IdentityByGit(c), here);
        }
        // Read here, not by git: git log ran for git's own side of each, and for the encoded commit only.
        Assert.Equal(commits.Length + 1, _log.Lines.Count(l => l.StartsWith("cmd: ") && l.Contains(" log -1 ")));

        // The message alone, as %B gives it: read from the commit inside an operation, by git for the encoded one.
        var bodies = commits.Select(git.BodyByGit).ToList();
        _log.Clear();
        using (root.Lock())
            for (var i = 0; i < commits.Length; i++) Assert.Equal(bodies[i], git.Body(commits[i]));
        Assert.Equal(1, _log.Lines.Count(l => l.StartsWith("cmd: ") && l.Contains(" log -1 --format=%B ")));
    }

    [Fact]
    public void Object_sizes_are_git_s()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        var git = root.Git;
        var shas = new[] { git.HashBlob([]), git.HashBlob(new byte[70_000]), git.EmptyTree(), git.RefSha(SgRoot.RootRef)!, new string('4', 40) };
        var byGit = git.ObjectSizes(shas);
        Dictionary<string, long> here;
        using (root.Lock()) here = git.ObjectSizes(shas);
        Assert.Equal(byGit.OrderBy(kv => kv.Key), here.OrderBy(kv => kv.Key));
        Assert.Equal(4, here.Count);
    }

    [Fact]
    public void Counts_against_one_commit_are_rev_list_s()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        var git = root.Git;
        var tree = git.EmptyTree();
        string Commit(string? parent, string message) => git.CommitTree(tree, parent, message);
        var b1 = Commit(null, "b1");
        var b2 = Commit(b1, "b2");
        var b3 = Commit(b2, "b3");
        var ahead = Commit(Commit(b3, "a1"), "a2");
        var diverged = Commit(Commit(b2, "d1"), "d2");
        var refs = new Dictionary<string, string>
        {
            ["refs/heads/ahead"] = ahead,
            ["refs/heads/behind"] = b1,
            ["refs/heads/diverged"] = diverged,
            ["refs/heads/team/merged"] = git.Out(null, "commit-tree", tree, "-p", diverged, "-p", ahead, "-m", "merge"),
            ["refs/heads/equal"] = b3,
            ["refs/heads/unrelated"] = Commit(Commit(null, "u1"), "u2"),
            ["refs/heads/other/deep"] = ahead,
        };
        foreach (var (name, sha) in refs) git.UpdateRef(name, sha);
        // refs/heads/other is no ref, yet as a pattern it matches the one below it, which was not asked for.
        var asked = refs.Keys.Where(k => k != "refs/heads/other/deep").Concat(["refs/heads/other", "refs/heads/missing"]).ToList();
        _log.Clear();
        var here = git.CountBothEach(b3, asked);
        Assert.Single(_log.Lines, l => l.StartsWith("cmd: ") && l.Contains(" for-each-ref "));
        Assert.Equal(asked.Count - 2, here.Count);
        foreach (var name in asked.SkipLast(2))
            Assert.Equal(git.CountBoth(b3, name), here[name]);
        // A name in place of the commit is refused rather than read into the format.
        Assert.Empty(git.CountBothEach("refs/heads/equal", asked));
    }

    [Fact]
    public void The_top_of_a_worktree_is_git_s_and_asked_once()
    {
        var root = Ops.Init(Path.Combine(_dir, "root"), _log, fsmonitor: false);
        var git = root.Git;
        var wt = Path.Combine(_dir, "wt");
        git.Ok(null, "worktree", "add", "-q", "-b", "t", wt, git.RefSha(SgRoot.RootRef)!);
        var sub = Path.Combine(wt, "a", "b");
        Directory.CreateDirectory(sub);
        string ByGit(string cwd) => Path.GetFullPath(Proc.Run("git", ["-C", cwd, "rev-parse", "--show-toplevel"], null, new CollectingLog()).EnsureOk().StdOut.Trim());
        int Asked() => _log.Lines.Count(l => l.StartsWith("cmd: ") && l.Contains(" --show-toplevel"));

        _log.Clear();
        Assert.Equal(ByGit(sub), git.Toplevel(sub));
        Assert.Equal(ByGit(wt), git.Toplevel(wt));
        Assert.Equal(ByGit(sub), git.Toplevel(sub));
        Assert.Equal(ByGit(wt), git.Toplevel(wt + "\\"));
        Assert.Equal(2, Asked());
        // Spelled in another case, the answer is the disk's spelling, which git gives.
        Assert.Equal(ByGit(sub), git.Toplevel(sub.ToLowerInvariant()));

        // A repository made inside the worktree is the top of what is under it, and then it is gone again.
        var nested = Path.Combine(wt, "a");
        Proc.Run("git", ["init", "-q", nested], null, new CollectingLog()).EnsureOk();
        Assert.Equal(ByGit(nested), git.Toplevel(sub));
        Directory.Delete(Path.Combine(nested, ".git"), true);
        Assert.Equal(ByGit(sub), git.Toplevel(sub));

        // A folder gone, and a worktree whose own folder in the store is gone, fail as git fails.
        Directory.Delete(sub);
        Assert.Throws<SgException>(() => git.Toplevel(sub));
        foreach (var admin in Directory.GetDirectories(Path.Combine(git.Store, "worktrees"))) Directory.Delete(admin, true);
        Assert.Throws<SgException>(() => git.Toplevel(wt));

        // A repository of its own is asked every time: its config may name another top.
        var plain = Path.Combine(_dir, "plain");
        Proc.Run("git", ["init", "-q", plain], null, new CollectingLog()).EnsureOk();
        _log.Clear();
        Assert.Equal(ByGit(plain), git.Toplevel(plain));
        Assert.Equal(ByGit(plain), git.Toplevel(plain));
        Assert.Equal(2, Asked());
    }

    [Fact]
    public void Status_lists_untracked_files_as_uall_does()
    {
        var root = Ops.Init(Path.Combine(_dir, "root"), _log, fsmonitor: false);
        var repo = Path.Combine(_dir, "repo");
        void Write(string rel, string text = "x")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, rel))!);
            File.WriteAllText(Path.Combine(repo, rel), text);
        }
        string Git(string cwd, params string[] args) =>
            Proc.Run("git", ["-C", cwd, "-c", "user.name=T", "-c", "user.email=t@x", .. args], null, new CollectingLog()).EnsureOk().StdOut;
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        Write(".gitignore", "*.log\n");
        foreach (var f in new[] { "a.txt", "d.txt", "keep/k.txt", "r.txt" }) Write(f);
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "one");
        // Tracked changes: changed, deleted, staged, renamed.
        Write("a.txt", "changed");
        File.Delete(Path.Combine(repo, "d.txt"));
        Write("s.txt");
        Git(repo, "add", "s.txt");
        Git(repo, "mv", "r.txt", "renamed.txt");
        List<StatusEntry> ByGit()
        {
            var tok = Git(repo, "status", "--porcelain=v1", "-z", "--untracked-files=all").Split('\0');
            var res = new List<StatusEntry>();
            for (var i = 0; i < tok.Length && tok[i].Length >= 4;)
            {
                var s = tok[i++];
                res.Add(new StatusEntry(s[..1], s[1..2], PathUtil.Rel(s[3..]), s[0] is 'R' or 'C' ? PathUtil.Rel(tok[i++]) : null));
            }
            return res;
        }
        void Same(int processes)
        {
            _log.Clear();
            var here = root.Git.Status(repo).Entries;
            var byGit = ByGit();
            Assert.True(byGit.SequenceEqual(here), $"git:\n{string.Join("\n", byGit)}\nhere:\n{string.Join("\n", here)}");
            Assert.Equal(processes, _log.Lines.Count(l => l.StartsWith("cmd: ")));
        }

        // Untracked files in tracked folders only: one status.
        Write("top.txt");
        Write("keep/untracked.txt");
        Same(1);

        // New folders: a deep one, one with an ignored file, one holding only ignored files, an empty one,
        // names git would read as patterns, names around the folder's in sort order, a repository inside
        // the checkout and one inside a new folder.
        Write("new dir/x.txt");
        Write("new dir/deep/y.txt");
        Write("new dir/skip.log");
        Write("only-ignored/z.log");
        Directory.CreateDirectory(Path.Combine(repo, "empty"));
        Write("mixed[1]/f.txt");
        Write("żółć/ü.txt");
        Write("keep/newsub/n.txt");
        Write("a/x.txt");
        Write("a-b.txt");
        Write("a0.txt");
        Write("nested/n.txt");
        Git(Path.Combine(repo, "nested"), "init", "-q");
        Write("wrap/inner/i.txt");
        Git(Path.Combine(repo, "wrap", "inner"), "init", "-q");
        Write("wrap/w.txt");
        Same(2);

        // More new folders than a command line is kept for: -uall itself.
        for (var i = 0; i < 201; i++) Write($"m{i:D3}/f.txt");
        Same(2);
    }
}
