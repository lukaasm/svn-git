using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Pushing the oldest few commits of a branch instead of all of it. The boundary is a count rather than
/// a commit, because a push rebases the branch first and that renames every commit on it.
/// </summary>
public sealed class PushScopeTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();

    string _wt = "";

    /// <summary>A branch with three commits, one file each, oldest first: one, two, three.</summary>
    void Branch(string name = "feature-scope")
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, name, f.Co).Path;
        Commit("one", "first");
        Commit("two", "second");
        Commit("three", "third");
    }

    void Commit(string file, string message)
    {
        Fixture.Put(_wt, "fort/dev/" + file + ".cpp", file + "\n");
        f.Root.Git.Ok(_wt, "add", "-A");
        f.Root.Git.Ok(_wt, "commit", "-q", "-m", message);
    }

    bool InSvn(string file)
    {
        try { return f.Cat(f.GameUrl + "/branches/fort/dev/" + file + ".cpp").Trim() == file; }
        catch (SgException) { return false; }
    }

    int OnBranch() => f.Root.Git.CountCommits(f.Root.SnapshotRef(f.Co), f.Root.Git.HeadSha(_wt));

    [Fact]
    public void The_preview_of_the_first_one_names_one_commit_and_one_file()
    {
        Branch();
        var p = Push.Preview(f.Root, _wt, PushScope.First(1));
        Assert.Equal(3, p.Commits.Count);          // the window still shows the whole branch
        Assert.Equal(1, p.Sending);
        Assert.True(p.Partial);
        Assert.Single(p.Entries);
        Assert.Equal("fort/dev/one.cpp", p.Entries.Single().Path);
        Assert.Equal("first", p.DefaultMessage.Trim());
        Assert.NotEqual(p.BranchTip, p.Tip);
    }

    [Fact]
    public void The_preview_of_the_whole_branch_is_what_it_always_was()
    {
        Branch();
        var p = Push.Preview(f.Root, _wt);
        Assert.Equal(3, p.Commits.Count);
        Assert.Equal(3, p.Sending);
        Assert.False(p.Partial);
        Assert.Equal(3, p.Entries.Count());
        Assert.Equal(p.BranchTip, p.Tip);
    }

    [Fact]
    public void Asking_for_more_commits_than_the_branch_has_sends_all_of_them()
    {
        Branch();
        var p = Push.Preview(f.Root, _wt, PushScope.First(9));
        Assert.Equal(3, p.Sending);
        Assert.False(p.Partial);
        Assert.Equal(p.BranchTip, p.Tip);
    }

    [Fact]
    public void Asking_for_none_is_refused()
    {
        Branch();
        Assert.Throws<SgException>(() => Push.Preview(f.Root, _wt, PushScope.First(0)));
    }

    [Fact]
    public void Pushing_the_first_two_sends_two_files_and_leaves_the_third_on_the_branch()
    {
        Branch();
        var r = Push.Run(f.Root, _wt, "the first two", interactive: true, scope: PushScope.First(2));

        Assert.True(r.AllCommitted is false, "a partial push is not the whole branch committed");
        Assert.All(r.Groups, g => Assert.Equal("committed", g.State));
        Assert.True(InSvn("one"));
        Assert.True(InSvn("two"));
        Assert.False(InSvn("three"));

        // What did not go is still its own commit on the branch, over the new snapshot.
        Assert.Equal(1, OnBranch());
        Assert.Equal("1 commit still on the branch", r.BranchState);
        Assert.Equal("third", f.Root.Git.Subject(f.Root.Git.HeadSha(_wt)));
        Assert.True(File.Exists(Path.Combine(_wt, "fort", "dev", "three.cpp")));
    }

    [Fact]
    public void The_rest_goes_on_the_next_push()
    {
        Branch();
        Push.Run(f.Root, _wt, "the first two", interactive: true, scope: PushScope.First(2));
        var second = Push.Run(f.Root, _wt, "and the third one", interactive: true);

        Assert.True(second.AllCommitted, string.Join("\n", second.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.True(InSvn("three"));
        Assert.Equal(0, OnBranch());
    }

    [Fact]
    public void One_commit_at_a_time_gets_the_whole_branch_out_in_order()
    {
        Branch();
        var messages = new[] { "number one", "number two", "number three" };
        foreach (var m in messages) Push.Run(f.Root, _wt, m, interactive: true, scope: PushScope.First(1));

        Assert.Equal(0, OnBranch());
        Assert.True(InSvn("one"));
        Assert.True(InSvn("two"));
        Assert.True(InSvn("three"));

        var head = f.Root.Svn.Info(f.Checkout, "fort/dev").Revision;
        var log = f.Root.Svn.Log(f.Checkout, "fort/dev", head - 2, head, 10);
        Assert.Equal(messages, log.Select(e => e.Message.Trim()));
    }

    [Fact]
    public void The_message_offered_is_the_message_of_the_commits_that_go()
    {
        Branch();
        Assert.Equal("first", Push.Preview(f.Root, _wt, PushScope.First(1)).DefaultMessage.Trim());
        Assert.Contains("second", Push.Preview(f.Root, _wt, PushScope.First(2)).DefaultMessage);
        Assert.DoesNotContain("third", Push.Preview(f.Root, _wt, PushScope.First(2)).DefaultMessage);
    }

    [Fact]
    public void A_partial_push_is_refused_only_for_what_it_actually_sends()
    {
        Branch("feature-collide");
        // The newest commit touches a file the checkout has a local edit on, which push always refuses.
        // A push of the first two does not touch it, so it must go through anyway.
        Fixture.Put(_wt, "fort/dev/game.cpp", "int game = 2;\n");
        f.Root.Git.Ok(_wt, "commit", "-q", "-am", "one that collides with the checkout");
        File.AppendAllText(Path.Combine(f.Checkout, "fort", "dev", "game.cpp"), "// edited in the checkout\n");

        var r = Push.Run(f.Root, _wt, "the first two", interactive: true, scope: PushScope.First(2));
        Assert.All(r.Groups, g => Assert.Equal("committed", g.State));
        Assert.True(InSvn("two"));

        // The whole branch still refuses, and says why.
        var ex = Assert.Throws<SgException>(() => Push.Run(f.Root, _wt, "everything", interactive: true));
        Assert.Contains("local edit in the checkout", ex.Message);
    }

    [Fact]
    public void What_is_left_after_a_partial_push_stays_as_separate_commits()
    {
        Branch();
        Push.Run(f.Root, _wt, "just the first", interactive: true, scope: PushScope.First(1));

        // Two commits left, not one blob: that is what lets the next push take one of them again.
        Assert.Equal(2, OnBranch());
        Assert.Equal(["third", "second"], f.Root.Git.Log(_wt, f.Root.SnapshotRef(f.Co) + "..HEAD", 10).Select(c => c.Subject));
        Assert.True(f.Root.Git.IsClean(_wt));
    }
}
