using System.Diagnostics;
using Xunit.Abstractions;

namespace Sg.Core.Tests;

public sealed class TransferPreviewPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Batched_tree_reads_keep_literal_names_and_directory_modes()
    {
        using var f = new Fixture(); f.Setup();
        var wt = Ops.Branch(f.Root, "entries", f.Co).Path;
        string[] names = ["-option.txt", "[brackets].txt", "sp ace.txt", "żółć.txt"];
        foreach (var path in names) Fixture.Put(wt, path, path);
        f.Root.Git.AddPaths(wt, names); f.Root.Git.Run(wt, "commit", "-m", "Exact path fixture").EnsureOk();
        var entries = f.Root.Git.EntriesAt("HEAD", []);
        Assert.Empty(entries);
        var head = f.Root.Git.HeadSha(wt);
        entries = f.Root.Git.EntriesAt(head, names.Concat(["schmetterling", "missing.txt"]));
        Assert.Equal(5, entries.Count);
        Assert.All(names, path => Assert.Contains(entries, e => e.Path == path && e.Mode == "100644"));
        Assert.Contains(entries, e => e.Path == "schmetterling" && e.Type == "tree");
    }

    [Fact]
    public void Cancelling_a_large_preview_stops_between_batches_without_a_shelf_or_worktree()
    {
        using var f = new Fixture(); f.Setup();
        for (var i = 0; i < 150; i++) Fixture.Put(f.Checkout, $"drafts/file {i}.txt", $"draft {i}\n");
        using var cancel = new CancellationTokenSource();
        var checkpoints = new List<int>();
        using (Cancellation.Use(cancel.Token))
            Assert.ThrowsAny<OperationCanceledException>(() => CheckoutTransfer.Preview(f.Root, f.Co, "cancelled", newWorktree: true,
                progress: (done, total) => { Assert.Equal(150, total); checkpoints.Add(done); if (done > 0) cancel.Cancel(); }));
        Assert.Equal(new[] { 0, 64 }, checkpoints);
        Assert.False(Directory.Exists(Path.Combine(f.RootDir, "cancelled")));
        Assert.Null(f.Root.Git.RefSha("refs/heads/cancelled"));
        Assert.Empty(Shelf.List(f.Root));
        Assert.Equal(150, Directory.GetFiles(Path.Combine(f.Checkout, "drafts")).Length);
        Assert.True(CheckoutTransfer.Preview(f.Root, f.Co, "retry", newWorktree: true).CanApply);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Large_preview_batches_reads_without_changing_files_or_index(bool create)
    {
        using var f = new Fixture(); f.Setup();
        for (var i = 0; i < 64; i++) Fixture.Put(f.Checkout, $"preview/tracked {i:D3}.txt", $"original {i}\n");
        f.Svn.Add(f.Checkout, ["preview"]);
        f.Svn.Commit(f.Checkout, new[] { "preview" }.Concat(Enumerable.Range(0, 64).Select(i => $"preview/tracked {i:D3}.txt")), "Seed large preview"); Ops.Sync(f.Root, f.Co);
        var target = Ops.Branch(f.Root, "existing", f.Co).Path;
        var index = f.Root.Git.WriteTree(target);
        for (var i = 0; i < 64; i++)
        {
            Fixture.Put(f.Checkout, $"preview/tracked {i:D3}.txt", $"edited {i}\n");
            Fixture.Put(f.Checkout, $"notes/new {i:D3}.txt", $"new {i}\n");
        }
        f.Log.Clear();
        var timer = Stopwatch.StartNew();
        var plan = CheckoutTransfer.Preview(f.Root, f.Co, create ? "new" : "existing", newWorktree: create);
        timer.Stop();
        var commands = f.Log.Lines.Where(l => l.StartsWith("cmd:")).ToArray();
        var hashes = commands.Count(l => l.Contains(" hash-object "));
        var trees = commands.Count(l => l.Contains(" ls-tree "));
        output.WriteLine($"{(create ? "new" : "existing")}: {timer.ElapsedMilliseconds} ms; {commands.Length} processes; {hashes} hash-object; {trees} ls-tree");
        Assert.True(plan.CanApply); Assert.Equal(128, plan.Files.Count);
        Assert.Equal(64, plan.Files.Count(f => f.Action == "Copy"));
        Assert.Equal(64, plan.Files.Count(f => f.Action == "Add"));
        Assert.Empty(plan.LeftBehind); Assert.Empty(plan.Conflicts);
        Assert.Equal(index, f.Root.Git.WriteTree(target));
        Assert.Equal("edited 63\n", File.ReadAllText(Path.Combine(f.Checkout, "preview/tracked 063.txt")));
        Assert.Equal("original 63\n", File.ReadAllText(Path.Combine(target, "preview/tracked 063.txt")));
        Assert.Empty(Shelf.List(f.Root));
        // Count costly process boundaries instead of asserting wall-clock time on a shared CI runner.
        Assert.InRange(hashes, 1, 6);
        Assert.InRange(trees, 0, 4);
    }
}
