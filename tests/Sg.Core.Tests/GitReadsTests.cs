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
}
