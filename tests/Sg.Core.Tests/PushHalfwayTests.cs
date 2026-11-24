using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// A push that fails part of the way. What went stays on the server, the commits nothing was sent of stay
/// the commits they were, and what somebody else committed meanwhile is never put back.
/// </summary>
public sealed class PushHalfwayTests : IDisposable
{
    Fixture? _fixture;
    Fixture f => _fixture ??= new();
    public void Dispose() => _fixture?.Dispose();

    string _wt = "";
    Git Git => f.Root.Git;

    void Commit(string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files) Fixture.Put(_wt, path, content);
        Git.Ok(_wt, "add", "-A");
        Git.Ok(_wt, "commit", "-q", "-m", message);
    }

    string Read(string rel) => File.ReadAllText(Path.Combine(_wt, rel.Replace('/', Path.DirectorySeparatorChar))).Trim();

    List<string> OnBranch() => Git.Log(_wt, f.Root.SnapshotRef(f.Co) + "..HEAD", 20).Select(c => c.Subject).ToList();

    [Fact]
    public void A_commit_that_sent_nothing_stays_a_commit_and_so_do_the_ones_above_it()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature-none", f.Co).Path;
        Commit("engine change", ("schmetterling/engine.cpp", "int engine = 2;\n"));
        Commit("second", ("fort/dev/two.cpp", "two\n"));
        Commit("third", ("fort/dev/three.cpp", "three\n"));
        f.SetPreCommitHook("engine", reject: true);

        var r = Push.Run(f.Root, _wt, "only the engine change", interactive: true, scope: PushScope.First(1));

        Assert.False(r.AllCommitted);
        Assert.Equal("failed", r.Groups.Single().State);
        Assert.Equal(["third", "second", "engine change"], OnBranch());
        Assert.Equal("int engine = 2;", Read("schmetterling/engine.cpp"));
        Assert.True(Git.IsClean(_wt));
        Assert.Empty(r.Warnings);

        f.SetPreCommitHook("engine", reject: false);
        Push.Run(f.Root, _wt, "only the engine change", interactive: true, scope: PushScope.First(1));
        Assert.Equal("int engine = 2;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
        Assert.Equal(["third", "second"], OnBranch());
    }

    [Fact]
    public void What_is_left_of_a_half_sent_commit_is_one_commit_under_the_ones_above_it()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature-half", f.Co).Path;
        Commit("engine and game", ("schmetterling/engine.cpp", "int engine = 2;\n"), ("fort/dev/game.cpp", "int game = 2;\n"));
        Commit("second", ("fort/dev/two.cpp", "two\n"), ("schmetterling/engine.cpp", "int engine = 2;\nint two = 2;\n"));
        Commit("third", ("fort/dev/three.cpp", "three\n"));
        f.SetPreCommitHook("engine", reject: true);

        var r = Push.Run(f.Root, _wt, "engine and game change", interactive: true, scope: PushScope.First(1));

        Assert.Equal("committed", r.Groups.Single(g => g.Wc == "fort/dev").State);
        Assert.Equal("failed", r.Groups.Single(g => g.Wc == "schmetterling").State);
        Assert.Equal(["third", "second", "not pushed yet: schmetterling"], OnBranch());
        Assert.Equal(["schmetterling/engine.cpp"], Git.DiffNameStatus(_wt, "HEAD~3", "HEAD~2").Select(e => e.Path));
        Assert.Equal("int engine = 2;\nint two = 2;", Read("schmetterling/engine.cpp").Replace("\r", ""));
        Assert.True(Git.IsClean(_wt));
        Assert.True(Ops.Status(f.Root, checkSvn: false).Worktrees.Single().Pending);
        Assert.True(CheckoutRemoval.Preview(f.Root, f.Co.Name).Branches.Single().HalfPushed);

        f.SetPreCommitHook("engine", reject: false);
        Push.Run(f.Root, _wt, "engine change, second try", interactive: true, scope: PushScope.First(1));
        Assert.Equal("int engine = 2;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
        Assert.Equal(["third", "second"], OnBranch());
        Assert.False(Ops.Status(f.Root, checkSvn: false).Worktrees.Single().Pending);
    }

    [Fact]
    public void Batches_that_were_never_tried_stay_the_commits_they_were()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature-batches", f.Co).Path;
        Commit("one", ("fort/dev/one.cpp", "one\n"));
        var first = Git.HeadSha(_wt);
        Commit("engine change", ("schmetterling/engine.cpp", "int engine = 2;\n"));
        var second = Git.HeadSha(_wt);
        Commit("three", ("fort/dev/three.cpp", "three\n"));
        Commit("four", ("fort/dev/four.cpp", "four\n"));
        f.SetPreCommitHook("engine", reject: true);

        var r = Push.Run(f.Root, _wt, [
            new PushBatch(first, "the first batch"),
            new PushBatch(second, "the refused batch"),
            new PushBatch(Git.HeadSha(_wt), "the last batch"),
        ], null, interactive: true);

        Assert.True(r.Batches[0].AllCommitted);
        Assert.False(r.Batches[2].Attempted);
        Assert.Equal(["four", "three", "engine change"], OnBranch());
    }

    [Fact]
    public void A_commit_somebody_else_made_during_a_push_that_fails_is_not_put_back()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature-race", f.Co).Path;
        Commit("engine and game", ("schmetterling/engine.cpp", "int engine = 2;\n"), ("fort/dev/game.cpp", "int game = 2;\n"));
        Commit("second", ("fort/dev/two.cpp", "two\n"));
        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");

        // The message is asked for after the base was synced and before anything is sent.
        var r = Push.Run(f.Root, _wt, null, interactive: true, editMessage: _ =>
        {
            Fixture.Put(other, "sub/data.txt", "data of somebody else\n");
            f.Svn.Ok(other, "commit", "--non-interactive", "-m", "somebody else was here");
            f.SetPreCommitHook("engine", reject: true);
            return "engine and game change";
        }, scope: PushScope.First(1));

        Assert.Equal("failed", r.Groups.Single(g => g.Wc == "schmetterling").State);
        Assert.Equal("data of somebody else", Read("schmetterling/sub/data.txt"));
        Assert.Equal(["second", "not pushed yet: schmetterling"], OnBranch());
        Assert.Equal(["fort/dev/two.cpp", "schmetterling/engine.cpp"],
            Git.DiffNameStatus(_wt, f.Root.SnapshotRef(f.Co), "HEAD").Select(e => e.Path).Order());
    }

    [Fact]
    public void A_commit_that_stays_and_meets_a_change_in_the_same_lines_stops_on_it_and_overwrites_nothing()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature-lines", f.Co).Path;
        Commit("game", ("fort/dev/game.cpp", "int game = 2;\n"));
        Commit("engine", ("schmetterling/engine.cpp", "int engine = 2;\n"));
        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");

        var r = Push.Run(f.Root, _wt, null, interactive: true, editMessage: _ =>
        {
            Fixture.Put(other, "engine.cpp", "int engine = 9;\n");
            f.Svn.Ok(other, "commit", "--non-interactive", "-m", "somebody else was here");
            return "only the game change";
        }, scope: PushScope.First(1));

        Assert.True(r.OnPurpose);
        Assert.Equal("int game = 2;", f.Cat(f.GameUrl + "/branches/fort/dev/game.cpp").Trim());
        Assert.True(Git.RebaseInProgress(_wt));
        Assert.Equal(["schmetterling/engine.cpp"], Git.ConflictedFiles(_wt));
        Assert.Equal("int engine = 9;", Git.Ok(_wt, "show", "HEAD:schmetterling/engine.cpp").StdOut.Trim());
        Assert.Contains("a rebase stopped part way", Assert.Single(r.Warnings));
    }
}
