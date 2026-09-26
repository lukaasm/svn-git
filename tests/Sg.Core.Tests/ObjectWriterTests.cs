using System.Text;

namespace Sg.Core.Tests;

/// <summary>
/// Blobs, trees and commits written in-process against git's own: the same ids for the same inputs,
/// objects git reads back as written, and a store `git fsck --strict` passes. Whatever git would write
/// differently - a signed commit, a missing object - still goes to git.
/// </summary>
public sealed class ObjectWriterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sgo-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>A new store, with what making it ran taken out of the log: the tests count only their own.</summary>
    SgRoot Root()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        _log.Clear();
        return root;
    }

    static ProcResult Git(SgRoot root, byte[]? stdin, params string[] args) =>
        Proc.Run("git", ["-C", root.StorePath, .. args], null, new CollectingLog(), stdin);

    static string Out(SgRoot root, byte[]? stdin, params string[] args) => Git(root, stdin, args).EnsureOk().StdOut.Trim();

    [Fact]
    public void Blobs_have_git_s_ids_and_read_back_as_written()
    {
        var root = Root();
        var big = new byte[1 << 20];
        new Random(7).NextBytes(big);
        foreach (var content in new[]
        {
            Array.Empty<byte>(), Encoding.UTF8.GetBytes("line\n"), Encoding.UTF8.GetBytes("no newline"),
            Encoding.UTF8.GetBytes("crlf\r\nlines\r\n"), Encoding.UTF8.GetBytes("zażółć gęślą jaźń\n"), [0, 1, 2, 0, 255], big,
        })
        {
            var here = root.Git.HashBlob(content);
            Assert.Equal(Out(root, content, "hash-object", "--stdin"), here);
            Assert.Equal(content, ReadBlob(root, here));
        }
        Assert.Equal(0, Git(root, null, "fsck", "--strict", "--no-dangling").ExitCode);
    }

    byte[] ReadBlob(SgRoot root, string sha)
    {
        var file = Path.Combine(_dir, "blob-" + sha);
        Proc.Run("git", ["-C", root.StorePath, "cat-file", "blob", sha], null, new CollectingLog(), null, null, file).EnsureOk();
        return File.ReadAllBytes(file);
    }

    [Fact]
    public void Trees_have_git_s_ids_in_git_s_order()
    {
        var root = Root();
        string Blob(string s) => root.Git.HashBlob(Encoding.UTF8.GetBytes(s));
        var sub = root.Git.MakeTree([new("100644", "blob", Blob("inside"), "file.txt")]);
        // Names that sort differently as files and as folders, a link, an executable and a submodule, given out of order.
        TreeEntry[] entries =
        [
            new("100644", "blob", Blob("z"), "zeta"), new("040000", "tree", sub, "a"), new("100644", "blob", Blob("a.txt"), "a.txt"),
            new("100644", "blob", Blob("a-b"), "a-b"), new("100755", "blob", Blob("#!"), "run.sh"), new("120000", "blob", Blob("a.txt"), "link"),
            new("160000", "commit", "1111111111111111111111111111111111111111", "sub"), new("100644", "blob", Blob("ü"), "ünï.txt"),
            new("040000", "tree", sub, "a0"), new("100644", "blob", Blob("ab"), "ab"), new("100644", "blob", Blob("space"), "with space"),
        ];
        string GitTree(IEnumerable<TreeEntry> list, bool missing = false) =>
            Out(root, Encoding.UTF8.GetBytes(string.Concat(list.Select(e => $"{e.Mode} {e.Type} {e.Sha}\t{e.Path}\0"))),
                missing ? ["mktree", "-z", "--missing"] : ["mktree", "-z"]);

        using (root.Lock())
        {
            Assert.Equal(GitTree(entries), root.Git.MakeTree(entries));
            Assert.Equal(GitTree(entries.Reverse()), root.Git.MakeTree(entries.Reverse().ToList()));
            Assert.Equal(GitTree([]), root.Git.EmptyTree());
            // A blob the store does not have: git's --missing takes it, and so does this; without it, git refuses.
            TreeEntry[] absent = [new("100644", "blob", "2222222222222222222222222222222222222222", "gone.txt")];
            Assert.Equal(GitTree(absent, missing: true), root.Git.MakeTree(absent, missing: true));
            Assert.Throws<SgException>(() => root.Git.MakeTree(absent));
            // A type that does not match what the store holds is git's to refuse, too.
            Assert.Throws<SgException>(() => root.Git.MakeTree([new("040000", "tree", Blob("not a tree"), "x")]));
        }
        // Written here, not by git: mktree ran for the folder made outside an operation and the two refusals only.
        Assert.Equal(3, _log.Lines.Count(l => l.StartsWith("cmd: ") && l.Contains(" mktree")));
        Assert.Equal(0, Git(root, null, "fsck", "--strict", "--no-dangling").ExitCode);
    }

    [Fact]
    public void Commits_have_git_s_ids_for_every_identity_date_and_message()
    {
        var root = Root();
        var tree = root.Git.EmptyTree();
        var parent = root.Git.RefSha(SgRoot.RootRef)!;
        var cases = new List<(string Message, CommitIdentity Who)>
        {
            ("plain\n", new("Ann Author", "ann@example.com", "2026-09-26T12:34:56+02:00", "Cid Committer", "cid@example.com", "2026-09-26T12:35:00+02:00", "")),
            ("no newline", new("A", "a@b", "2001-02-03T04:05:06+00:00", "C", "c@d", "2001-02-03T04:05:06+00:00", "")),
            ("crlf\r\nbody\r\n", new("Zoë Łukasz", "zoe@example.com", "2026-01-01T00:00:00-05:30", "Zoë Łukasz", "zoe@example.com", "2026-01-01T00:00:00-05:30", "")),
            ("", new("Empty", "e@x", "2026-09-26T00:00:00+14:00", "Empty", "e@x", "2026-09-26T00:00:00-12:00", "")),
            ("subject\n\nbody line one\nbody line two\n", new("Raw", "raw@x", "1790000000 +0130", "Raw", "raw@x", "1790000001 -0800", "")),
            // Names git cleans up go to git, and come out as git writes them.
            ("crud\n", new("Name.", "x@y", "2026-09-26T12:00:00+02:00", " Spaced ", "x@y", "2026-09-26T12:00:00+02:00", "")),
        };
        foreach (var (message, who) in cases)
            foreach (var p in new[] { null, parent })
            {
                var byGit = root.Git.CommitTreeExactByGit(tree, p, message, who);
                string here;
                using (root.Lock()) here = root.Git.CommitTreeExact(tree, p, message, who);
                Assert.True(byGit == here, $"{message.Replace("\n", "\\n")} / {who}: git {byGit}, here {here}");
            }
        // Written here, not by git: commit-tree ran for git's own side of each case and for the two crud ones.
        Assert.Equal(cases.Count * 2 + 2, _log.Lines.Count(l => l.StartsWith("cmd: ") && l.Contains(" commit-tree ")));
        // A commit given no date is stamped now, by sg, as commit-tree stamps it.
        string made;
        using (root.Lock()) made = root.Git.CommitTree(tree, parent, "now\n");
        var raw = Out(root, null, "cat-file", "-p", made).Split('\n');
        Assert.Equal("tree " + tree, raw[0]);
        Assert.Equal("parent " + parent, raw[1]);
        Assert.Matches(@"^author sg <sg@localhost> \d+ [+-]\d{4}$", raw[2]);
        Assert.Equal(raw[2]["author ".Length..], raw[3]["committer ".Length..]);
        Assert.Equal(0, Git(root, null, "fsck", "--strict", "--no-dangling").ExitCode);
    }

    [Fact]
    public void Signing_settings_change_nothing_and_another_encoding_goes_to_git()
    {
        var root = Root();
        var tree = root.Git.EmptyTree();
        CommitIdentity who = new("A", "a@b", "2026-09-26T12:00:00+02:00", "A", "a@b", "2026-09-26T12:00:00+02:00", "");
        // commit-tree signs only when told -S: with commit.gpgSign on and a signer that always fails, git
        // writes the same unsigned commit, and so does this.
        Out(root, null, "config", "commit.gpgSign", "true");
        Out(root, null, "config", "gpg.program", "false");
        string here;
        using (root.Lock()) here = root.Git.CommitTreeExact(tree, null, "unsigned\n", who);
        Assert.Equal(root.Git.CommitTreeExactByGit(tree, null, "unsigned\n", who), here);
        // Another commit encoding puts a header in the commit: that one git writes.
        Out(root, null, "config", "i18n.commitEncoding", "ISO-8859-2");
        using (root.Lock()) here = root.Git.CommitTreeExact(tree, null, "encoded\n", who);
        Assert.Contains("encoding ISO-8859-2", Out(root, null, "cat-file", "-p", here));
        Assert.Equal(root.Git.CommitTreeExactByGit(tree, null, "encoded\n", who), here);
    }
}
