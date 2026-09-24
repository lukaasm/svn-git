namespace Sg.Core.Tests;

[Collection(ReviewNotificationCollection.Name)]
public sealed class ReviewFeedTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "sg-review-feed-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(directory, "review.json");
    public ReviewFeedTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);
    static CodeReviewData Data(string body)
    {
        var text = "source\n"; var hash = WorkspaceVersion.Hash(text);
        return new() { Contents = new() { [hash] = text }, Threads = [new()
        {
            Id = Guid.NewGuid().ToString("N"), Anchor = new("file.cs", "modified", 1, 1, hash, "head", "base"),
            Events = [new(Guid.NewGuid().ToString("N"), [], "comment", body, "Agent", DateTimeOffset.UtcNow, hash)]
        }] };
    }
    [Fact]
    public async Task Atomic_creation_and_replacement_notify_and_read_the_published_document()
    {
        using var feed = new ReviewFeed(FilePath);
        Assert.Null(feed.WatchError);
        Assert.Empty(feed.Read().Data.Threads);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.Changed += () => changed.TrySetResult();
        AtomicFile.WriteAllText(FilePath, CodeReview.Encode(Data("First")));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = feed.Read();
        Assert.Equal("First", Assert.Single(first.Data.Threads).Events[0].Body);
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AtomicFile.WriteAllText(FilePath, CodeReview.Encode(Data("Replacement")));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = feed.Read();
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal("Replacement", Assert.Single(second.Data.Threads).Events[0].Body);
    }
    [Fact]
    public async Task Other_documents_do_not_trigger_refresh_and_disposal_stops_observation()
    {
        using var feed = new ReviewFeed(FilePath);
        var count = 0;
        feed.Changed += () => Interlocked.Increment(ref count);
        AtomicFile.WriteAllText(Path.Combine(directory, "other.json"), CodeReview.Encode(Data("Other worktree")));
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref count));
        feed.Dispose();
        AtomicFile.WriteAllText(FilePath, CodeReview.Encode(Data("After closing")));
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref count));
    }
    [Fact]
    public void Reads_recover_from_invalid_metadata_and_catch_changes_without_relying_on_events()
    {
        using var feed = new ReviewFeed(FilePath);
        AtomicFile.WriteAllText(FilePath, CodeReview.Encode(Data("Before")));
        var before = feed.Read();
        File.WriteAllText(FilePath, "incomplete");
        Assert.Throws<SgException>(() => feed.Read());
        Assert.Equal("Before", Assert.Single(before.Data.Threads).Events[0].Body);
        AtomicFile.WriteAllText(FilePath, CodeReview.Encode(Data("Recovered")));
        Assert.Equal("Recovered", Assert.Single(feed.Read().Data.Threads).Events[0].Body);
        File.Delete(FilePath);
        Assert.Empty(feed.Read().Data.Threads);
    }
}
