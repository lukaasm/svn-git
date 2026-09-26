namespace Sg.Core.Tests;

public sealed class WorkflowTests : IDisposable
{
    Fixture? _fixture;
    Fixture f => _fixture ??= new();
    public void Dispose() => _fixture?.Dispose();
    string Branch()
    {
        f.Setup();
        return Ops.Branch(f.Root, "feature", f.Co).Path;
    }

    [Fact]
    public void Pull_plan_scans_each_workspace_once_and_does_not_count_remote_commits()
    {
        var path = Branch();
        Fixture.Put(f.Checkout, "CMakeLists.txt", "project(new_server_version)\n");
        f.Svn.Ok(f.Checkout, "commit", "--non-interactive", "-m", "Changed on SVN", "CMakeLists.txt");
        Fixture.Put(path, "notes.txt", "untracked branch edit\n");
        Fixture.Put(f.Checkout, "local.txt", "untracked checkout edit\n");
        f.Log.Clear();
        var reports = new List<BranchUpdateProgress>();
        var plan = Operations.Plan(f.Root, path, progress: reports.Add);
        Assert.True(plan.Ready);
        Assert.Contains(plan.Revisions, x => x.To > x.From);
        Assert.Equal(2, f.Log.Lines.Count(x => x.Contains(" status ")));
        Assert.DoesNotContain(f.Log.Lines, x => x.Contains(" log ") && x.Contains("--xml"));
        Assert.Equal("Waiting for repository access", reports[0].Stage);
        Assert.Equal("Checking SVN revisions", reports[^1].Stage);
        Assert.Contains("notes.txt", reports[^1].BranchEdits!);
        Assert.Contains("local.txt", reports[^1].CheckoutEdits!);
        Assert.Null(reports[1].BranchEdits);
        Assert.Equal(WorkspaceVersion.Of(f.Root, path), plan.BranchVersion);
        Assert.Equal(WorkspaceVersion.Of(f.Root, f.Checkout, checkout: true), plan.CheckoutVersion);
        Fixture.Put(f.Checkout, "local.txt", "changed after the preview\n");
        Assert.Throws<SgException>(() => Operations.Run(f.Root, plan));
        Assert.Empty(Operations.List(f.Root));
    }

    [Fact]
    public void Dirty_update_recovers_both_owners_and_retains_checkpoint()
    {
        var path = Branch();
        Fixture.Put(path, "CMakeLists.txt", "project(branch)\n");
        Fixture.Put(path, "notes.txt", "untracked\n");
        Fixture.Put(f.Checkout, "schmetterling/engine.cpp", "checkout edit\n");
        var record = Operations.Run(f.Root, Operations.Plan(f.Root, path));
        Assert.Equal(OperationPhase.Completed, record.Phase);
        Assert.Equal("project(branch)\n", File.ReadAllText(Path.Combine(path, "CMakeLists.txt")));
        Assert.Equal("untracked\n", File.ReadAllText(Path.Combine(path, "notes.txt")));
        Assert.Equal("checkout edit\n", File.ReadAllText(Path.Combine(f.Checkout, "schmetterling/engine.cpp")));
        Assert.Equal(record.Before, f.Root.Git.RefSha(record.Checkpoint));
        Assert.Equal(2, record.RestoredShelves.Count);
    }

    [Fact]
    public void Changed_plan_is_refused_and_partial_staging_is_named()
    {
        var path = Branch();
        var plan = Operations.Plan(f.Root, path);
        Fixture.Put(path, "new.txt", "later edit\n");
        Assert.Throws<SgException>(() => Operations.Run(f.Root, plan));
        Fixture.Put(path, "CMakeLists.txt", "staged\n");
        f.Root.Git.Ok(path, "add", "CMakeLists.txt");
        Fixture.Put(path, "CMakeLists.txt", "unstaged\n");
        Assert.Contains(Operations.Plan(f.Root, path).Blockers, x => x.Contains("partial staging"));
        Assert.Empty(Operations.List(f.Root));
    }

    [Fact]
    public void Paused_save_resumes_and_protects_its_shelf()
    {
        var path = Branch();
        Fixture.Put(path, "notes.txt", "saved\n");
        int steps = 0;
        var record = Operations.Run(f.Root, Operations.Plan(f.Root, path), () => ++steps == 3);
        Assert.Equal(OperationPhase.SavingCheckout, record.Phase);
        Assert.NotNull(record.BranchShelf);
        Assert.Throws<SgException>(() => Shelf.Drop(f.Root, record.BranchShelf!));
        var resumed = Operations.Resume(f.Root, record.Id);
        Assert.Equal(OperationPhase.Completed, resumed.Phase);
        Assert.Single(Shelf.List(f.Root));
        Assert.Equal("saved\n", File.ReadAllText(Path.Combine(path, "notes.txt")));
    }

    [Fact]
    public void Readiness_invalidates_after_content_or_check_configuration_changes()
    {
        var path = Branch();
        Review.RunChecks(f.Root, path);
        Review.MarkReady(f.Root, path);
        Assert.Equal("Ready for this version", Review.Status(f.Root, path));
        Fixture.Put(path, "notes.txt", "new input\n");
        Assert.Equal("Changed since review", Review.Status(f.Root, path));
        Assert.Throws<SgException>(() => Review.MarkReady(f.Root, path));
        Review.RunChecks(f.Root, path);
        Review.MarkReady(f.Root, path);
        f.Root.Config.ReviewChecks.Add(new() { Name = "configured later", Executable = "git", Arguments = ["status"] });
        Assert.Equal("Changed since review", Review.Status(f.Root, path));
    }

    [Fact]
    public void Archive_refuses_ignored_untracked_and_pending_work()
    {
        var path = Branch();
        Fixture.Put(path, "notes.txt", "must survive\n");
        Fixture.Put(path, "build/win_vc17/ignored.txt", "ignored\n");
        var plan = Storage.Plan(f.Root, "feature");
        Assert.False(plan.Ready);
        Assert.Contains(plan.Blockers, x => x.Contains("notes.txt"));
        Assert.Contains(plan.Blockers, x => x.Contains("ignored.txt"));
        Assert.Throws<SgException>(() => Storage.Archive(f.Root, plan));
        Assert.True(File.Exists(Path.Combine(path, "notes.txt")));
    }

    [Fact]
    public void Coverage_detects_removed_refs_and_restore_test_leaves_source_intact()
    {
        var path = Branch();
        Fixture.Put(path, "CMakeLists.txt", "project(backedup)\n");
        f.Root.Git.Ok(path, "add", "CMakeLists.txt");
        f.Root.Git.Ok(path, "commit", "-m", "backed up change");
        Fixture.Put(path, "notes.txt", "uncommitted\n");
        var remote = Path.Combine(f.Base, "backup.git");
        Proc.Run("git", ["init", "--bare", remote], null, f.Log).EnsureOk();
        Backup.Set(f.Root, remote);
        Assert.True(Backup.Run(f.Root).Ok);
        var receipt = Backup.Coverage(f.Root, path);
        var before = WorkspaceVersion.Of(f.Root, path);
        Assert.Contains(receipt.Coverage, x => x.Kind == "branch" && x.State == "Remote refs checked");
        var tested = Backup.TestRestore(f.Root, receipt);
        Assert.NotNull(tested.RestoreTested);
        Assert.NotEqual(path, tested.RestorePath);
        Assert.Equal(before, WorkspaceVersion.Of(f.Root, path));
        Proc.Run("git", ["-C", remote, "update-ref", "-d", receipt.Refs.Keys.First()], null, f.Log).EnsureOk();
        Assert.NotEmpty(Backup.ValidateReceipt(f.Root, receipt));
        Assert.Throws<SgException>(() => Backup.TestRestore(f.Root, receipt));
    }

    [Fact]
    public void Conflict_can_be_finished_after_reopening_and_recovers_saved_edits()
    {
        var path = Branch();
        Fixture.Put(path, "schmetterling/engine.cpp", "branch change\n");
        f.Root.Git.Ok(path, "add", "schmetterling/engine.cpp");
        f.Root.Git.Ok(path, "commit", "-m", "local change");
        Fixture.Put(f.Checkout, "schmetterling/engine.cpp", "server change\n");
        f.Svn.Ok(Path.Combine(f.Checkout, "schmetterling"), "commit", "-m", "incoming change", "engine.cpp");
        Fixture.Put(path, "notes.txt", "branch draft\n");
        Fixture.Put(f.Checkout, "CMakeLists.txt", "checkout draft\n");
        var record = Operations.Run(f.Root, Operations.Plan(f.Root, path));
        Assert.Equal(OperationPhase.Replaying, record.Phase);
        var reopened = SgRoot.Open(f.RootDir, f.Log);
        var state = Conflicts.State(reopened, path);
        Assert.Single(state.Conflicted);
        Assert.Equal("both modified", Conflicts.Describe(state.Codes[state.Conflicted[0]]));
        reopened.Git.TakeSide(path, state.Conflicted, ours: false);
        var result = Conflicts.Continue(reopened, path);
        Assert.True(result.Ok);
        Assert.Equal(OperationPhase.Completed, Operations.Read(reopened, record.Id).Phase);
        Assert.Equal("branch draft\n", File.ReadAllText(Path.Combine(path, "notes.txt")));
        Assert.Equal("checkout draft\n", File.ReadAllText(Path.Combine(f.Checkout, "CMakeLists.txt")));
    }

    [Fact]
    public void Interrupted_restoration_never_applies_a_shelf_twice()
    {
        var path = Branch();
        Fixture.Put(path, "notes.txt", "saved draft\n");
        var record = Operations.Run(f.Root, Operations.Plan(f.Root, path), () => Operations.List(f.Root).Any(x => x.Phase == OperationPhase.RestoringBranch));
        record.ReviewShelves.Add(record.BranchShelf!);
        AtomicFile.WriteAllText(Path.Combine(f.Root.StorePath, "operations", record.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(record, SgConfig.JsonOptions));
        Fixture.Put(path, "notes.txt", "edited after interruption\n");
        var result = Operations.Resume(f.Root, record.Id);
        Assert.Equal(OperationPhase.NeedsReview, result.Phase);
        Assert.Equal("edited after interruption\n", File.ReadAllText(Path.Combine(path, "notes.txt")));
        Assert.Throws<SgException>(() => Shelf.Drop(f.Root, record.BranchShelf!));
        Assert.Throws<SgException>(() => Ops.Remove(f.Root, "feature", force: true));
    }

    [Fact]
    public void Clean_archive_retains_a_restorable_commit_checkpoint()
    {
        var path = Branch();
        // This branch has a shared junction by default, which must first be explicitly removed.
        Directory.Delete(Path.Combine(path, "fort/builds"));
        // Generated ignored helper files are still real files; archive must not silently discard them.
        foreach (var file in new[] { ".gitignore", "CLAUDE.local.md", ".cursor/rules/sg.mdc" }) File.Delete(Path.Combine(path, file));
        var plan = Storage.Plan(f.Root, "feature");
        Assert.True(plan.Ready, string.Join("\n", plan.Blockers));
        var id = Storage.Archive(f.Root, plan);
        Assert.False(Directory.Exists(path));
        var recovered = Operations.RestoreCheckpoint(f.Root, id);
        Assert.Equal(plan.Head, f.Root.Git.HeadSha(recovered));
        Assert.True(File.Exists(Path.Combine(recovered, "CMakeLists.txt")));
    }

    [Fact]
    public void Temporary_cleanup_rechecks_age_contents_and_pending_operations()
    {
        var path = Branch();
        var temp = f.Root.NewStoreTempDir();
        Fixture.Put(temp, "old.txt", "stale\n");
        File.SetLastWriteTimeUtc(Path.Combine(temp, "old.txt"), DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddDays(-2));
        var plan = Assert.Single(Storage.TemporaryData(f.Root));
        Assert.Empty(plan.Blockers);
        Fixture.Put(temp, "new.txt", "still in use\n");
        Assert.Throws<SgException>(() => Storage.CleanTemporaryData(f.Root, plan));
        var operation = Operations.Run(f.Root, Operations.Plan(f.Root, path), () => true);
        Assert.Contains(Storage.TemporaryData(f.Root).Single().Blockers, x => x.Contains("unfinished"));
        Operations.FinishReview(f.Root, operation.Id);
        File.SetLastWriteTimeUtc(Path.Combine(temp, "new.txt"), DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddDays(-2));
        Storage.CleanTemporaryData(f.Root, Storage.TemporaryData(f.Root).Single());
        Assert.False(Directory.Exists(temp));
    }

    [Fact]
    public void Optional_receipt_failure_cannot_turn_completed_work_into_a_failure()
    {
        Branch();
        File.WriteAllText(Path.Combine(f.Root.StorePath, "operations"), "blocks the journal directory");
        Operations.Receipt(f.Root, "Already published", "", ["r123"]);
        Assert.Contains(f.Log.Lines, x => x.Contains("receipt could not be saved"));
        // An intent record required before a server mutation must still fail closed.
        Assert.ThrowsAny<IOException>(() => Operations.Receipt(f.Root, "Before publishing", "", ["planned"], required: true));
    }

    [Fact]
    public void Forced_backup_restore_cannot_replace_a_branch_with_pending_recovery()
    {
        var path = Branch();
        var remote = Path.Combine(f.Base, "backup.git");
        Proc.Run("git", ["init", "--bare", remote], null, f.Log).EnsureOk();
        Backup.Set(f.Root, remote);
        Backup.Run(f.Root);
        var before = f.Root.Git.HeadSha(path);
        Operations.Run(f.Root, Operations.Plan(f.Root, path), () => true);
        var error = Assert.Throws<SgException>(() => Backup.Restore(f.Root, "feature", force: true));
        Assert.Contains("unfinished operation protects", error.Message);
        Assert.Equal(before, f.Root.Git.HeadSha(path));
        Assert.NotNull(Operations.Pending(f.Root, path));
    }
}
