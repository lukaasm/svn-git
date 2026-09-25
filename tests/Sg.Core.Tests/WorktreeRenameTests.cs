namespace Sg.Core.Tests;

public sealed class WorktreeRenameTests
{
    [Fact]
    public void Rename_preserves_edits_shelves_comments_and_backup_exclusion()
    {
        using var f = new Fixture(); f.Setup();
        var path = Ops.Branch(f.Root, "original", f.Co).Path;
        Fixture.Put(path, "CMakeLists.txt", "saved edits\n");
        var shelf = Shelf.Save(f.Root, path, null, "before rename").Shelf;
        Fixture.Put(path, "CMakeLists.txt", "staged edits\n"); f.Root.Git.AddPaths(path, ["CMakeLists.txt"]);
        Fixture.Put(path, "untracked.txt", "keep this too\n");
        var index = f.Root.Git.WriteTree(path); var head = f.Root.Git.HeadSha(path);
        var file = CodeReview.ReadFile(f.Root, path, "CMakeLists.txt");
        CodeReview.Add(f.Root, path, file, "modified", 1, 1, "Review this", "Tester");
        var identity = CodeReview.WorktreeIdentity(f.Root, path);
        f.Root.Config.Backup = new BackupConfig { Excluded = ["original"] }; f.Root.Save();
        f.Root.Git.Config("branch.original.sgBackedUp", "old confirmation");
        var result = WorktreeRename.Apply(f.Root, WorktreeRename.Preview(f.Root, "original", "renamed"));
        Assert.False(Directory.Exists(path)); Assert.True(Directory.Exists(result.NewPath));
        Assert.Equal("renamed", f.Root.Git.CurrentBranch(result.NewPath));
        Assert.Equal(head, f.Root.Git.HeadSha(result.NewPath)); Assert.Equal(index, f.Root.Git.WriteTree(result.NewPath));
        Assert.Equal("staged edits\n", File.ReadAllText(Path.Combine(result.NewPath, "CMakeLists.txt")));
        Assert.Equal("keep this too\n", File.ReadAllText(Path.Combine(result.NewPath, "untracked.txt")));
        Assert.Equal(identity, CodeReview.WorktreeIdentity(f.Root, result.NewPath)); Assert.Single(CodeReview.Read(f.Root, result.NewPath).Threads);
        var kept = Shelf.Read(f.Root, shelf.Id); Assert.Equal(result.NewPath, kept.Path); Assert.Equal("renamed", kept.Branch);
        Assert.True(Backup.IsExcluded(f.Root.Config.Backup, "renamed")); Assert.False(Backup.IsExcluded(f.Root.Config.Backup, "original"));
        Assert.Null(f.Root.Git.ConfigGet("branch.renamed.sgBackedUp"));
        Assert.Equal(f.Co.Name, Ops.BaseCheckout(f.Root, "renamed").Name);
    }

    [Fact]
    public void Rename_retains_readiness_results_but_requires_review_in_new_folder()
    {
        using var f = new Fixture(); f.Setup(); var path = Ops.Branch(f.Root, "original", f.Co).Path;
        Review.RunChecks(f.Root, path); Review.MarkReady(f.Root, path);
        var result = WorktreeRename.Apply(f.Root, WorktreeRename.Preview(f.Root, path, "renamed"));
        Assert.NotNull(Review.Read(f.Root, result.NewPath)); Assert.Null(Review.Read(f.Root, result.NewPath)!.Ready);
    }

    [Fact]
    public void Existing_folder_or_branch_and_stale_plan_are_refused()
    {
        using var f = new Fixture(); f.Setup(); var path = Ops.Branch(f.Root, "original", f.Co).Path;
        Ops.Branch(f.Root, "taken", f.Co);
        Assert.Throws<SgException>(() => WorktreeRename.Preview(f.Root, path, "taken"));
        Directory.CreateDirectory(Path.Combine(f.RootDir, "folder"));
        Assert.Throws<SgException>(() => WorktreeRename.Preview(f.Root, path, "folder"));
        var plan = WorktreeRename.Preview(f.Root, path, "renamed");
        f.Root.Git.Ok(path, "-c", "user.name=Tester", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "Changed after preview");
        Assert.Throws<SgException>(() => WorktreeRename.Apply(f.Root, plan));
        Assert.True(Directory.Exists(path)); Assert.Equal("original", f.Root.Git.CurrentBranch(path));
    }
}
