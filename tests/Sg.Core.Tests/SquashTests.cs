using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Joining a run of the branch's own commits into one, and giving one a new message. Both rewrite
/// history, so the tests care as much about what they refuse as about what they do.
/// </summary>
public sealed class SquashTests : IDisposable
{
    readonly Fixture f = new();
    string _wt = "";

    public void Dispose() => f.Dispose();

    /// <summary>A branch with three commits of its own over the snapshot, oldest first: one, two, three.</summary>
    void Branch()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature", f.Co).Path;
        f.Root.Git.Config("user.name", "Test");
        f.Root.Git.Config("user.email", "test@localhost");
        Commit("one", "a.txt", "first\n");
        Commit("two", "b.txt", "second\n");
        Commit("three", "c.txt", "third\n");
    }

    void Commit(string message, string rel, string text)
    {
        File.WriteAllText(Path.Combine(_wt, rel), text);
        f.Root.Git.AddPaths(_wt, new[] { rel });
        f.Root.Git.CommitAsUser(_wt, message + "\n");
    }

    /// <summary>The branch's own commits, newest first.</summary>
    List<string> Own() => f.Root.Git.RevList(_wt, f.Root.SnapshotRef(f.Co) + "..HEAD");

    List<string> Subjects() => Own().Select(s => f.Root.Git.Subject(s)).ToList();

    static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel));

    [Fact]
    public void Squashing_the_top_two_leaves_one_commit_with_both_changes()
    {
        Branch();
        var own = Own();                       // three, two, one
        var r = Ops.Squash(f.Root, _wt, new[] { own[0], own[1] }, "two and three together");

        Assert.Equal(2, r.Replaced);
        Assert.Equal(new[] { "two and three together", "one" }, Subjects());
        Assert.Equal("second\n", Read(_wt, "b.txt"));
        Assert.Equal("third\n", Read(_wt, "c.txt"));
        Assert.Equal("", f.Root.Git.Out(_wt, "status", "--porcelain"));
        Assert.Equal(r.Sha, f.Root.Git.HeadSha(_wt));
    }

    [Fact]
    public void Squashing_the_bottom_two_replays_the_commit_above_them()
    {
        Branch();
        var own = Own();                       // three, two, one
        Ops.Squash(f.Root, _wt, new[] { own[1], own[2] }, "one and two together");

        Assert.Equal(new[] { "three", "one and two together" }, Subjects());
        Assert.Equal("first\n", Read(_wt, "a.txt"));
        Assert.Equal("second\n", Read(_wt, "b.txt"));
        Assert.Equal("third\n", Read(_wt, "c.txt"));
        Assert.Equal("", f.Root.Git.Out(_wt, "status", "--porcelain"));
    }

    [Fact]
    public void Squashing_the_whole_branch_leaves_one_commit_over_the_snapshot()
    {
        Branch();
        Ops.Squash(f.Root, _wt, Own(), "all of it");
        Assert.Equal(new[] { "all of it" }, Subjects());
        Assert.Equal("first\n", Read(_wt, "a.txt"));
        Assert.Equal("third\n", Read(_wt, "c.txt"));
    }

    [Fact]
    public void The_message_it_starts_from_is_every_message_oldest_first()
    {
        Branch();
        var own = Own();
        Assert.Equal("one\n\ntwo\n\nthree", Ops.SquashMessage(f.Root, _wt, own));
    }

    [Fact]
    public void The_author_of_the_first_commit_of_the_run_is_kept()
    {
        Branch();
        var own = Own();
        var before = f.Root.Git.AuthorOf(own[^1]);
        Ops.Squash(f.Root, _wt, own, "all of it");
        var after = f.Root.Git.AuthorOf(f.Root.Git.HeadSha(_wt));
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.Date, after.Date);
    }

    [Fact]
    public void Rewording_changes_the_message_and_nothing_else()
    {
        Branch();
        var own = Own();
        var tree = f.Root.Git.TreeOf(own[1]);
        Ops.Reword(f.Root, _wt, own[1], "two, said better");

        Assert.Equal(new[] { "three", "two, said better", "one" }, Subjects());
        Assert.Equal(tree, f.Root.Git.TreeOf(Own()[1]));
        Assert.Equal("", f.Root.Git.Out(_wt, "status", "--porcelain"));
    }

    [Fact]
    public void Rewording_the_top_commit_works_the_same()
    {
        Branch();
        Ops.Reword(f.Root, _wt, Own()[0], "three, said better");
        Assert.Equal(new[] { "three, said better", "two", "one" }, Subjects());
    }

    [Fact]
    public void A_run_with_a_gap_in_it_is_refused()
    {
        Branch();
        var own = Own();
        var ex = Assert.Throws<SgException>(() => Ops.Squash(f.Root, _wt, new[] { own[0], own[2] }, "no"));
        Assert.Contains("next to each other", ex.Message);
        Assert.Equal(3, Own().Count);
    }

    [Fact]
    public void A_snapshot_of_svn_is_refused()
    {
        Branch();
        var snapshot = f.Root.Git.RefSha(f.Root.SnapshotRef(f.Co))!;
        var ex = Assert.Throws<SgException>(() => Ops.Squash(f.Root, _wt, new[] { Own()[^1], snapshot }, "no"));
        Assert.Contains("Snapshots of SVN cannot be rewritten", ex.Message);
        Assert.Equal(3, Own().Count);
    }

    [Fact]
    public void A_worktree_with_uncommitted_changes_is_refused()
    {
        Branch();
        File.WriteAllText(Path.Combine(_wt, "a.txt"), "edited\n");
        var ex = Assert.Throws<SgException>(() => Ops.Squash(f.Root, _wt, Own().Take(2).ToList(), "no"));
        Assert.Contains("uncommitted changes", ex.Message);
        Assert.Equal(3, Own().Count);
        Assert.Equal("edited\n", Read(_wt, "a.txt"));
    }

    [Fact]
    public void One_commit_is_not_a_squash()
    {
        Branch();
        Assert.Throws<SgException>(() => Ops.Squash(f.Root, _wt, new[] { Own()[0] }, "no"));
    }

    [Fact]
    public void An_empty_message_is_refused()
    {
        Branch();
        Assert.Throws<SgException>(() => Ops.Squash(f.Root, _wt, Own().Take(2).ToList(), "   "));
        Assert.Equal(3, Own().Count);
    }

    [Fact]
    public void A_squash_leaves_the_branch_where_a_rebase_can_still_find_it()
    {
        Branch();
        Ops.Squash(f.Root, _wt, Own().Take(2).ToList(), "two and three");
        var r = Ops.Rebase(f.Root, _wt);
        Assert.True(r.Ok);
        Assert.Equal(2, r.Ahead);
    }
}
