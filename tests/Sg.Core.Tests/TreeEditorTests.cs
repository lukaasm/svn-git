using System.Text;

namespace Sg.Core.Tests;

/// <summary>
/// Trees edited in-process against git's read-tree, update-index --index-info and write-tree on a
/// private index: the same tree for every edit, and git asked whenever it has to decide.
/// </summary>
public sealed class TreeEditorTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sge-" + Guid.NewGuid().ToString("N")[..8]);
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

    [Fact]
    public void Every_edit_makes_the_tree_git_makes()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        string Blob(string s) => root.Git.HashBlob(Encoding.UTF8.GetBytes(s));
        var git = root.Git;
        // The base: nested folders, a folder with one file, and one whose file has a legacy mode that
        // only mktree writes - the index reads it as 100644, so a folder written again changes id.
        var legacy = git.Run(null, ["mktree"], Encoding.UTF8.GetBytes($"100664 blob {Blob("legacy")}\tlegacy.txt\n")).EnsureOk().StdOut.Trim();
        var legacy2 = git.Run(null, ["mktree"], Encoding.UTF8.GetBytes($"100664 blob {Blob("legacy2")}\tlegacy.txt\n")).EnsureOk().StdOut.Trim();
        var baseTree = git.EditTree(null, [
            ("100644", Blob("a"), "a.txt"), ("100644", Blob("c"), "a/b/c.txt"), ("100644", Blob("e"), "a/b/d/e.txt"),
            ("100644", Blob("x"), "ab/x.txt"), ("100755", Blob("run"), "run.sh"), ("100644", Blob("z"), "zeta/z.txt"),
        ]);
        baseTree = git.ReplaceEntry(baseTree, "zeta", "040000", legacy);
        baseTree = git.MakeTree([.. git.LsTree(baseTree), new("040000", "tree", legacy2, "old")]);

        var cases = new List<(string Name, (string Mode, string Sha, string Path)[] Edits, string? Base)>
        {
            ("add at the top", [("100644", Blob("new"), "new.txt")], baseTree),
            ("add deep, making folders", [("100644", Blob("deep"), "new/dir/file.txt")], baseTree),
            ("change one", [("100644", Blob("c2"), "a/b/c.txt")], baseTree),
            ("delete one", [("0", new string('0', 40), "a/b/c.txt")], baseTree),
            ("delete the last in a folder", [("0", new string('0', 40), "ab/x.txt")], baseTree),
            ("delete what is not there", [("0", new string('0', 40), "nope.txt")], baseTree),
            ("delete a folder by name, which is not a file", [("0", new string('0', 40), "a/b")], baseTree),
            ("file where a folder was", [("100644", Blob("flat"), "a")], baseTree),
            ("folder where a file was", [("100644", Blob("in"), "a.txt/inner.txt")], baseTree),
            ("submodule and link", [("160000", new string('1', 40), "sub"), ("120000", Blob("a.txt"), "link")], baseTree),
            ("exec bit", [("100755", Blob("a"), "a.txt")], baseTree),
            ("delete then put back", [("0", new string('0', 40), "a.txt"), ("100644", Blob("back"), "a.txt")], baseTree),
            ("last one wins", [("100644", Blob("one"), "twice.txt"), ("100644", Blob("two"), "twice.txt")], baseTree),
            ("names", [("100644", Blob("pl"), "żółć/nowy plik.txt")], baseTree),
            ("into a legacy folder", [("100644", Blob("o"), "zeta/other.txt")], baseTree),
            ("next to a legacy folder", [("100644", Blob("n"), "neighbour.txt")], baseTree),
            ("nothing", [], baseTree),
            ("everything out", [
                ("0", new string('0', 40), "a.txt"), ("0", new string('0', 40), "a/b/c.txt"), ("0", new string('0', 40), "a/b/d/e.txt"),
                ("0", new string('0', 40), "ab/x.txt"), ("0", new string('0', 40), "run.sh"), ("0", new string('0', 40), "zeta/legacy.txt"),
                ("0", new string('0', 40), "old/legacy.txt")], baseTree),
            ("from nothing", [("100644", Blob("1"), "one.txt"), ("100644", Blob("2"), "d/two.txt")], null),
            ("nothing from nothing", [], null),
        };
        foreach (var (name, edits, from) in cases)
        {
            var byGit = git.EditTree(from, edits);
            _log.Clear();
            string here;
            using (root.Lock()) here = git.EditTree(from, edits);
            Assert.True(byGit == here, $"{name}: git {byGit}, here {here}");
            Assert.DoesNotContain(_log.Lines, l => l.StartsWith("cmd: ") && (l.Contains(" update-index ") || l.Contains(" write-tree")));
        }
        Assert.Equal(0, git.Run(null, "fsck", "--strict", "--no-dangling").ExitCode);
    }

    [Fact]
    public void What_git_has_to_decide_goes_to_git()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        var blob = root.Git.HashBlob("x"u8.ToArray());
        using (root.Lock())
        {
            // An object the store does not have: write-tree refuses it.
            Assert.Throws<SgException>(() => root.Git.EditTree(null, [("100644", new string('2', 40), "gone.txt")]));
            // A path git will not have in an index: update-index says it is ignoring it, and the tree goes without.
            Assert.Equal(EditByGit(root, [("100644", blob, ".git/config")]), root.Git.EditTree(null, [("100644", blob, ".git/config")]));
            // A mode the index reads its own way comes out as git writes it.
            Assert.Equal(EditByGit(root, [("100664", blob, "odd.txt")]), root.Git.EditTree(null, [("100664", blob, "odd.txt")]));
        }
    }

    static string EditByGit(SgRoot root, (string, string, string)[] edits)
    {
        var index = Path.Combine(Path.GetTempPath(), "sge-" + Guid.NewGuid().ToString("N")[..8] + ".index");
        try
        {
            root.Git.UpdateIndexInfo(root.Git.Store, edits, index);
            return root.Git.WriteTree(root.Git.Store, index);
        }
        finally { File.Delete(index); }
    }
}
