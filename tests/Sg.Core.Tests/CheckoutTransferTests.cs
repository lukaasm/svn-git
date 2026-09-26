namespace Sg.Core.Tests;

public sealed class CheckoutTransferTests
{
    [Fact]
    public void Copy_to_new_worktree_keeps_source_and_transfers_add_modify_delete_and_external_files()
    {
        using var f = new Fixture(); f.Setup();
        Fixture.Put(f.Checkout, "CMakeLists.txt", "project(changed)\n");
        Fixture.Put(f.Checkout, "schmetterling/engine.cpp", "int engine = 2;\n");
        Fixture.Put(f.Checkout, "notes/nested/new.txt", "not yet versioned\n");
        f.Svn.Rm(f.Checkout, ["src/.keep"]);
        var plan = CheckoutTransfer.Preview(f.Root, f.Co, "copied", newWorktree: true);
        Assert.True(plan.CanApply); Assert.Equal(4, plan.Files.Count);
        var result = CheckoutTransfer.Apply(f.Root, plan);
        Assert.Equal("project(changed)\n", File.ReadAllText(Path.Combine(result.Path, "CMakeLists.txt")));
        Assert.Equal("int engine = 2;\n", File.ReadAllText(Path.Combine(result.Path, "schmetterling/engine.cpp")));
        Assert.Equal("not yet versioned\n", File.ReadAllText(Path.Combine(result.Path, "notes/nested/new.txt")));
        Assert.False(File.Exists(Path.Combine(result.Path, "src/.keep")));
        Assert.Equal(4, Ops.CheckoutChanges(f.Root, f.Co).Count);
        Assert.False(result.Moved); Assert.Equal(2, Shelf.List(f.Root).Count);
        Assert.Equal(f.Root.Git.RefSha(f.Root.SnapshotRef(f.Co)), f.Root.Git.HeadSha(result.Path));
        Assert.Empty(f.Root.Git.Run(result.Path, "diff", "--cached", "--name-only").EnsureOk().StdOut);
    }

    [Fact]
    public void New_worktree_takes_the_checkout_edits_in_the_same_step()
    {
        using var f = new Fixture(); f.Setup();
        Fixture.Put(f.Checkout, "CMakeLists.txt", "project(changed)\n");
        Fixture.Put(f.Checkout, "notes/new.txt", "not yet versioned\n");
        var made = CheckoutTransfer.NewWorktree(f.Root, "carried", f.Co, CheckoutEdits.Move);
        Assert.Null(made.Stayed); Assert.NotNull(made.Carried); Assert.True(made.Carried!.Moved);
        Assert.Equal("project(changed)\n", File.ReadAllText(Path.Combine(made.Branch.Path, "CMakeLists.txt")));
        Assert.Equal("not yet versioned\n", File.ReadAllText(Path.Combine(made.Branch.Path, "notes/new.txt")));
        Assert.Empty(Ops.CheckoutChanges(f.Root, f.Co));
        // The task's receipt opens the new worktree, as it does for a worktree made without edits.
        Assert.Equal(TaskState.Succeeded, TaskResults.Describe(made).State);
        Assert.Equal(made.Branch.Path, TaskResults.FollowUp(made)!.Path);
    }

    [Fact]
    public void New_worktree_is_kept_when_the_checkout_edits_cannot_go()
    {
        using var f = new Fixture(); f.Setup();
        var made = CheckoutTransfer.NewWorktree(f.Root, "clean", f.Co, CheckoutEdits.Copy);
        Assert.Null(made.Carried); Assert.NotNull(made.Stayed);
        Assert.True(Directory.Exists(made.Branch.Path));
        Assert.Equal(TaskState.NeedsAttention, TaskResults.Describe(made).State);
    }

    [Fact]
    public void Move_to_existing_worktree_merges_edits_preserves_index_and_keeps_recovery()
    {
        using var f = new Fixture(); f.Setup();
        var text = "one\ntwo\nthree\nfour\nfive\nsix\nseven\n";
        Fixture.Put(f.Checkout, "CMakeLists.txt", text);
        f.Svn.Commit(f.Checkout, ["CMakeLists.txt"], "Longer base for merging"); Ops.Sync(f.Root, f.Co);
        var wt = Ops.Branch(f.Root, "existing", f.Co).Path;
        Fixture.Put(wt, "CMakeLists.txt", text.Replace("seven", "destination"));
        f.Root.Git.AddPaths(wt, ["CMakeLists.txt"]);
        var index = f.Root.Git.WriteTree(wt);
        Fixture.Put(f.Checkout, "CMakeLists.txt", text.Replace("one", "source"));
        Fixture.Put(f.Checkout, "added.txt", "added in svn\n"); f.Svn.Add(f.Checkout, ["added.txt"]);
        Fixture.Put(f.Checkout, "untracked.txt", "draft\n");
        var plan = CheckoutTransfer.Preview(f.Root, f.Co, "existing", move: true);
        Assert.True(plan.CanApply); Assert.Contains(plan.Files, x => x.Action == "Merge edits");
        var result = CheckoutTransfer.Apply(f.Root, plan);
        Assert.Equal(text.Replace("one", "source").Replace("seven", "destination"), File.ReadAllText(Path.Combine(wt, "CMakeLists.txt")));
        Assert.Equal(index, f.Root.Git.WriteTree(wt)); Assert.Empty(Ops.CheckoutChanges(f.Root, f.Co));
        Assert.True(result.Moved);
        Shelf.Restore(f.Root, result.SourceShelf, keep: true);
        Assert.Equal(text.Replace("one", "source"), File.ReadAllText(Path.Combine(f.Checkout, "CMakeLists.txt")));
        Assert.Contains(Ops.CheckoutChanges(f.Root, f.Co), x => x.Path == "added.txt" && x.Item == "added");
        Shelf.Restore(f.Root, result.DestinationShelf, keep: true);
        Assert.Equal(text.Replace("seven", "destination"), File.ReadAllText(Path.Combine(wt, "CMakeLists.txt")));
        Assert.Equal(index, f.Root.Git.WriteTree(wt));
    }

    [Fact]
    public void Conflict_leaves_both_folders_untouched()
    {
        using var f = new Fixture(); f.Setup(); var wt = Ops.Branch(f.Root, "existing", f.Co).Path;
        Fixture.Put(wt, "CMakeLists.txt", "destination\n"); Fixture.Put(f.Checkout, "CMakeLists.txt", "source\n");
        var plan = CheckoutTransfer.Preview(f.Root, f.Co, "existing", move: true);
        Assert.False(plan.CanApply); Assert.Single(plan.Conflicts);
        Assert.Throws<SgException>(() => CheckoutTransfer.Apply(f.Root, plan));
        Assert.Equal("destination\n", File.ReadAllText(Path.Combine(wt, "CMakeLists.txt")));
        Assert.Equal("source\n", File.ReadAllText(Path.Combine(f.Checkout, "CMakeLists.txt")));
        Assert.Empty(Shelf.List(f.Root));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("index")]
    [InlineData("nested")]
    public void Stale_preview_is_refused(string changed)
    {
        using var f = new Fixture(); f.Setup(); var wt = Ops.Branch(f.Root, "existing", f.Co).Path;
        Fixture.Put(f.Checkout, "notes/draft.txt", "draft\n");
        var plan = CheckoutTransfer.Preview(f.Root, f.Co, "existing", move: true);
        if (changed == "source") Fixture.Put(f.Checkout, "notes/draft.txt", "later\n");
        if (changed == "nested") Fixture.Put(f.Checkout, "notes/second.txt", "later\n");
        if (changed == "destination") Fixture.Put(wt, "notes/draft.txt", "later\n");
        if (changed == "index") { Fixture.Put(wt, "unrelated.txt", "later\n"); f.Root.Git.AddPaths(wt, ["unrelated.txt"]); }
        Assert.Contains("changed", Assert.Throws<SgException>(() => CheckoutTransfer.Apply(f.Root, plan)).Message);
        Assert.True(File.Exists(Path.Combine(f.Checkout, "notes/draft.txt"))); Assert.Empty(Shelf.List(f.Root));
    }

    [Fact]
    public void Selected_files_transfer_while_properties_and_shared_content_stay()
    {
        using var f = new Fixture(); f.Setup();
        Fixture.Put(f.Checkout, "CMakeLists.txt", "with props\n"); f.Svn.Ok(f.Checkout, "propset", "custom", "value", "CMakeLists.txt");
        Fixture.Put(f.Checkout, "fort/builds/big.bin", "shared\n");
        Fixture.Put(f.Checkout, "selected.txt", "selected\n"); Fixture.Put(f.Checkout, "other.txt", "other\n");
        var plan = CheckoutTransfer.Preview(f.Root, f.Co, "new", true, true, ["CMakeLists.txt", "fort/builds", "selected.txt"]);
        Assert.Single(plan.Files); Assert.Equal(2, plan.LeftBehind.Count);
        var result = CheckoutTransfer.Apply(f.Root, plan);
        Assert.True(File.Exists(Path.Combine(result.Path, "selected.txt")));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "selected.txt")));
        Assert.True(File.Exists(Path.Combine(f.Checkout, "other.txt")));
        Assert.Contains(Ops.CheckoutChanges(f.Root, f.Co), c => c.Path == "CMakeLists.txt" && c.Props == "modified");
    }

    [Fact]
    public void Binary_files_copy_and_conflict_without_overwriting()
    {
        using var f = new Fixture(); f.Setup(); var wt = Ops.Branch(f.Root, "existing", f.Co).Path;
        var bytes = new byte[] { 0, 1, 255, 0, 7 }; File.WriteAllBytes(Path.Combine(f.Checkout, "image.bin"), bytes);
        CheckoutTransfer.Apply(f.Root, CheckoutTransfer.Preview(f.Root, f.Co, "existing"));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(wt, "image.bin")));
        File.WriteAllBytes(Path.Combine(f.Checkout, "image.bin"), [0, 2, 3]);
        Assert.Single(CheckoutTransfer.Preview(f.Root, f.Co, "existing").Conflicts);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(wt, "image.bin")));
    }

    [Fact]
    public void Move_removes_only_transferred_files_from_an_unversioned_directory()
    {
        using var f = new Fixture(); f.Setup();
        Fixture.Put(f.Checkout, "notes/first.txt", "first\n"); Fixture.Put(f.Checkout, "notes/second.txt", "second\n");
        var result = CheckoutTransfer.Apply(f.Root, CheckoutTransfer.Preview(f.Root, f.Co, "selected", true, true, ["notes/first.txt"]));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "notes/first.txt")));
        Assert.Equal("second\n", File.ReadAllText(Path.Combine(f.Checkout, "notes/second.txt")));
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(result.Path, "notes/first.txt")));
        Assert.False(File.Exists(Path.Combine(result.Path, "notes/second.txt")));
    }

    [Fact]
    public void Failed_destination_write_keeps_source_and_recovery_shelves()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows prevents replacing a file opened without delete sharing.
        using var f = new Fixture(); f.Setup(); var wt = Ops.Branch(f.Root, "existing", f.Co).Path;
        Fixture.Put(f.Checkout, "CMakeLists.txt", "checkout edit\n");
        var plan = CheckoutTransfer.Preview(f.Root, f.Co, "existing", move: true);
        using (var locked = new FileStream(Path.Combine(wt, "CMakeLists.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Contains("Recovery shelves", Assert.Throws<SgException>(() => CheckoutTransfer.Apply(f.Root, plan)).Message);
        Assert.Equal("checkout edit\n", File.ReadAllText(Path.Combine(f.Checkout, "CMakeLists.txt")));
        Assert.Equal("project(fort)\n", File.ReadAllText(Path.Combine(wt, "CMakeLists.txt")));
        Assert.Equal(2, Shelf.List(f.Root).Count);
    }
}
