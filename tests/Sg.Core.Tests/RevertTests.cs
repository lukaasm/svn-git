using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Taking a commit's changes back out. Nothing is rewritten: the commit stays and a new one on top
/// undoes it, which is the only undo that is safe once anyone else could have the commit.
/// </summary>
public sealed class RevertTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();

    string _wt = "";

    /// <summary>A branch with three commits, one file each, oldest first: one, two, three.</summary>
    void Branch()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature-revert", f.Co).Path;
        f.Root.Git.Config("user.name", "Test");
        f.Root.Git.Config("user.email", "test@localhost");
        Commit("a.txt", "first\n", "one");
        Commit("b.txt", "second\n", "two");
        Commit("c.txt", "third\n", "three");
    }

    void Commit(string rel, string text, string message)
    {
        File.WriteAllText(Path.Combine(_wt, rel), text);
        f.Root.Git.AddPaths(_wt, new[] { rel });
        f.Root.Git.CommitAsUser(_wt, message + "\n");
    }

    /// <summary>The branch's own commits, newest first.</summary>
    List<string> Own() => f.Root.Git.RevList(_wt, f.Root.SnapshotRef(f.Co) + "..HEAD");

    List<string> Subjects() => Own().Select(s => f.Root.Git.Subject(s)).ToList();

    bool Has(string rel) => File.Exists(Path.Combine(_wt, rel));

    [Fact]
    public void Reverting_one_commit_takes_its_change_out_and_leaves_the_history_alone()
    {
        Branch();
        var own = Own();                       // three, two, one
        var r = Ops.Revert(f.Root, _wt, new[] { own[1] }, "Revert two");

        Assert.Equal(4, Own().Count);          // the commit it undoes is still there, with one more on top
        Assert.Equal("Revert two", f.Root.Git.Subject(r.Sha));
        Assert.Contains("two", Subjects());
        Assert.False(Has("b.txt"));
        Assert.True(Has("a.txt"));
        Assert.True(Has("c.txt"));
        Assert.Equal("", f.Root.Git.Out(_wt, "status", "--porcelain"));
    }

    [Fact]
    public void Reverting_several_commits_makes_one_commit_that_undoes_all_of_them()
    {
        Branch();
        var own = Own();
        var r = Ops.Revert(f.Root, _wt, new[] { own[0], own[1] }, "Revert the last two");

        Assert.Equal(2, r.Replaced);
        Assert.Equal(4, Own().Count);
        Assert.False(Has("b.txt"));
        Assert.False(Has("c.txt"));
        Assert.True(Has("a.txt"));
    }

    [Fact]
    public void The_message_it_starts_from_names_what_it_undoes()
    {
        Branch();
        var own = Own();
        Assert.StartsWith("Revert \"two\"", Ops.RevertMessage(f.Root, _wt, new[] { own[1] }));

        var many = Ops.RevertMessage(f.Root, _wt, new[] { own[0], own[1] });
        Assert.StartsWith("Revert 2 commits", many);
        Assert.Contains("two", many);
        Assert.Contains("three", many);
    }

    [Fact]
    public void A_snapshot_of_svn_is_refused_and_says_where_to_undo_it_instead()
    {
        Branch();
        var snapshot = f.Root.Git.RefSha(f.Root.SnapshotRef(f.Co))!;
        var ex = Assert.Throws<SgException>(() => Ops.Revert(f.Root, _wt, new[] { snapshot }, "no"));
        Assert.Contains("reversing its revision", ex.Message);
        Assert.Equal(3, Own().Count);
    }

    [Fact]
    public void A_worktree_with_uncommitted_changes_is_refused()
    {
        Branch();
        File.WriteAllText(Path.Combine(_wt, "a.txt"), "edited\n");
        var ex = Assert.Throws<SgException>(() => Ops.Revert(f.Root, _wt, new[] { Own()[0] }, "no"));
        Assert.Contains("uncommitted changes", ex.Message);
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(_wt, "a.txt")));
    }

    [Fact]
    public void Reverting_the_same_commit_twice_says_there_is_nothing_left_to_take_out()
    {
        Branch();
        var sha = Own()[0];
        Ops.Revert(f.Root, _wt, new[] { sha }, "Revert three");
        var ex = Assert.Throws<SgException>(() => Ops.Revert(f.Root, _wt, new[] { sha }, "again"));
        Assert.Contains("nothing to take back out", ex.Message);
        // The refusal left nothing half applied.
        Assert.Equal("", f.Root.Git.Out(_wt, "status", "--porcelain"));
    }

    [Fact]
    public void An_empty_message_is_refused()
    {
        Branch();
        Assert.Throws<SgException>(() => Ops.Revert(f.Root, _wt, new[] { Own()[0] }, "  "));
        Assert.Equal(3, Own().Count);
    }

    [Fact]
    public void A_reverted_change_still_pushes_as_the_two_commits_it_is()
    {
        Branch();
        Ops.Revert(f.Root, _wt, new[] { Own()[1] }, "Revert two");
        var r = Push.Run(f.Root, _wt, "the work, without the second bit", interactive: true);

        Assert.True(r.AllCommitted, string.Join("\n", r.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.True(f.Root.Svn.UrlExists(f.MonoUrl + "/trunk/a.txt"));
        Assert.False(f.Root.Svn.UrlExists(f.MonoUrl + "/trunk/b.txt"));
        Assert.True(f.Root.Svn.UrlExists(f.MonoUrl + "/trunk/c.txt"));
    }
}
