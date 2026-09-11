using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Putting changes aside and taking them back. The checkout is the case that matters: a local edit
/// there blocks a push of the same file, and a shelf is how it gets out of the way without being lost.
/// </summary>
public sealed class ShelfTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();

    string Abs(string rel) => Path.Combine(f.Checkout, rel.Replace('/', Path.DirectorySeparatorChar));
    string Read(string rel) => File.ReadAllText(Abs(rel));
    bool Has(string rel) => File.Exists(Abs(rel));

    /// <summary>What svn says about one path. A path svn status does not name at all is clean.</summary>
    string ItemOf(string rel) =>
        f.Svn.Status(f.Checkout, noIgnore: false).FirstOrDefault(e => PathUtil.Rel(e.Path) == rel)?.Item
        ?? (Has(rel) ? "clean" : "gone");

    [Fact]
    public void A_modified_file_leaves_the_checkout_and_comes_back()
    {
        f.Setup();
        File.WriteAllText(Abs("CMakeLists.txt"), "project(fort)\nlocal edit\n");
        Assert.Equal("modified", ItemOf("CMakeLists.txt"));

        var saved = Shelf.Save(f.Root, f.Checkout, null, "my edit");
        Assert.Equal(1, saved.Shelf.Count);
        Assert.True(saved.Shelf.IsCheckout);
        Assert.Equal("project(fort)\n", Read("CMakeLists.txt"));
        Assert.Equal("clean", ItemOf("CMakeLists.txt"));

        var back = Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Equal("project(fort)\nlocal edit\n", Read("CMakeLists.txt"));
        Assert.Equal("modified", ItemOf("CMakeLists.txt"));
        Assert.Empty(back.Conflicted);
        Assert.Empty(Shelf.List(f.Root));
    }

    [Fact]
    public void An_unversioned_file_goes_with_it_and_is_unversioned_again_after()
    {
        f.Setup();
        File.WriteAllText(Abs("notes.txt"), "mine\n");
        var saved = Shelf.Save(f.Root, f.Checkout, null, "notes");
        Assert.False(Has("notes.txt"));

        Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Equal("mine\n", Read("notes.txt"));
        Assert.Equal("unversioned", ItemOf("notes.txt"));
    }

    [Fact]
    public void A_file_scheduled_for_adding_is_scheduled_again_when_it_comes_back()
    {
        f.Setup();
        File.WriteAllText(Abs("added.txt"), "new file\n");
        f.Svn.Add(f.Checkout, new[] { "added.txt" });
        Assert.Equal("added", ItemOf("added.txt"));

        var saved = Shelf.Save(f.Root, f.Checkout, null, "an add");
        Assert.False(Has("added.txt"));
        Assert.Equal("gone", ItemOf("added.txt"));

        Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Equal("new file\n", Read("added.txt"));
        Assert.Equal("added", ItemOf("added.txt"));
    }

    [Fact]
    public void A_deleted_file_is_deleted_again_when_it_comes_back()
    {
        f.Setup();
        f.Svn.Rm(f.Checkout, new[] { "CMakeLists.txt" });
        Assert.Equal("deleted", ItemOf("CMakeLists.txt"));

        var saved = Shelf.Save(f.Root, f.Checkout, null, "a delete");
        Assert.True(Has("CMakeLists.txt"));
        Assert.Equal("clean", ItemOf("CMakeLists.txt"));

        Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Equal("deleted", ItemOf("CMakeLists.txt"));
    }

    [Fact]
    public void Only_the_named_paths_go()
    {
        f.Setup();
        File.WriteAllText(Abs("CMakeLists.txt"), "project(fort)\none\n");
        File.WriteAllText(Abs("schmetterling/engine.cpp"), "int engine = 2;\n");

        var saved = Shelf.Save(f.Root, f.Checkout, new[] { "schmetterling" }, "the engine only");
        Assert.Equal(1, saved.Shelf.Count);
        Assert.Equal("schmetterling/engine.cpp", saved.Shelf.Files[0].Path);
        Assert.Equal("project(fort)\none\n", Read("CMakeLists.txt"));
        Assert.Equal("int engine = 1;\n", Read("schmetterling/engine.cpp"));
    }

    [Fact]
    public void A_skipped_path_is_refused_rather_than_half_shelved()
    {
        f.Setup();
        Directory.CreateDirectory(Abs("fort/builds"));
        File.WriteAllText(Abs("fort/builds/scratch.txt"), "not tracked here\n");
        var ex = Assert.Throws<SgException>(() => Shelf.Save(f.Root, f.Checkout, new[] { "fort/builds" }, "builds"));
        Assert.Contains("does not track these", ex.Message);
        Assert.True(Has("fort/builds/scratch.txt"));
    }

    [Fact]
    public void A_merge_that_cannot_settle_keeps_the_shelf_and_names_the_file()
    {
        f.Setup();
        // Both sides rewrite the same line, so there is no merge that keeps them both.
        File.WriteAllText(Abs("schmetterling/engine.cpp"), "int engine = 2;\n");
        var saved = Shelf.Save(f.Root, f.Checkout, null, "mine");

        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        File.WriteAllText(Path.Combine(other, "engine.cpp"), "int engine = 3;\n");
        f.Svn.Commit(other, new[] { "engine.cpp" }, "someone else took the same line");
        Ops.Sync(f.Root, f.Co);

        var back = Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Contains("schmetterling/engine.cpp", back.Conflicted);
        Assert.True(back.Kept);
        Assert.Single(Shelf.List(f.Root));
        Assert.Contains("<<<<<<<", Read("schmetterling/engine.cpp"));
    }

    [Fact]
    public void A_file_the_shelf_adds_that_someone_else_added_too_is_left_alone()
    {
        f.Setup();
        File.WriteAllText(Abs("notes.txt"), "mine\n");
        var saved = Shelf.Save(f.Root, f.Checkout, null, "notes");
        Assert.False(Has("notes.txt"));

        // The same name is back, with other content: there is no third version to merge against.
        File.WriteAllText(Abs("notes.txt"), "somebody else's\n");

        var back = Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Contains("notes.txt", back.Conflicted);
        Assert.True(back.Kept);
        Assert.Equal("somebody else's\n", Read("notes.txt"));
    }

    [Fact]
    public void A_file_that_moved_on_gets_the_change_merged_into_it()
    {
        f.Setup();
        // A local edit at the end of the file, then the same file changed at the top on the server.
        File.WriteAllText(Abs("schmetterling/engine.cpp"), "int engine = 1;\nlocal tail\n");
        var saved = Shelf.Save(f.Root, f.Checkout, null, "tail");

        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        File.WriteAllText(Path.Combine(other, "engine.cpp"), "// a header line\nint engine = 1;\n");
        f.Svn.Commit(other, new[] { "engine.cpp" }, "someone else changed the top");
        Ops.Sync(f.Root, f.Co);

        var back = Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Empty(back.Conflicted);
        Assert.Contains("a header line", Read("schmetterling/engine.cpp"));
        Assert.Contains("local tail", Read("schmetterling/engine.cpp"));
    }

    [Fact]
    public void A_shelf_clears_the_push_check_the_local_edit_failed()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "feature-shelf", f.Co).Path;
        f.Root.Git.Config("user.name", "Test");
        f.Root.Git.Config("user.email", "test@localhost");
        File.WriteAllText(Path.Combine(wt, "CMakeLists.txt"), "project(fort)\nfrom the branch\n");
        f.Root.Git.AddPaths(wt, new[] { "CMakeLists.txt" });
        f.Root.Git.CommitAsUser(wt, "change it on the branch\n");

        // The same file is edited in the checkout, so the push refuses to write over it.
        File.WriteAllText(Abs("CMakeLists.txt"), "project(fort)\nand in the checkout\n");
        var before = Push.Preview(f.Root, wt);
        var check = before.Checks.Single(c => c.Id == PushChecks.Collisions);
        Assert.False(check.Ok);
        Assert.Contains("CMakeLists.txt", check.Paths);

        Shelf.Save(f.Root, f.Checkout, check.Paths, "out of the way of the push");

        var after = Push.Preview(f.Root, wt);
        Assert.True(after.Checks.Single(c => c.Id == PushChecks.Collisions).Ok);
        Assert.True(after.Ready);
    }

    [Fact]
    public void A_worktree_shelves_its_own_changes_and_stages_again_what_was_staged()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "feature-wt-shelf", f.Co).Path;
        f.Root.Git.Config("user.name", "Test");
        f.Root.Git.Config("user.email", "test@localhost");
        File.WriteAllText(Path.Combine(wt, "CMakeLists.txt"), "project(fort)\nwork in progress\n");
        File.WriteAllText(Path.Combine(wt, "fresh.txt"), "brand new\n");
        f.Root.Git.AddPaths(wt, new[] { "CMakeLists.txt" });

        var saved = Shelf.Save(f.Root, wt, null, "work in progress");
        Assert.False(saved.Shelf.IsCheckout);
        Assert.Equal("feature-wt-shelf", saved.Shelf.Branch);
        Assert.Equal(2, saved.Shelf.Count);
        Assert.True(f.Root.Git.IsClean(wt));
        Assert.False(File.Exists(Path.Combine(wt, "fresh.txt")));

        Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Contains("work in progress", File.ReadAllText(Path.Combine(wt, "CMakeLists.txt")));
        Assert.Equal("brand new\n", File.ReadAllText(Path.Combine(wt, "fresh.txt")));
        var staged = f.Root.Git.StatusEntries(wt).Where(e => e.Staged).Select(e => e.Path).ToList();
        Assert.Contains("CMakeLists.txt", staged);
    }

    [Fact]
    public void Keeping_one_leaves_it_on_the_shelf_and_dropping_takes_it_off()
    {
        f.Setup();
        File.WriteAllText(Abs("CMakeLists.txt"), "project(fort)\nkeep me\n");
        var saved = Shelf.Save(f.Root, f.Checkout, null, "keep me");

        var back = Shelf.Restore(f.Root, saved.Shelf.Id, keep: true);
        Assert.True(back.Kept);
        Assert.Single(Shelf.List(f.Root));

        Shelf.Drop(f.Root, saved.Shelf.Id);
        Assert.Empty(Shelf.List(f.Root));
    }

    [Fact]
    public void The_status_of_a_checkout_says_how_many_shelves_it_has()
    {
        f.Setup();
        File.WriteAllText(Abs("CMakeLists.txt"), "project(fort)\ncounted\n");
        Shelf.Save(f.Root, f.Checkout, null, "counted");
        var s = Ops.Status(f.Root, checkSvn: false);
        Assert.Equal(1, s.Checkouts.Single().Shelves);
    }
}
