namespace Sg.Core.Tests;

public sealed class PermanentDiscardTests
{
    [Fact]
    public void Checkout_revert_deletes_selected_additions_without_creating_a_shelf()
    {
        using var f = new Fixture(); f.Setup();
        Fixture.Put(f.Checkout, "CMakeLists.txt", "discard\n");
        Fixture.Put(f.Checkout, "added.txt", "scheduled addition\n");
        f.Svn.Ok(f.Checkout, "add", "added.txt");
        Fixture.Put(f.Checkout, "loose.txt", "unversioned\n");
        Fixture.Put(f.Checkout, "keep.txt", "keep this\n");
        Ops.SvnRevert(f.Root, f.Co, ["CMakeLists.txt", "added.txt", "loose.txt"], deleteUnversioned: true);
        Assert.Equal("project(fort)\n", File.ReadAllText(Path.Combine(f.Checkout, "CMakeLists.txt")));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "added.txt")));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "loose.txt")));
        Assert.Equal("keep this\n", File.ReadAllText(Path.Combine(f.Checkout, "keep.txt")));
        Assert.Empty(Shelf.List(f.Root));
        Assert.Equal("keep.txt", Assert.Single(Ops.CheckoutChanges(f.Root, f.Co)).Path);
    }

    [Fact]
    public void Worktree_restore_handles_staged_additions_and_renames_and_keeps_unselected_edits()
    {
        using var f = new Fixture(); f.Setup();
        var wt = Ops.Branch(f.Root, "discard", f.Co).Path;
        Fixture.Put(wt, "added.txt", "new\n");
        f.Root.Git.AddPaths(wt, ["added.txt"]);
        f.Root.Git.Run(wt, "mv", "CMakeLists.txt", "renamed.txt").EnsureOk();
        Fixture.Put(wt, "renamed.txt", "changed too\n");
        Fixture.Put(wt, "schmetterling/engine.cpp", "keep staged\n");
        f.Root.Git.AddPaths(wt, ["schmetterling/engine.cpp"]);
        var staged = f.Root.Git.Run(wt, "diff", "--cached", "--", "schmetterling/engine.cpp").EnsureOk().StdOut;
        f.Root.Git.RestoreFromHead(wt, ["added.txt", "renamed.txt", "CMakeLists.txt"]);
        Assert.False(File.Exists(Path.Combine(wt, "added.txt")));
        Assert.False(File.Exists(Path.Combine(wt, "renamed.txt")));
        Assert.Equal("project(fort)\n", File.ReadAllText(Path.Combine(wt, "CMakeLists.txt")));
        Assert.Equal("keep staged\n", File.ReadAllText(Path.Combine(wt, "schmetterling/engine.cpp")));
        Assert.Equal(staged, f.Root.Git.Run(wt, "diff", "--cached", "--", "schmetterling/engine.cpp").EnsureOk().StdOut);
        Assert.Empty(Shelf.List(f.Root));
    }
}
