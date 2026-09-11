using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// A push that stops before the server: the change is written into the checkout and left there, to
/// read and commit by hand. Nothing reaches SVN and the branch does not move.
/// </summary>
public sealed class PushApplyTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();

    string _wt = "";

    void Branch(string name = "feature-apply")
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, name, f.Co).Path;
        Fixture.Put(_wt, "fort/dev/added.cpp", "brand new\n");
        Fixture.Put(_wt, "fort/dev/game.cpp", "int game = 2;\n");
        f.Root.Git.Ok(_wt, "add", "-A");
        f.Root.Git.Ok(_wt, "commit", "-q", "-m", "one added file and one changed");
    }

    static string Read(string dir, string rel) =>
        File.ReadAllText(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar))).Trim();

    int OnBranch() => f.Root.Git.CountCommits(f.Root.SnapshotRef(f.Co), f.Root.Git.HeadSha(_wt));

    [Fact]
    public void The_change_lands_in_the_checkout_and_nothing_is_committed()
    {
        Branch();
        var before = f.Root.Svn.Info(f.Checkout, "fort/dev").Revision;

        var r = Push.Run(f.Root, _wt, null, interactive: true, finish: PushFinish.LeaveInCheckout);

        Assert.True(r.AppliedOnly);
        Assert.False(r.AllCommitted);
        Assert.All(r.Groups, g => Assert.Equal("applied", g.State));

        // The files are in the checkout, with the branch's content.
        Assert.Equal("brand new", Read(f.Checkout, "fort/dev/added.cpp"));
        Assert.Equal("int game = 2;", Read(f.Checkout, "fort/dev/game.cpp"));

        // The server has not moved, and neither has the branch.
        Assert.Equal(before, f.Root.Svn.Info(f.Checkout, "fort/dev").Revision);
        Assert.False(f.Root.Svn.UrlExists(f.GameUrl + "/branches/fort/dev/added.cpp"));
        Assert.Equal(1, OnBranch());
    }

    [Fact]
    public void What_it_leaves_behind_is_what_the_changes_window_lists()
    {
        Branch();
        Push.Run(f.Root, _wt, null, interactive: true, finish: PushFinish.LeaveInCheckout);

        var changes = Ops.CheckoutChanges(f.Root, f.Co);
        Assert.Contains(changes, c => c.Path.Replace('\\', '/') == "fort/dev/added.cpp" && c.Item == "added");
        Assert.Contains(changes, c => c.Path.Replace('\\', '/') == "fort/dev/game.cpp" && c.Item == "modified");
    }

    [Fact]
    public void Committing_them_by_hand_afterwards_puts_them_in_svn()
    {
        Branch();
        Push.Run(f.Root, _wt, null, interactive: true, finish: PushFinish.LeaveInCheckout);

        Ops.SvnCommit(f.Root, f.Co, new List<string> { "fort/dev" }, "sent by hand from the checkout");

        Assert.Equal("brand new", f.Cat(f.GameUrl + "/branches/fort/dev/added.cpp").Trim());
        Assert.Equal("int game = 2;", f.Cat(f.GameUrl + "/branches/fort/dev/game.cpp").Trim());
        Assert.Empty(f.CheckoutChanges());
    }

    [Fact]
    public void It_asks_for_no_message()
    {
        Branch();
        // A commit would refuse this: the minimum message length is 10. Nothing is committed here.
        var r = Push.Run(f.Root, _wt, "x", interactive: true, finish: PushFinish.LeaveInCheckout);
        Assert.True(r.AppliedOnly);
        Assert.Equal("", r.Message);
    }

    [Fact]
    public void A_second_apply_is_refused_while_the_first_is_still_sitting_there()
    {
        Branch();
        Push.Run(f.Root, _wt, null, interactive: true, finish: PushFinish.LeaveInCheckout);

        // The rule that stops the two ways of sending the same work from writing over each other.
        var ex = Assert.Throws<SgException>(() =>
            Push.Run(f.Root, _wt, null, interactive: true, finish: PushFinish.LeaveInCheckout));
        Assert.Contains("local edit in the checkout", ex.Message);
    }

    [Fact]
    public void Reverting_what_it_left_puts_the_checkout_back()
    {
        Branch();
        Push.Run(f.Root, _wt, null, interactive: true, finish: PushFinish.LeaveInCheckout);

        Ops.SvnRevert(f.Root, f.Co, new List<string> { "fort/dev" }, deleteUnversioned: true);

        Assert.Empty(f.CheckoutChanges());
        Assert.Equal("int game = 1;", Read(f.Checkout, "fort/dev/game.cpp"));
        // And the push is available again, as if the apply had never run.
        Assert.Equal(1, OnBranch());
    }

    [Fact]
    public void Only_the_first_commits_are_applied_when_the_push_is_narrowed()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature-apply-scope", f.Co).Path;
        Fixture.Put(_wt, "fort/dev/one.cpp", "one\n");
        f.Root.Git.Ok(_wt, "add", "-A");
        f.Root.Git.Ok(_wt, "commit", "-q", "-m", "the first");
        Fixture.Put(_wt, "fort/dev/two.cpp", "two\n");
        f.Root.Git.Ok(_wt, "add", "-A");
        f.Root.Git.Ok(_wt, "commit", "-q", "-m", "the second");

        Push.Run(f.Root, _wt, null, interactive: true, scope: PushScope.First(1), finish: PushFinish.LeaveInCheckout);

        Assert.True(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "one.cpp")));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "two.cpp")));
        // Neither commit moved: the branch is untouched by an apply.
        Assert.Equal(2, OnBranch());
    }

    [Fact]
    public void A_worktree_with_uncommitted_changes_is_refused_the_same_way()
    {
        Branch();
        File.WriteAllText(Path.Combine(_wt, "fort", "dev", "game.cpp"), "int game = 3;\n");
        var ex = Assert.Throws<SgException>(() =>
            Push.Run(f.Root, _wt, null, interactive: true, finish: PushFinish.LeaveInCheckout));
        Assert.Contains("uncommitted changes", ex.Message);
        Assert.Empty(f.CheckoutChanges());
    }
}
