using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Several git commits going to SVN as more than one commit, each under its own message. Batches are
/// contiguous and applied oldest first, because SVN history is a line.
/// </summary>
public sealed class PushBatchTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();

    static string Read(string dir, string rel) =>
        File.ReadAllText(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar))).Trim();

    /// <summary>Three commits then three, as two SVN commits in the root, each with its own message.</summary>
    [Fact]
    public void TwoBatches_MakeTwoSvnCommits_WithTheirOwnMessages()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-batch", f.Co).Path;

        Fixture.Put(wt, "fort/dev/one.cpp", "one\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "first");
        Fixture.Put(wt, "fort/dev/two.cpp", "two\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "second");
        var half = git.HeadSha(wt);

        Fixture.Put(wt, "fort/dev/three.cpp", "three\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "third");
        var tip = git.HeadSha(wt);

        var before = f.Root.Svn.Info(f.Checkout, "fort/dev").Revision;

        var r = Push.Run(f.Root, wt, [
            new PushBatch(half, "the first two files"),
            new PushBatch(tip, "the third file"),
        ], null, interactive: true);

        Assert.True(r.AllCommitted, string.Join("\n", r.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.Equal(2, r.Batches.Count);
        Assert.All(r.Batches, b => Assert.True(b.AllCommitted));

        // Two SVN commits in the game repository, not one.
        var revisions = r.Batches.SelectMany(b => b.Groups).Select(g => g.Revision).Distinct().ToList();
        Assert.Equal(2, revisions.Count);

        var log = f.Root.Svn.Log(f.Checkout, "fort/dev", before + 1, f.Root.Svn.Info(f.Checkout, "fort/dev").Revision, 10);
        Assert.Equal(["the first two files", "the third file"], log.Select(e => e.Message.Trim()));

        // All three files are in SVN, and the branch is back on the snapshot.
        Assert.Equal("one", f.Cat(f.GameUrl + "/branches/fort/dev/one.cpp").Trim());
        Assert.Equal("two", f.Cat(f.GameUrl + "/branches/fort/dev/two.cpp").Trim());
        Assert.Equal("three", f.Cat(f.GameUrl + "/branches/fort/dev/three.cpp").Trim());
        Assert.Equal(0, git.CountCommits(f.Root.SnapshotRef(f.Co), git.HeadSha(wt)));
    }

    /// <summary>A batch that stops short of the tip sends its half and leaves the rest on the branch.</summary>
    [Fact]
    public void BatchThroughAnEarlierCommit_LeavesTheRestOnTheBranch()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-through", f.Co).Path;

        Fixture.Put(wt, "fort/dev/early.cpp", "early\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "early");
        var half = git.HeadSha(wt);
        Fixture.Put(wt, "fort/dev/late.cpp", "late\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "late");

        var r = Push.Run(f.Root, wt, [new PushBatch(half, "only the early half")], null, interactive: true);

        Assert.False(r.AllCommitted);          // the branch still holds something
        Assert.Single(r.Batches);
        Assert.True(r.Batches[0].AllCommitted);
        Assert.Equal("early", f.Cat(f.GameUrl + "/branches/fort/dev/early.cpp").Trim());
        Assert.False(f.Root.Svn.UrlExists(f.GameUrl + "/branches/fort/dev/late.cpp"));

        // What did not go is still its own commit, replayed onto the new snapshot, and the file is
        // still in the worktree. Only a push that failed half way flattens the rest into one commit.
        Assert.Equal("late", git.Subject(git.HeadSha(wt)));
        Assert.Equal(1, git.CountCommits(f.Root.SnapshotRef(f.Co), git.HeadSha(wt)));
        Assert.Equal("late", Read(wt, "fort/dev/late.cpp"));
        Assert.True(git.IsClean(wt));

        // Pushing again sends the rest.
        var r2 = Push.Run(f.Root, wt, "and now the late half", interactive: true);
        Assert.True(r2.AllCommitted, string.Join("\n", r2.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.Equal("late", f.Cat(f.GameUrl + "/branches/fort/dev/late.cpp").Trim());
    }

    /// <summary>
    /// The second batch fails. The first stays in SVN, and everything from the failed batch onward
    /// comes back onto the branch.
    /// </summary>
    [Fact]
    public void AFailedSecondBatch_KeepsTheFirst_AndReturnsTheRest()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-halt", f.Co).Path;

        Fixture.Put(wt, "fort/dev/ok.cpp", "ok\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "the good one");
        var half = git.HeadSha(wt);
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 9;\n");
        git.Ok(wt, "commit", "-q", "-am", "the rejected one");
        var tip = git.HeadSha(wt);

        f.SetPreCommitHook("engine", reject: true);
        var r = Push.Run(f.Root, wt, [
            new PushBatch(half, "the good batch"),
            new PushBatch(tip, "the batch that is refused"),
        ], null, interactive: true);

        Assert.False(r.AllCommitted);
        Assert.True(r.Batches[0].AllCommitted);
        Assert.False(r.Batches[1].AllCommitted);
        Assert.Equal("ok", f.Cat(f.GameUrl + "/branches/fort/dev/ok.cpp").Trim());
        Assert.Equal("int engine = 1;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
        Assert.Empty(f.CheckoutChanges());

        Assert.StartsWith("not pushed yet", git.Subject(git.HeadSha(wt)));
        Assert.Equal("int engine = 9;", Read(wt, "schmetterling/engine.cpp"));
        Assert.True(git.IsClean(wt));

        f.SetPreCommitHook("engine", reject: false);
        var r2 = Push.Run(f.Root, wt, "the engine change, second try", interactive: true);
        Assert.True(r2.AllCommitted, string.Join("\n", r2.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.Equal("int engine = 9;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
    }

    [Fact]
    public void BatchesOutOfOrderOrOffTheBranch_AreRefusedBeforeAnythingIsSent()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-bad", f.Co).Path;
        Fixture.Put(wt, "fort/dev/a.cpp", "a\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "a");
        var first = git.HeadSha(wt);
        Fixture.Put(wt, "fort/dev/b.cpp", "b\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "b");
        var tip = git.HeadSha(wt);

        // Newest first is the wrong way round.
        var back = Assert.Throws<SgException>(() => Push.Run(f.Root, wt,
            [new PushBatch(tip, "later first"), new PushBatch(first, "earlier second")], null, interactive: true));
        Assert.Contains("does not come after", back.Message);

        // A commit that is not on this branch at all.
        var stranger = git.RefSha(f.Root.SnapshotRef(f.Co))!;
        var off = Assert.Throws<SgException>(() => Push.Run(f.Root, wt,
            [new PushBatch(stranger, "the snapshot is not a batch"), new PushBatch(tip, "the rest")], null, interactive: true));
        Assert.Contains("does not come after", off.Message);

        // A message the server would refuse, named by which batch it belongs to.
        var short_ = Assert.Throws<SgException>(() => Push.Run(f.Root, wt,
            [new PushBatch(first, "no"), new PushBatch(tip, "a long enough message")], null, interactive: true));
        Assert.Contains("batch 1", short_.Message);

        // Nothing reached SVN through any of that.
        Assert.False(f.Root.Svn.UrlExists(f.GameUrl + "/branches/fort/dev/a.cpp"));
    }
}
