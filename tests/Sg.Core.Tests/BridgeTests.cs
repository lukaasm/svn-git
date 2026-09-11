using Xunit;

namespace Sg.Core.Tests;

public sealed class BridgeTests : IDisposable
{
    readonly Fixture f = new();

    public void Dispose() => f.Dispose();

    List<string> SnapshotPaths() => f.Root.Git.LsTree(f.Root.SnapshotRef(f.Co), recursive: true).Select(e => e.Path).ToList();
    string SnapshotFile(string rel) => f.Root.Git.Out(null, "show", f.Root.SnapshotRef(f.Co) + ":" + rel);
    static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar))).Trim();

    [Fact]
    public void CheckoutAdd_BuildsSnapshot_WithoutSkippedUnversionedIgnoredAndLocalEdits()
    {
        File.AppendAllText(Path.Combine(f.Checkout, "CMakeLists.txt"), "# local tweak\n");
        File.WriteAllText(Path.Combine(f.Checkout, "junk.txt"), "unversioned");
        Fixture.Put(f.Checkout, "build/win_vc17/x.obj", "obj");
        f.Setup();

        var paths = SnapshotPaths();
        Assert.Contains("CMakeLists.txt", paths);
        Assert.Contains("schmetterling/engine.cpp", paths);
        Assert.Contains("fort/dev/game.cpp", paths);
        Assert.Contains("fort/tools/tool.py", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("fort/builds/"));
        Assert.DoesNotContain(paths, p => p.Contains(".svn"));
        Assert.DoesNotContain("junk.txt", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("build/win_vc17"));

        Assert.Equal("project(fort)", SnapshotFile("CMakeLists.txt"));
        var meta = SnapshotMeta.Parse(f.Root.Git.Body(f.Root.Git.RefSha(f.Root.SnapshotRef(f.Co))!));
        Assert.Equal(5, meta.Externals.Count);
        Assert.DoesNotContain(paths, p => p.StartsWith("fort/builds/data_engine"));
        Assert.True(meta.Revision > 0);

        Assert.NotNull(SgRoot.Find(f.Checkout, f.Log));
        Assert.Equal(f.Root.RootPath, SgRoot.Find(Path.Combine(f.Checkout, "fort", "dev"), f.Log)!.RootPath);
    }

    [Fact]
    public void Branch_MakesWorktree_WithNotes_Junction_AndMinimalSparse()
    {
        f.Setup();
        var b = Ops.Branch(f.Root, "feature-x", f.Co);
        Assert.True(File.Exists(Path.Combine(b.Path, "schmetterling", "engine.cpp")));
        Assert.True(File.Exists(Path.Combine(b.Path, "fort", "tools", "tool.py")));
        Assert.True(File.Exists(Path.Combine(b.Path, "CLAUDE.local.md")));
        Assert.True(PathUtil.IsReparsePoint(Path.Combine(b.Path, "fort", "builds")));
        Assert.True(File.Exists(Path.Combine(b.Path, "fort", "builds", "big.bin")));
        Assert.Equal("", f.Root.Git.Out(b.Path, "status", "--porcelain"));
        Assert.Equal(f.Root.RootPath, SgRoot.Find(b.Path, f.Log)!.RootPath);

        // .gitignore comes from svn:ignore and the skip list, and is itself invisible to git
        var ignore = File.ReadAllLines(Path.Combine(b.Path, ".gitignore"));
        Assert.Contains("/build/win_vc17", ignore);
        Assert.Contains("/fort/builds", ignore);
        Fixture.Put(b.Path, "build/win_vc17/x.obj", "obj");
        Assert.Equal("", f.Root.Git.Out(b.Path, "status", "--porcelain"));

        var m = Ops.Branch(f.Root, "small", f.Co, minimal: true);
        Assert.True(File.Exists(Path.Combine(m.Path, "fort", "dev", "game.cpp")));
        Assert.True(File.Exists(Path.Combine(m.Path, "schmetterling", "engine.cpp")));
        Assert.False(Directory.Exists(Path.Combine(m.Path, "fort", "tools")));

        var st = Ops.Status(f.Root, checkSvn: true);
        Assert.Equal(2, st.Worktrees.Count);
        Assert.All(st.Worktrees, w => Assert.False(w.NeedsRebase));
        Assert.Equal(0, st.Checkouts.Single().LocalEdits);
    }

    [Fact]
    public void Branch_CopiesOrClonesTheSharedFolder_AndTheChoiceIsPerBranch()
    {
        f.Setup();
        Ops.UpdateCheckout(f.Root, f.Co, new Ops.CheckoutEdit(Shared: SharedMode.Copy));
        Assert.Equal(SharedMode.Copy, SgConfig.Load(Path.Combine(f.RootDir, ".sg", "sg.json")).Checkouts.Single().Shared);

        var b = Ops.Branch(f.Root, "copied", f.Co);
        var builds = Path.Combine(b.Path, "fort", "builds");
        Assert.Equal(SharedMode.Copy, b.SharedMode);
        Assert.Equal(["fort/builds"], b.Shared);
        Assert.False(PathUtil.IsReparsePoint(builds));
        Assert.True(File.Exists(Path.Combine(builds, "big.bin")));
        // A private folder is not a working copy: .svn stays behind, and it is most of the bytes.
        Assert.False(Directory.Exists(Path.Combine(builds, ".svn")));
        File.WriteAllText(Path.Combine(builds, "big.bin"), "mine");
        Assert.NotEqual("mine", File.ReadAllText(Path.Combine(f.Checkout, "fort", "builds", "big.bin")));
        Assert.Equal("", f.Root.Git.Out(b.Path, "status", "--porcelain"));
        Assert.Contains("private copies", File.ReadAllText(Path.Combine(b.Path, "CLAUDE.local.md")));

        // A rebase brings the private folder in step with the checkout, the way a junction always is.
        var coBuilds = Path.Combine(f.Checkout, "fort", "builds");
        File.WriteAllText(Path.Combine(coBuilds, "big.bin"), "newer build");
        File.WriteAllText(Path.Combine(coBuilds, "extra.bin"), "new file");
        var rb = Ops.Rebase(f.Root, b.Path);
        Assert.True(rb.Ok);
        Assert.Equal(["fort/builds"], rb.Refreshed);
        Assert.Equal("newer build", File.ReadAllText(Path.Combine(builds, "big.bin")));
        Assert.Equal("new file", File.ReadAllText(Path.Combine(builds, "extra.bin")));
        Assert.False(Directory.Exists(Path.Combine(builds, ".svn")));

        // The checkout says copy; this one branch says junction.
        var j = Ops.Branch(f.Root, "linked", f.Co, shared: SharedMode.Junction);
        Assert.True(PathUtil.IsReparsePoint(Path.Combine(j.Path, "fort", "builds")));
        Assert.Contains("junctions", File.ReadAllText(Path.Combine(j.Path, "CLAUDE.local.md")));
        Assert.Empty(Ops.Rebase(f.Root, j.Path).Refreshed);
        Assert.True(PathUtil.IsReparsePoint(Path.Combine(j.Path, "fort", "builds")));

        var st = Ops.Status(f.Root, checkSvn: false);
        Assert.Equal("copy", st.Worktrees.Single(w => w.Branch == "copied").Shared);
        Assert.Equal("junction", st.Worktrees.Single(w => w.Branch == "linked").Shared);

        // A clone is refused before anything is made when the volumes cannot do it. On a ReFS temp it works.
        if (SharedFolders.CloneProblem(f.Co.Path, f.RootDir) is { } problem)
        {
            var ex = Assert.Throws<SgException>(() => Ops.Branch(f.Root, "cloned", f.Co, shared: SharedMode.Clone));
            Assert.Contains(problem, ex.Message);
            Assert.False(Directory.Exists(f.Root.WorktreePathFor("cloned")));
            Assert.Null(f.Root.Git.RefSha("refs/heads/cloned"));
        }
        else
        {
            var c = Ops.Branch(f.Root, "cloned", f.Co, shared: SharedMode.Clone);
            Assert.Equal(SharedMode.Clone, c.SharedMode);
            Assert.False(PathUtil.IsReparsePoint(Path.Combine(c.Path, "fort", "builds")));
            Assert.True(File.Exists(Path.Combine(c.Path, "fort", "builds", "big.bin")));
        }

        Ops.Remove(f.Root, "copied", force: true);
        Assert.False(Directory.Exists(b.Path));
        Assert.Null(f.Root.Git.ConfigGet("branch.copied.sgShared"));
    }

    [Fact]
    public void Push_CommitsPerRepository_KeepsRenames_ResetsBranch()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-y", f.Co).Path;

        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 2;\n");
        Fixture.Put(wt, "CMakeLists.txt", "project(fort)\nadd_subdirectory(x)\n");
        Fixture.Put(wt, "fort/dev/new/file.txt", "new\n");
        git.Ok(wt, "mv", "fort/dev/game.cpp", "fort/dev/game_renamed.cpp");
        File.Delete(Path.Combine(wt, "fort", "tools", "tool.py"));
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "feature y work");

        var r = Push.Run(f.Root, wt, "feature y: engine, root, game", interactive: true);

        Assert.True(r.AllCommitted, string.Join("\n", r.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}").Concat(r.Warnings)));
        Assert.Equal(new[] { "", "fort/dev", "fort/tools", "schmetterling" }, r.Groups.Select(g => g.Wc).ToArray());
        Assert.Equal("int engine = 2;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
        Assert.Equal("new", f.Cat(f.GameUrl + "/branches/fort/dev/new/file.txt").Trim());
        Assert.Contains("add_subdirectory", f.Cat(f.MonoUrl + "/trunk/CMakeLists.txt"));
        Assert.Contains("copyfrom", f.LogVerbose(f.GameUrl + "/branches/fort/dev"));
        Assert.DoesNotContain("game.cpp\n", f.Ls(f.GameUrl + "/branches/fort/dev").Replace("\r", ""));
        Assert.DoesNotContain("tool.py", f.Ls(f.GameUrl + "/branches/fort/tools"));

        Assert.Equal(git.RefSha(f.Root.SnapshotRef(f.Co)), git.HeadSha(wt));
        Assert.True(git.IsClean(wt));
        Assert.Empty(f.CheckoutChanges());
        Assert.Equal("int engine = 2;", Read(f.Checkout, "schmetterling/engine.cpp"));
        Assert.Equal("new", Read(f.Checkout, "fort/dev/new/file.txt"));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "fort", "tools", "tool.py")));
        Assert.Equal("int engine = 2;", SnapshotFile("schmetterling/engine.cpp"));
    }

    [Fact]
    public void Push_CancelledBeforeItWritesLeavesSvnUntouched()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-cancel", f.Co).Path;
        Fixture.Put(wt, "fort/dev/game.cpp", "int game = 5;\n");
        git.Ok(wt, "commit", "-q", "-am", "change the game file");
        var before = f.Cat(f.GameUrl + "/branches/fort/dev/game.cpp").Trim();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using (Cancellation.Use(cancelled.Token))
            Assert.ThrowsAny<OperationCanceledException>(
                () => Push.Run(f.Root, wt, "change the game file under cancellation", interactive: true));

        // Cancelling during sync and rebase is safe: nothing reached the server.
        Assert.Equal(before, f.Cat(f.GameUrl + "/branches/fort/dev/game.cpp").Trim());
    }

    [Fact]
    public void Cancellation_CanBeSuspendedForWorkThatMustFinish()
    {
        // This is what keeps a push from stopping between two repositories: once it starts writing
        // to SVN it runs under a suspended token, so a cancel cannot leave the checkout half done.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using (Cancellation.Use(cancelled.Token))
        {
            Assert.ThrowsAny<OperationCanceledException>(() => Proc.Run("git", ["--version"], null, f.Log));
            using (Cancellation.Use(CancellationToken.None))
                Assert.True(Proc.Run("git", ["--version"], null, f.Log).Ok);
            Assert.ThrowsAny<OperationCanceledException>(() => Proc.Run("git", ["--version"], null, f.Log));
        }
    }

    [Fact]
    public void CheckoutAdd_CancelledRegistersNothing()
    {
        f.Root = Ops.Init(f.RootDir, f.Log, fsmonitor: false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        using (Cancellation.Use(cancelled.Token))
            Assert.ThrowsAny<Exception>(() => Ops.CheckoutAdd(f.Root, f.Checkout, skip: ["fort/builds"]));

        Assert.Empty(f.Root.Config.Checkouts);
        Assert.False(File.Exists(Path.Combine(f.Checkout, ".git")), "a .git pointer was left in the checkout");
        Assert.Empty(SgRoot.Open(f.RootDir, f.Log).Config.Checkouts);
    }

    [Fact]
    public void PreviewChecks_AnswerTheSameQuestionsPushDoes()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-checks", f.Co).Path;
        Fixture.Put(wt, "fort/dev/game.cpp", "int game = 7;\n");
        git.Ok(wt, "commit", "-q", "-am", "change the game file");

        // Nothing in the way: every check passes and push would run.
        var ready = Push.Preview(f.Root, wt);
        Assert.True(ready.Ready, string.Join("\n", ready.Checks.Select(c => $"{c.Ok} {c.Name}: {c.Detail}")));
        Assert.Equal(6, ready.Checks.Count);

        // A local edit on the same file in the checkout is what push refuses on.
        Fixture.Put(f.Checkout, "fort/dev/game.cpp", "int game = 99; // local\n");
        var blocked = Push.Preview(f.Root, wt);
        Assert.False(blocked.Ready);
        var collision = Assert.Single(blocked.Checks, c => !c.Ok);
        Assert.Contains("local edit", collision.Name);
        Assert.Contains("fort/dev/game.cpp", collision.Detail);

        // The same state makes the real push refuse, so the checklist is not a separate opinion.
        Assert.Throws<SgException>(() => Push.Run(f.Root, wt, "change the game file", interactive: true));
    }

    [Fact]
    public void PreviewChecks_CatchADirtyWorktree()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "feature-dirty", f.Co).Path;
        Fixture.Put(wt, "fort/dev/game.cpp", "int game = 8;\n");

        var p = Push.Preview(f.Root, wt);

        Assert.False(p.Ready);
        Assert.Contains(p.Checks, c => !c.Ok && c.Name == "Worktree is clean");
    }

    [Fact]
    public void Push_RefusesWhenCheckoutHasLocalEditOnTheSameFile()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-z", f.Co).Path;
        Fixture.Put(f.Checkout, "fort/dev/game.cpp", "int game = 99; // local\n");
        Fixture.Put(wt, "fort/dev/game.cpp", "int game = 2;\n");
        git.Ok(wt, "commit", "-q", "-am", "change game");

        var ex = Assert.Throws<SgException>(() => Push.Run(f.Root, wt, "change the game file", interactive: true));

        Assert.Contains("local edit", ex.Message);
        Assert.Equal("int game = 1;", f.Cat(f.GameUrl + "/branches/fort/dev/game.cpp").Trim());
        Assert.Equal("int game = 1;", SnapshotFile("fort/dev/game.cpp"));
        Assert.Equal("int game = 99; // local", Read(f.Checkout, "fort/dev/game.cpp"));
        Assert.Equal(1, Ops.Status(f.Root, checkSvn: true).Checkouts.Single().LocalEdits);
    }

    [Fact]
    public void Push_PartialFailure_KeepsRestAsPendingCommit_ThenSucceeds()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-p", f.Co).Path;
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 3;\n");
        Fixture.Put(wt, "fort/dev/game.cpp", "int game = 3;\n");
        git.Ok(wt, "commit", "-q", "-am", "engine and game");
        f.SetPreCommitHook("engine", reject: true);

        var r = Push.Run(f.Root, wt, "engine and game change", interactive: true);

        Assert.False(r.AllCommitted);
        Assert.Equal("committed", r.Groups.Single(g => g.Wc == "fort/dev").State);
        Assert.Equal("failed", r.Groups.Single(g => g.Wc == "schmetterling").State);
        Assert.Equal("int game = 3;", f.Cat(f.GameUrl + "/branches/fort/dev/game.cpp").Trim());
        Assert.Equal("int engine = 1;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
        Assert.Equal("int engine = 1;", Read(f.Checkout, "schmetterling/engine.cpp"));
        Assert.Empty(f.CheckoutChanges());
        Assert.Empty(r.Warnings);

        Assert.StartsWith("not pushed yet", git.Subject(git.HeadSha(wt)));
        Assert.Equal(1, git.CountCommits(f.Root.SnapshotRef(f.Co), git.HeadSha(wt)));
        Assert.Equal("int engine = 3;", Read(wt, "schmetterling/engine.cpp"));
        Assert.Equal("int game = 3;", Read(wt, "fort/dev/game.cpp"));
        Assert.True(git.IsClean(wt));
        Assert.True(Ops.Status(f.Root, checkSvn: false).Worktrees.Single().Pending);

        f.SetPreCommitHook("engine", reject: false);
        var r2 = Push.Run(f.Root, wt, "engine change, second try", interactive: true);

        Assert.True(r2.AllCommitted, string.Join("\n", r2.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.Single(r2.Groups);
        Assert.Equal("int engine = 3;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
        Assert.Equal(git.RefSha(f.Root.SnapshotRef(f.Co)), git.HeadSha(wt));
        Assert.False(Ops.Status(f.Root, checkSvn: false).Worktrees.Single().Pending);
    }

    [Fact]
    public void Sync_PicksUpOutsideCommit_AndRebaseApplies()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-r", f.Co).Path;
        Fixture.Put(wt, "CMakeLists.txt", "project(fort)\n# branch\n");
        git.Ok(wt, "commit", "-q", "-am", "branch edit");

        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        Fixture.Put(other, "engine.cpp", "int engine = 7;\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "outside change");

        var s1 = Ops.Sync(f.Root, f.Co);
        Assert.True(s1.Changed);
        Assert.Equal("int engine = 7;", SnapshotFile("schmetterling/engine.cpp"));
        Assert.Contains("outside change", git.Body(s1.Sha));

        var s2 = Ops.Sync(f.Root, f.Co);
        Assert.False(s2.Changed);
        Assert.Equal(s1.Sha, s2.Sha);

        Assert.True(Ops.Status(f.Root, checkSvn: false).Worktrees.Single().NeedsRebase);
        var rb = Ops.Rebase(f.Root, wt);
        Assert.True(rb.Ok, rb.Output);
        Assert.Equal(1, rb.Ahead);
        Assert.Equal("int engine = 7;", Read(wt, "schmetterling/engine.cpp"));
        Assert.False(Ops.Status(f.Root, checkSvn: false).Worktrees.Single().NeedsRebase);
    }

    [Fact]
    public void Rebase_Conflict_ResolvedWithBranchVersion_ThenContinues()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-c", f.Co).Path;
        Fixture.Put(wt, "CMakeLists.txt", "project(fort)\n# branch side\n");
        git.Ok(wt, "commit", "-q", "-am", "branch edit");

        var other = f.OtherWc(f.MonoUrl + "/trunk");
        Fixture.Put(other, "CMakeLists.txt", "project(fort)\n# svn side\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "svn edit");
        Ops.Sync(f.Root, f.Co);

        var rb = Ops.Rebase(f.Root, wt);
        Assert.True(rb.Conflict);
        var st = Ops.Status(f.Root, checkSvn: false).Worktrees.Single();
        Assert.True(st.RebaseInProgress);
        Assert.Equal("feature-c", st.Branch);
        Assert.Equal(1, st.Conflicts);
        var state = Conflicts.State(f.Root, wt);
        Assert.Equal(Replay.Rebase, state.Kind);
        Assert.Equal(new[] { "CMakeLists.txt" }, state.Conflicted);
        Assert.Contains("svn side", git.ShowStage(wt, 2, "CMakeLists.txt"));
        Assert.Contains("branch side", git.ShowStage(wt, 3, "CMakeLists.txt"));

        git.TakeSide(wt, ["CMakeLists.txt"], ours: false);
        var cont = Conflicts.Continue(f.Root, wt);

        Assert.True(cont.Ok, cont.Output);
        Assert.Equal(1, cont.Ahead);
        Assert.Equal("project(fort)\n# branch side", Read(wt, "CMakeLists.txt").Replace("\r", ""));
        Assert.False(Ops.Status(f.Root, checkSvn: false).Worktrees.Single().RebaseInProgress);
        Assert.True(git.IsClean(wt));
    }

    [Fact]
    public void RefreshExcludes_UpdatesGitignoreInExistingWorktrees()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "feature-i", f.Co).Path;
        Assert.DoesNotContain("/src/generated", File.ReadAllLines(Path.Combine(wt, ".gitignore")));
        f.Svn.Ok(f.Checkout, "propset", "svn:ignore", "generated", "src");
        f.Svn.Ok(f.Checkout, "commit", "--non-interactive", "-m", "ignore generated", "src");

        f.Root.RefreshExcludes();

        Assert.Contains("/src/generated", File.ReadAllLines(Path.Combine(wt, ".gitignore")));
    }

    [Fact]
    public void SvnCommit_FromCheckout_CommitsPerRepository_AddsNewFiles_ThenSyncs()
    {
        f.Setup();
        Fixture.Put(f.Checkout, "CMakeLists.txt", "project(fort)\nset(X 1)\n");
        Fixture.Put(f.Checkout, "schmetterling/engine.cpp", "int engine = 5;\n");
        Fixture.Put(f.Checkout, "fort/dev/newdir/a.txt", "a\n");
        Fixture.Put(f.Checkout, "junk.txt", "not chosen");

        var changes = Ops.CheckoutChanges(f.Root, f.Co);
        Assert.Contains(changes, c => c.Path == "CMakeLists.txt" && c.Code == "M" && c.Wc == "");
        Assert.Contains(changes, c => c.Path == "schmetterling/engine.cpp" && c.Wc == "schmetterling");
        Assert.Contains(changes, c => c.Path == "fort/dev/newdir" && c.Code == "?" && c.Wc == "fort/dev");
        Assert.Contains(changes, c => c.Path == "junk.txt" && c.Code == "?");

        var r = Ops.SvnCommit(f.Root, f.Co, ["CMakeLists.txt", "schmetterling/engine.cpp", "fort/dev/newdir"], "direct commit from the checkout");

        Assert.True(r.AllCommitted, string.Join("\n", r.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.Equal(3, r.Groups.Count);
        Assert.Contains("set(X 1)", f.Cat(f.MonoUrl + "/trunk/CMakeLists.txt"));
        Assert.Equal("int engine = 5;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
        Assert.Equal("a", f.Cat(f.GameUrl + "/branches/fort/dev/newdir/a.txt").Trim());
        Assert.NotNull(r.Sync);
        Assert.Equal("int engine = 5;", SnapshotFile("schmetterling/engine.cpp"));
        Assert.Contains("fort/dev/newdir/a.txt", SnapshotPaths());
        var left = Ops.CheckoutChanges(f.Root, f.Co);
        Assert.Single(left);
        Assert.Equal("junk.txt", left[0].Path);

        Ops.SvnRevert(f.Root, f.Co, ["junk.txt"], deleteUnversioned: true);
        Assert.Empty(Ops.CheckoutChanges(f.Root, f.Co));
    }

    [Fact]
    public void SvnRevert_RestoresVersionedFiles()
    {
        f.Setup();
        Fixture.Put(f.Checkout, "fort/dev/game.cpp", "int game = 42;\n");
        Assert.Single(Ops.CheckoutChanges(f.Root, f.Co));
        Ops.SvnRevert(f.Root, f.Co, ["fort/dev/game.cpp"], deleteUnversioned: false);
        Assert.Equal("int game = 1;", Read(f.Checkout, "fort/dev/game.cpp"));
        Assert.Empty(Ops.CheckoutChanges(f.Root, f.Co));
    }

    /// <summary>
    /// svn update points a switched external back at what svn:externals declares, and deletes what
    /// only existed on the branch it was switched to. It says nothing about having done it. Sync has
    /// to notice and put the switch back, or switching an external is undone by the next sync.
    /// </summary>
    [Fact]
    public void Sync_KeepsASwitchedExternal_AndSnapshotsItsContent()
    {
        f.Setup();

        // A file only the trunk has, so the assertion is about content and not only about a URL.
        var other = f.OtherWc(f.EngineUrl + "/trunk/dev");
        Fixture.Put(other, "only_trunk.txt", "trunk only\n");
        f.Svn.Ok(other, "add", "--non-interactive", "only_trunk.txt");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "a file only on trunk");

        var trunkDev = f.EngineUrl + "/trunk/dev";
        Ops.SwitchExternal(f.Root, f.Co, "schmetterling", trunkDev);
        Assert.True(File.Exists(Path.Combine(f.Checkout, "schmetterling", "only_trunk.txt")));

        var r = Ops.Sync(f.Root, f.Co);

        Assert.Contains("schmetterling", r.KeptSwitched);
        Assert.Equal(trunkDev, f.Svn.Info(f.Checkout, "schmetterling").Url);
        Assert.True(File.Exists(Path.Combine(f.Checkout, "schmetterling", "only_trunk.txt")));
        Assert.Contains("schmetterling/only_trunk.txt", SnapshotPaths());
    }

    /// <summary>
    /// The switch has to survive without the branch difference crossing the wire twice. A plain
    /// svn update pulls the external home and sync sends it back out; on a builds folder over a VPN
    /// that is tens of gigabytes. Sync keeps the root update off the externals instead, so the
    /// external never leaves the branch it is on. The log is the only place that difference shows.
    /// </summary>
    [Fact]
    public void Sync_KeepsASwitchedExternal_WithoutARoundTrip()
    {
        f.Setup();
        var trunkDev = f.EngineUrl + "/trunk/dev";
        Ops.SwitchExternal(f.Root, f.Co, "schmetterling", trunkDev);
        f.Log.Clear();

        var r = Ops.Sync(f.Root, f.Co);

        Assert.DoesNotContain(f.Log.Lines, l => l.Contains("switching it to"));
        Assert.DoesNotContain(f.Log.Lines, l => l.Contains("transfers each of them twice"));
        Assert.Contains("schmetterling", r.KeptSwitched);
        Assert.Equal(trunkDev, f.Svn.Info(f.Checkout, "schmetterling").Url);
    }

    /// <summary>
    /// The cheap path only holds while svn has no external work of its own. A definition that moved
    /// on the server is svn's job, so sync hands the whole update back to it and puts the switch back
    /// afterwards. That is the slow way, and it has to say so.
    /// </summary>
    [Fact]
    public void Sync_WhenExternalsChangedOnTheServer_FallsBackAndStillKeepsTheSwitch()
    {
        f.Setup();
        var trunkDev = f.EngineUrl + "/trunk/dev";
        Ops.SwitchExternal(f.Root, f.Co, "schmetterling", trunkDev);

        // Someone adds an external on the server. Placing it is svn's job, not sync's.
        var other = f.OtherWc(f.MonoUrl + "/trunk");
        f.Svn.Ok(other, "propset", "svn:externals",
            f.EngineUrl + "/branches/fort/dev schmetterling\n" + f.EngineUrl + "/trunk/docs docs\n", ".");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "add the docs external");
        f.Log.Clear();

        var r = Ops.Sync(f.Root, f.Co);

        Assert.Contains(f.Log.Lines, l => l.Contains("transfers each of them twice"));
        Assert.Contains(f.Log.Lines, l => l.Contains("switching it to"));
        Assert.Contains("schmetterling", r.KeptSwitched);
        Assert.Equal(trunkDev, f.Svn.Info(f.Checkout, "schmetterling").Url);
        Assert.True(File.Exists(Path.Combine(f.Checkout, "docs", "readme.txt")));
    }

    [Fact]
    public void Sync_WithNothingSwitched_TouchesNoExternal()
    {
        f.Setup();

        var r = Ops.Sync(f.Root, f.Co);

        Assert.Empty(r.KeptSwitched);
        Assert.Equal(f.EngineUrl + "/branches/fort/dev", f.Svn.Info(f.Checkout, "schmetterling").Url);
    }

    [Fact]
    public void Remove_DeletesWorktreeAndBranch_LeavesCheckoutBuildsAlone()
    {
        f.Setup();
        var b = Ops.Branch(f.Root, "temp", f.Co);
        Assert.True(File.Exists(Path.Combine(b.Path, "fort", "builds", "big.bin")));

        Ops.Remove(f.Root, "temp", force: false);

        Assert.False(Directory.Exists(b.Path));
        Assert.True(File.Exists(Path.Combine(f.Checkout, "fort", "builds", "big.bin")));
        Assert.Null(f.Root.Git.RefSha("refs/heads/temp"));
        Assert.Empty(Ops.Status(f.Root, checkSvn: false).Worktrees);
    }

    [Fact]
    public void Push_CommitsOneWorkingCopy_UnderItsOwnMessage()
    {
        f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "feature-m", f.Co).Path;
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 3;\n");
        Fixture.Put(wt, "CMakeLists.txt", "project(fort)\nadd_subdirectory(m)\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "feature m work");

        var r = Push.Run(f.Root, wt, "shared message for the push", interactive: true,
            messageFor: new Dictionary<string, string> { ["schmetterling"] = "engine gets its own words" });

        Assert.True(r.AllCommitted, string.Join("\n", r.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.Contains("engine gets its own words", f.LogVerbose(f.EngineUrl + "/branches/fort/dev"));
        Assert.DoesNotContain("shared message for the push", f.LogVerbose(f.EngineUrl + "/branches/fort/dev"));
        Assert.Contains("shared message for the push", f.LogVerbose(f.MonoUrl + "/trunk"));

        var ex = Assert.Throws<SgException>(() => Push.Run(f.Root, wt, "shared message for the push", interactive: true,
            messageFor: new Dictionary<string, string> { [""] = "short" }));
        Assert.Contains("too short", ex.Message);
    }
}
