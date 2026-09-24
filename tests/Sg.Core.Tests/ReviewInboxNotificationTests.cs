namespace Sg.Core.Tests;

// Native notifications use thread-pool callbacks. Verify delivery apart from the parallel suites
// that synchronously wait on thousands of Git/SVN processes; content reads remain parallel-tested.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReviewNotificationCollection
{
    public const string Name = "Review file notifications";
}

[Collection(ReviewNotificationCollection.Name)]
public sealed class ReviewInboxNotificationTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "sg-review-inbox-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_directory_creation_and_atomic_replacement_notify_the_inbox(bool existing)
    {
        // The inbox observer needs only its metadata directory, not SVN checkouts or branch setup.
        var root = SgRoot.Create(directory, new SgConfig(), new NullLog());
        var file = Path.Combine(root.StorePath, "code-reviews", Guid.NewGuid().ToString("N") + ".json");
        if (existing) AtomicFile.WriteAllText(file, CodeReview.Encode(new()));
        using var inbox = new ReviewInbox(root);
        Assert.Null(inbox.WatchError);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inbox.Changed += () => changed.TrySetResult();
        AtomicFile.WriteAllText(file, CodeReview.Encode(new()));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(inbox.WatchError);
        Assert.True(File.Exists(file));
    }
}
