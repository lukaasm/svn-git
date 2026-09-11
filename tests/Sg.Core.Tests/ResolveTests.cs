namespace Sg.Core.Tests;

/// <summary>
/// Work that stopped on conflicts is finished, not thrown away. A rebase stops one way and an import
/// stops another, but both leave files at three stages with a commit waiting behind them, so both are
/// read and moved on through the same three verbs. What matters most here is the series: a patch that
/// would not merge used to end the import and take the patches after it down with it.
/// </summary>
public sealed class ResolveTests : IDisposable
{
    readonly Fixture f = new();

    public void Dispose() => f.Dispose();

    static string Read(string root, string rel) =>
        File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

    /// <summary>The far PC: its own root, its own checkout of the same repository, its own snapshot.</summary>
    (SgRoot Root, CheckoutConfig Co) Far()
    {
        var dir = Path.Combine(f.Base, "far");
        Directory.CreateDirectory(dir);
        var wc = Path.Combine(dir, "mono");
        f.Svn.Ok(null, "checkout", "--non-interactive", f.MonoUrl + "/trunk", wc);
        var root = Ops.Init(dir, f.Log, fsmonitor: false);
        var co = Ops.CheckoutAdd(root, wc, skip: ["fort/builds"], junctions: ["fort/builds"], optional: ["fort/tools"]).Checkout;
        return (root, co);
    }

    /// <summary>
    /// An export of two commits, and a far side that already holds a different edit to the file the
    /// first of them touches. The first patch cannot merge; the second one has nothing to do with it.
    /// </summary>
    (ImportResult Res, SgRoot Root) StoppedImport()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-r", f.Co).Path;

        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 2;\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "first: the engine says two");
        Fixture.Put(wt, "fort/dev/new/file.txt", "brand new\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "second: a file nobody else has");

        var file = Path.Combine(f.Base, "carried" + Export.Extension);
        Export.Write(f.Root, wt, file);

        // Somebody else changes the same line on the server, and only the far side has synced it.
        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        Fixture.Put(other, "engine.cpp", "int engine = 9;\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "the engine says nine");

        var far = Far();
        Ops.Sync(far.Root, far.Co);
        return (Export.Import(far.Root, file), far.Root);
    }

    /// <summary>
    /// The real repository stores its files with CRLF, and sg sets core.autocrlf=false so what SVN has
    /// is what the snapshot holds, byte for byte. git mailsplit strips the CR off the end of every line
    /// it reads, so a patch of such a file arrives LF-only, matches nothing, and the whole series stops
    /// on the first one - with nothing in conflict, because git never got as far as a merge. That is
    /// what --keep-cr is for, and it is why 44 of 45 commits stayed in the file they came in.
    /// </summary>
    [Fact]
    public void Import_OfAFileStoredWithCrlf_GoesIn()
    {
        f.Setup();
        var git = f.Root.Git;

        var other = f.OtherWc(f.MonoUrl + "/trunk");
        Fixture.Put(other, "src/app.txt", "one\r\ntwo\r\nthree\r\nfour\r\n");
        f.Svn.Ok(other, "add", "--non-interactive", "--parents", Path.Combine("src", "app.txt"));
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "a file with windows line endings");
        Ops.Sync(f.Root, f.Co);

        var wt = Ops.Branch(f.Root, "feature-crlf", f.Co).Path;
        Assert.Equal("one\r\ntwo\r\nthree\r\nfour\r\n", File.ReadAllText(Path.Combine(wt, "src", "app.txt")));
        Fixture.Put(wt, "src/app.txt", "one\r\ntwo\r\nCHANGED\r\nfour\r\n");
        git.Ok(wt, "commit", "-q", "-am", "change the third line");

        var file = Path.Combine(f.Base, "crlf" + Export.Extension);
        Export.Write(f.Root, wt, file);

        var far = Far();
        var r = Export.Import(far.Root, file);

        Assert.True(r.Ok, "the import stopped: " + r.Why);
        Assert.Equal(1, r.Applied);
        Assert.Equal(Replay.None, far.Root.Git.ReplayInProgress(r.Path));
        // Byte for byte, endings included: a patch that arrived LF-only would have rewritten the file.
        Assert.Equal("one\r\ntwo\r\nCHANGED\r\nfour\r\n", File.ReadAllText(Path.Combine(r.Path, "src", "app.txt")));
    }

    /// <summary>
    /// Two commits over one file. The second patch is cut against what the first one left, and that
    /// version is on no commit the far side has, so it has to travel in the pack or git cannot merge
    /// the second patch at all - it falls back to a plain apply, which the far side's own edit defeats.
    /// </summary>
    [Fact]
    public void Import_OfTwoCommitsOverOneFile_MergesTheSecondOneToo()
    {
        f.Setup();
        var git = f.Root.Git;

        // A file long enough that an edit at one end is nowhere near an edit at the other, so what
        // this test measures is the missing base blob and not a conflict of its own making.
        static string Lines(string first, string last) =>
            first + "\n" + string.Join("\n", Enumerable.Range(2, 18).Select(i => "line " + i)) + "\n" + last + "\n";

        var other = f.OtherWc(f.MonoUrl + "/trunk");
        Fixture.Put(other, "src/long.txt", Lines("line 1", "line 20"));
        f.Svn.Ok(other, "add", "--non-interactive", "--parents", Path.Combine("src", "long.txt"));
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "a long file");
        Ops.Sync(f.Root, f.Co);

        var wt = Ops.Branch(f.Root, "feature-twice", f.Co).Path;
        Fixture.Put(wt, "src/long.txt", Lines("line 1", "the branch touches the end"));
        git.Ok(wt, "commit", "-q", "-am", "first touch");
        Fixture.Put(wt, "src/long.txt", Lines("line 1", "the branch touches the end again"));
        git.Ok(wt, "commit", "-q", "-am", "second touch, same file");

        var file = Path.Combine(f.Base, "twice" + Export.Extension);
        Export.Write(f.Root, wt, file);

        // The far side has moved on, in the same file but at the other end of it.
        Fixture.Put(other, "src/long.txt", Lines("svn touches the start", "line 20"));
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "svn changes the first line");

        var far = Far();
        Ops.Sync(far.Root, far.Co);
        var r = Export.Import(far.Root, file);

        Assert.True(r.Ok, "the import stopped on " + r.Stopped + ": " + r.Why);
        Assert.Equal(2, r.Applied);
        // Both ends survived: the second patch merged over an edit it was never cut against.
        var text = Read(r.Path, "src/long.txt");
        Assert.Contains("the branch touches the end again", text);
        Assert.Contains("svn touches the start", text);
    }

    [Fact]
    public void Import_ThatCannotMerge_IsLeftWaitingWithTheRestOfTheSeriesBehindIt()
    {
        var (res, root) = StoppedImport();

        Assert.False(res.Ok);
        Assert.True(res.Waiting);
        Assert.Equal(0, res.Applied);
        Assert.Equal(2, res.Commits);
        Assert.Contains("schmetterling/engine.cpp", res.Conflicted);

        // The state reads the same way a stopped rebase does, and says which of the two it is.
        var state = Conflicts.State(root, res.Path);
        Assert.Equal(Replay.Import, state.Kind);
        Assert.True(state.InProgress);
        Assert.Equal("import", state.Verb);
        Assert.Equal(1, state.At);
        Assert.Equal(2, state.Of);
        Assert.Equal("first: the engine says two", state.Stopped);
        Assert.Equal(["schmetterling/engine.cpp"], state.Conflicted);

        // Both versions are there to pick between: what is on the branch, and what came in the file.
        Assert.Contains("engine = 9", root.Git.ShowStage(res.Path, 2, "schmetterling/engine.cpp"));
        Assert.Contains("engine = 2", root.Git.ShowStage(res.Path, 3, "schmetterling/engine.cpp"));

        // And the overview says so, rather than calling the worktree clean.
        var ws = Ops.Status(root, checkSvn: false).Worktrees.Single();
        Assert.Equal(Replay.Import, ws.Stopped);
        Assert.True(ws.RebaseInProgress);
        Assert.Equal(1, ws.Conflicts);
        Assert.Equal("feature-r", ws.Branch);
    }

    [Fact]
    public void Import_Resolved_ThenContinued_LandsEveryCommitOfTheSeries()
    {
        var (res, root) = StoppedImport();
        var git = root.Git;

        git.TakeSide(res.Path, ["schmetterling/engine.cpp"], ours: false);
        Assert.Empty(git.ConflictedFiles(res.Path));

        var cont = Conflicts.Continue(root, res.Path);
        Assert.True(cont.Ok, cont.Output);
        Assert.Equal(Replay.Import, cont.Kind);
        Assert.Equal(2, cont.Ahead);

        // The patch that stopped, and the one that was queued behind it, are both on the branch.
        Assert.Equal("int engine = 2;\n", Read(res.Path, "schmetterling/engine.cpp"));
        Assert.Equal("brand new\n", Read(res.Path, "fort/dev/new/file.txt"));
        Assert.Equal(Replay.None, git.ReplayInProgress(res.Path));
        Assert.True(git.IsClean(res.Path));

        var log = git.Log(res.Path, root.SnapshotRef(Ops.BaseCheckout(root, "feature-r")) + "..refs/heads/feature-r", 10);
        Assert.Equal(["second: a file nobody else has", "first: the engine says two"], log.Select(c => c.Subject).ToArray());
    }

    [Fact]
    public void Import_Continued_WithoutResolving_IsRefusedAndNamesTheFiles()
    {
        var (res, root) = StoppedImport();
        var ex = Assert.Throws<SgException>(() => Conflicts.Continue(root, res.Path));
        Assert.Contains("still in conflict", ex.Message);
        Assert.Contains("schmetterling/engine.cpp", ex.Message);
        Assert.Equal(Replay.Import, root.Git.ReplayInProgress(res.Path));
    }

    /// <summary>
    /// Resolving a conflict back to what the branch already holds leaves the step with nothing to
    /// commit. That is not "every file is resolved": continuing can only fail, and a page that offered
    /// Continue again would loop with nothing changing. It is named as stuck, and Skip is offered.
    /// </summary>
    [Fact]
    public void Import_ResolvedBackToWhatIsAlreadyHere_IsCalledStuckRatherThanResolved()
    {
        var (res, root) = StoppedImport();

        root.Git.TakeSide(res.Path, ["schmetterling/engine.cpp"], ours: true);
        Assert.Empty(root.Git.ConflictedFiles(res.Path));

        var state = Conflicts.State(root, res.Path);
        Assert.True(state.InProgress);
        Assert.True(state.Stuck);

        var ex = Assert.Throws<SgException>(() => Conflicts.Continue(root, res.Path));
        Assert.Contains("Nothing is staged", ex.Message);
        Assert.Contains("Skip it", ex.Message);

        // And the way out it names does work.
        var skipped = Conflicts.Skip(root, res.Path);
        Assert.True(skipped.Ok, skipped.Output);
        Assert.Equal(Replay.None, root.Git.ReplayInProgress(res.Path));
    }

    [Fact]
    public void ForcingAPatchThatGitDidTake_IsRefused()
    {
        var (res, root) = StoppedImport();
        var ex = Assert.Throws<SgException>(() => Conflicts.ApplyWhatFits(root, res.Path));
        Assert.Contains("in conflict", ex.Message);
        Assert.Equal(Replay.Import, root.Git.ReplayInProgress(res.Path));
    }

    [Fact]
    public void Import_Skipped_DropsThatOneAndLandsTheRest()
    {
        var (res, root) = StoppedImport();

        var skipped = Conflicts.Skip(root, res.Path);
        Assert.True(skipped.Ok, skipped.Output);
        Assert.Equal(1, skipped.Ahead);

        // The one that would not merge is gone; SVN's version of that file stands. The other landed.
        Assert.Equal("int engine = 9;\n", Read(res.Path, "schmetterling/engine.cpp"));
        Assert.Equal("brand new\n", Read(res.Path, "fort/dev/new/file.txt"));
        Assert.Equal(Replay.None, root.Git.ReplayInProgress(res.Path));
    }

    [Fact]
    public void Import_Aborted_TakesTheWholeSeriesBackOff()
    {
        var (res, root) = StoppedImport();

        Conflicts.Abort(root, res.Path);

        Assert.Equal(Replay.None, root.Git.ReplayInProgress(res.Path));
        var co = Ops.BaseCheckout(root, "feature-r");
        Assert.Equal(0, root.Git.CountCommits(root.SnapshotRef(co), "refs/heads/feature-r"));
        Assert.Equal("int engine = 9;\n", Read(res.Path, "schmetterling/engine.cpp"));
        Assert.False(File.Exists(Path.Combine(res.Path, "fort", "dev", "new", "file.txt")));
    }

    [Fact]
    public void Rebase_Skipped_DropsTheCommitItStoppedOn()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-s", f.Co).Path;
        Fixture.Put(wt, "CMakeLists.txt", "project(fort)\n# branch side\n");
        git.Ok(wt, "commit", "-q", "-am", "branch edit");

        var other = f.OtherWc(f.MonoUrl + "/trunk");
        Fixture.Put(other, "CMakeLists.txt", "project(fort)\n# svn side\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "svn edit");
        Ops.Sync(f.Root, f.Co);

        Assert.True(Ops.Rebase(f.Root, wt).Conflict);
        var state = Conflicts.State(f.Root, wt);
        Assert.Equal(Replay.Rebase, state.Kind);
        Assert.Equal("rebase", state.Verb);
        Assert.Equal("branch edit", state.Stopped);

        var skipped = Conflicts.Skip(f.Root, wt);
        Assert.True(skipped.Ok, skipped.Output);
        Assert.Equal(0, skipped.Ahead);
        Assert.Equal("project(fort)\n# svn side\n", Read(wt, "CMakeLists.txt"));
        Assert.Equal(Replay.None, git.ReplayInProgress(wt));
    }

    [Fact]
    public void NothingStopped_RefusesEveryVerbAndSaysSo()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "feature-q", f.Co).Path;

        var state = Conflicts.State(f.Root, wt);
        Assert.Equal(Replay.None, state.Kind);
        Assert.False(state.InProgress);
        Assert.Empty(state.Conflicted);

        foreach (var verb in new Action[] { () => Conflicts.Continue(f.Root, wt), () => Conflicts.Skip(f.Root, wt), () => Conflicts.Abort(f.Root, wt) })
            Assert.Contains("nothing is stopped", Assert.Throws<SgException>(verb).Message);
    }
}
