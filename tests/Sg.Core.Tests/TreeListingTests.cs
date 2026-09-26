using System.Text;

namespace Sg.Core.Tests;

/// <summary>
/// The in-process ls-tree against git's own: for every path set - files, folders with and without a
/// slash, folders on the way to a path, overlapping paths, submodules, links, names that are prefixes
/// of each other, paths that are not there - with -r and without, the same entries in the same order.
/// </summary>
public sealed class TreeListingTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sgl-" + Guid.NewGuid().ToString("N")[..8]);
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

    static readonly string[] Files =
    [
        "a.txt", "a-b.txt", "a/b.txt", "a/bc.txt", "a/b/c.txt", "a/b/d/e.txt", "ab/x.txt", "b",
        "dir with space/file one.txt", "żółć/ünï.txt", "x/y/z/deep.txt", "sub2/keep.txt",
    ];

    (SgRoot Root, string Commit, string Tree) Build()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        string Git(string? stdin, params string[] args) =>
            Proc.Run("git", ["-C", root.StorePath, .. args], null, _log, stdin == null ? null : Encoding.UTF8.GetBytes(stdin)).EnsureOk().StdOut.Trim();
        var entries = new List<(string Mode, string Sha, string Path)>();
        foreach (var f in Files) entries.Add(("100644", Git("content of " + f + "\n", "hash-object", "-w", "--stdin"), f));
        entries.Add(("100755", Git("#!/bin/sh\n", "hash-object", "-w", "--stdin"), "exec.sh"));
        entries.Add(("120000", Git("a.txt", "hash-object", "-w", "--stdin"), "link"));
        // Submodules: a commit this store does not have, at the top and one level down.
        entries.Add(("160000", "1111111111111111111111111111111111111111", "sub"));
        entries.Add(("160000", "2222222222222222222222222222222222222222", "sub2/inner"));
        var index = Path.Combine(_dir, "listing.index");
        var info = string.Concat(entries.Select(e => $"{e.Mode} {e.Sha}\t{e.Path}\n"));
        Proc.Run("git", ["-C", root.StorePath, "update-index", "--add", "--index-info"], null, _log, Encoding.UTF8.GetBytes(info),
            new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index }).EnsureOk();
        var tree = Proc.Run("git", ["-C", root.StorePath, "write-tree"], null, _log, null,
            new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index }).EnsureOk().StdOut.Trim();
        var commit = Git("listing\n", "commit-tree", tree, "-F", "-");
        Git(null, "update-ref", "refs/heads/listing", commit);
        return (root, commit, tree);
    }

    static readonly string[][] PathSets =
    [
        [], ["a.txt"], ["a"], ["a/"], ["a/b"], ["a/b/"], ["a/b/c.txt"], ["a/b/d"], ["a/b/d/"], ["a/b/c"], ["a/bc.txt"],
        ["ab"], ["ab/"], ["b"], ["b/"], ["nope"], ["a/nope"], ["a/b/c.txt/x"], ["a", "a/b"], ["a/b", "a"], ["a/", "a/b/"],
        ["a/b/c.txt", "a/bc.txt", "x/y/z/deep.txt"], ["sub"], ["sub/"], ["sub/x"], ["sub2"], ["sub2/"], ["sub2/inner"],
        ["sub2/inner/"], ["link"], ["link/"], ["exec.sh"], ["dir with space"], ["dir with space/"], ["żółć/"],
        ["żółć/ünï.txt"], ["x/y"], ["x/y/"], ["x/y/z/deep.txt"], ["a-b.txt", "a.txt", "a"],
    ];

    [Fact]
    public void Every_path_set_lists_what_git_lists()
    {
        var (root, commit, tree) = Build();
        using var reader = new GitReader("git", root.StorePath, new Dictionary<string, string>(), _log);
        (string, byte[])? Read(string rev) =>
            reader.TryContents(rev, out var header, out var data) && header is { Type: "tree" } h && data != null ? (h.Oid, data) : null;
        var compared = 0;
        var entries = 0;
        foreach (var treeish in new[] { commit, tree, "refs/heads/listing" })
            foreach (var paths in PathSets)
                foreach (var recursive in new[] { false, true })
                {
                    var git = root.Git.LsTreeByGit(treeish, paths.Length == 0 ? null : paths, recursive);
                    var here = TreeListing.List(Read, treeish, paths.Length == 0 ? null : paths, recursive);
                    Assert.NotNull(here);
                    Assert.True(git.SequenceEqual(here!),
                        $"ls-tree{(recursive ? " -r" : "")} {treeish} -- {string.Join(" ", paths)}\ngit:\n{string.Join("\n", git)}\nhere:\n{string.Join("\n", here!)}");
                    compared++;
                    entries += git.Count;
                }
        Assert.Equal(3 * PathSets.Length * 2, compared);
        // Not a match of empty lists: the listings hold real entries.
        Assert.True(entries > 300, entries + " entries compared");
    }

    [Fact]
    public void What_git_would_refuse_or_read_as_magic_goes_to_git()
    {
        var (root, commit, _) = Build();
        using var reader = new GitReader("git", root.StorePath, new Dictionary<string, string>(), _log);
        (string, byte[])? Read(string rev) =>
            reader.TryContents(rev, out var header, out var data) && header is { Type: "tree" } h && data != null ? (h.Oid, data) : null;
        foreach (var odd in new[] { "", ".", "a/../b", ":(glob)a", "/a", "a//b", "a\\b" })
            Assert.Null(TreeListing.List(Read, commit, [odd], recursive: false));
        Assert.Null(TreeListing.List(Read, "refs/heads/none", null, recursive: false));
        Assert.Throws<SgException>(() => root.Git.LsTree("refs/heads/none"));
    }

    [Fact]
    public void An_operation_lists_trees_on_its_reader()
    {
        var (root, commit, _) = Build();
        List<TreeEntry> here;
        using (root.Lock()) here = root.Git.LsTree(commit, ["a/"], recursive: true);
        Assert.Equal(root.Git.LsTree(commit, ["a/"], recursive: true), here);
        Assert.Single(_log.Lines, l => l.Contains("--batch-command < ls-tree -z -r " + commit + " -- a/"));
        // Rebuilding a tree around one entry reads its listings the same way, and makes the same tree.
        string replaced;
        using (root.Lock()) replaced = root.Git.ReplaceEntry(commit + "^{tree}", "a/b/c.txt", null, null);
        Assert.Equal(root.Git.ReplaceEntry(commit + "^{tree}", "a/b/c.txt", null, null), replaced);
    }
}
