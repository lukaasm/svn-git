using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// What sg says when a branch name or its folder is already taken. "branch exists: graph" was true and
/// useless: it left the reader to work out whether something was using the name, whether removing it
/// would lose anything, and which of two commands to run. The three states look identical from outside
/// and want three different answers, so each one is asserted here by the words it produces.
/// </summary>
public sealed class NameTakenTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();

    /// <summary>The message from a second branch of the same name, whatever state the first is left in.</summary>
    string Second(string name)
    {
        var e = Assert.Throws<SgException>(() => Ops.Branch(f.Root, name, f.Co));
        return e.Message;
    }

    [Fact]
    public void ABranchSomeoneIsWorkingIn_NamesTheFolderAndOffersToRemoveIt()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "graph", f.Co).Path;

        var msg = Second("graph");

        Assert.Contains("already checked out at", msg);
        Assert.Contains(wt, msg);
        Assert.Contains("sg rm graph", msg);
        // The one thing it must not say, because nothing was left behind here.
        Assert.DoesNotContain("left behind", msg);
    }

    /// <summary>
    /// The state the user hit: the folder went and the branch did not, because the removal stopped
    /// between the two. The message has to say that, and say it without offering to delete work.
    /// </summary>
    [Fact]
    public void ABranchLeftBehindByAHalfRemoval_SaysSo_AndCountsWhatWouldBeLost()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "graph", f.Co).Path;
        f.Root.Git.Config("user.name", "Test");
        f.Root.Git.Config("user.email", "test@localhost");
        File.WriteAllText(Path.Combine(wt, "new.txt"), "work that never reached SVN\n");
        f.Root.Git.AddPaths(wt, new[] { "new.txt" });
        f.Root.Git.CommitAsUser(wt, "one commit that is not in SVN\n");
        // Exactly what a stopped removal leaves: the worktree gone, the branch still here.
        f.Root.Git.WorktreeRemove(wt, force: true);

        var msg = Second("graph");

        Assert.Contains("exists with no worktree", msg);
        Assert.Contains("left behind", msg);
        Assert.Contains("1 commit(s) that never reached SVN", msg);
        Assert.Contains("sg export graph", msg);
        // It must not tell you to remove a branch whose commits are not in SVN.
        Assert.DoesNotContain("Remove it with", msg);
    }

    [Fact]
    public void ABranchLeftBehindWithNothingUnpushed_JustOffersToRemoveIt()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "graph", f.Co).Path;
        f.Root.Git.WorktreeRemove(wt, force: true);

        var msg = Second("graph");

        Assert.Contains("exists with no worktree", msg);
        Assert.Contains("Nothing on it is unpushed", msg);
        Assert.Contains("sg rm graph", msg);
        Assert.DoesNotContain("never reached SVN", msg);
    }

    /// <summary>A folder in the way is a different problem from a branch in the way, and reads as one.</summary>
    [Fact]
    public void AFolderNoWorktreeIsUsing_SaysItWasLeftBehind_NotThatABranchExists()
    {
        f.Setup();
        Directory.CreateDirectory(f.Root.WorktreePathFor("graph"));

        var msg = Second("graph");

        Assert.Contains("folder exists", msg);
        Assert.Contains("no worktree is using it", msg);
        Assert.Contains("left behind", msg);
        Assert.DoesNotContain("branch \"graph\"", msg);
    }

    /// <summary>
    /// A removal that finishes leaves nothing behind, so the same name works again. This is the case the
    /// user expected and the one the other three are measured against.
    /// </summary>
    [Fact]
    public void AfterAWholeRemoval_TheSameNameIsFreeAgain()
    {
        f.Setup();
        Ops.Branch(f.Root, "graph", f.Co);
        Ops.Remove(f.Root, "graph", force: true);

        var again = Ops.Branch(f.Root, "graph", f.Co);

        Assert.Equal("graph", again.Branch);
        Assert.True(Directory.Exists(again.Path));
    }
}
