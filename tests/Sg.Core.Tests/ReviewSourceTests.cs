namespace Sg.Core.Tests;

public sealed class ReviewSourceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "sg-review-source-" + Guid.NewGuid().ToString("N"));
    public ReviewSourceTests() => Directory.CreateDirectory(Path.Combine(root, "src"));
    public void Dispose() => Directory.Delete(root, recursive: true);
    string FilePath => Path.Combine(root, "src", "file.cs");
    static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Atomic_saves_deletes_and_recreation_report_versions_without_changing_the_snapshot()
    {
        AtomicFile.WriteAllText(FilePath, "before\n");
        using var source = new ReviewSource(root, "src/file.cs");
        var signal = Signal(); source.Changed += () => signal.TrySetResult();
        var before = source.ReadVersion();
        AtomicFile.WriteAllText(FilePath, "after\n");
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkspaceVersion.Hash("after\n"), source.ReadVersion());
        Assert.Equal(WorkspaceVersion.Hash("before\n"), before);
        signal = Signal(); File.Delete(FilePath);
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("missing", source.ReadVersion());
        signal = Signal(); AtomicFile.WriteAllText(FilePath, "restored\n");
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkspaceVersion.Hash("restored\n"), source.ReadVersion());
    }
    [Fact]
    public async Task Same_content_and_other_files_do_not_report_new_versions_and_disposal_stops_observation()
    {
        File.WriteAllText(FilePath, "same");
        using var source = new ReviewSource(root, "src/file.cs");
        var before = source.ReadVersion();
        File.WriteAllText(FilePath, "same");
        Assert.Equal(before, source.ReadVersion());
        await Task.Delay(100);
        var count = 0; source.Changed += () => Interlocked.Increment(ref count);
        AtomicFile.WriteAllText(Path.Combine(root, "src", "other.cs"), "another file");
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref count));
        source.Dispose();
        File.WriteAllText(FilePath, "after disposal");
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref count));
    }
    [Fact]
    public async Task Replacing_a_parent_directory_reconnects_to_the_new_file()
    {
        File.WriteAllText(FilePath, "before");
        using var source = new ReviewSource(root, "src/file.cs");
        var signal = Signal(); source.Changed += () => signal.TrySetResult();
        Directory.Move(Path.Combine(root, "src"), Path.Combine(root, "old-src"));
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Reconnect();
        Assert.Equal("missing", source.ReadVersion());
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(FilePath, "replacement");
        // Reconnect after the ancestor's creation hint, then observe another atomic save.
        await Task.Delay(100); source.Reconnect();
        Assert.Equal(WorkspaceVersion.Hash("replacement"), source.ReadVersion());
        signal = Signal(); AtomicFile.WriteAllText(FilePath, "next save");
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkspaceVersion.Hash("next save"), source.ReadVersion());
    }
    [Fact]
    public void Unsafe_paths_and_unreadable_text_fail_without_caching_bad_versions()
    {
        Assert.Throws<SgException>(() => new ReviewSource(root, "../escape.cs"));
        Assert.Throws<SgException>(() => new ReviewSource(root, ".git/config"));
        using var source = new ReviewSource(root, "src/file.cs");
        File.WriteAllText(FilePath, "binary\0data");
        Assert.Throws<SgException>(() => source.ReadVersion());
        File.WriteAllText(FilePath, new string('a', CodeReview.MaxFileBytes + 1));
        Assert.Throws<SgException>(() => source.ReadVersion());
        File.WriteAllText(FilePath, "readable again");
        Assert.Equal(WorkspaceVersion.Hash("readable again"), source.ReadVersion());
    }
}
