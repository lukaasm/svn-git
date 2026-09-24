namespace Sg.Core.Tests;

public sealed class ReviewInboxTests
{
    [Fact]
    public async Task Inbox_is_read_only_isolates_document_errors_and_supports_filtered_pagination()
    {
        using var fixture = new Fixture(); fixture.Setup();
        var root = fixture.Root;
        var alpha = Ops.Branch(root, "alpha", fixture.Co).Path;
        var beta = Ops.Branch(root, "beta", fixture.Co).Path;
        using var inbox = new ReviewInbox(root);
        Assert.All(inbox.Read(), w => Assert.Empty(w.Items));
        Assert.False(File.Exists(root.Git.PrivateFile(alpha, "review-id")));
        Assert.False(Directory.Exists(Path.Combine(root.StorePath, "code-reviews")));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inbox.Changed += () => changed.TrySetResult();
        var first = CodeReview.Add(root, alpha, CodeReview.ReadFile(root, alpha, "CMakeLists.txt"), "modified", 1, 1, "Clarify the build configuration.", "Reviewer");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = CodeReview.Add(root, beta, CodeReview.ReadFile(root, beta, "CMakeLists.txt"), "original", 1, 1, "Keep compatibility.");
        var context = CodeReview.Context(root, beta, second.Id);
        CodeReview.Address(root, beta, second.Id, "resolve", "Compatibility verified.", second.Revision, "Agent", context.Version);
        var progress = new List<ReviewInboxWorktree>();
        var entries = inbox.Read(progress.Add);
        Assert.Equal(2, progress.Count); Assert.All(entries, w => Assert.Null(w.Error));
        Assert.Equal(first.Id, Assert.Single(ReviewInbox.Query(root).Threads).Thread.Id);
        var resolved = Assert.Single(ReviewInbox.Query(root, "resolved", "AGENT", "beta").Threads);
        Assert.Equal(second.Id, resolved.Thread.Id); Assert.Equal(beta, resolved.Worktree);
        Assert.Empty(ReviewInbox.Query(root, "open", worktree: "beta").Threads);
        Assert.Equal(2, ReviewInbox.Query(root, "all", "CMake").Total);

        // Summary reads do not need current source, even when it is unavailable to inline review.
        Fixture.Put(alpha, "CMakeLists.txt", "binary\0data");
        Assert.Single(ReviewInbox.Query(root).Threads);
        var betaFile = CodeReview.ExistingDocument(root, beta)!;
        var valid = File.ReadAllText(betaFile);
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AtomicFile.WriteAllText(betaFile, "incomplete");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var partial = ReviewInbox.Query(root, "all");
        Assert.Single(partial.Threads); Assert.Equal("beta", Assert.Single(partial.Errors).Branch);
        AtomicFile.WriteAllText(betaFile, valid);
        Assert.Equal(2, ReviewInbox.Query(root, "all").Total);

        var data = CodeReview.Read(root, alpha);
        for (var i = 0; i < 105; i++) data.Threads.Add(new()
        {
            Id = Guid.NewGuid().ToString("N"), Anchor = first.Anchor,
            Events = [new(Guid.NewGuid().ToString("N"), [], "comment", "Paged feedback " + i, "Reviewer", DateTimeOffset.UtcNow.AddMinutes(i + 1), first.Events[0].Version)]
        });
        AtomicFile.WriteAllText(CodeReview.ExistingDocument(root, alpha)!, CodeReview.Encode(data));
        var page = ReviewInbox.Query(root, query: "Paged feedback");
        Assert.Equal(105, page.Total); Assert.Equal(100, page.Threads.Length); Assert.Equal(100, page.NextOffset);
        var rest = ReviewInbox.Query(root, query: "Paged feedback", offset: page.NextOffset!.Value);
        Assert.Equal(5, rest.Threads.Length); Assert.Null(rest.NextOffset);
        Assert.Empty(page.Threads.Select(t => t.Thread.Id).Intersect(rest.Threads.Select(t => t.Thread.Id)));
        Assert.Equal("Paged feedback 104", page.Threads[0].Thread.Comment);
        Assert.Throws<SgException>(() => ReviewInbox.Query(root, state: "unknown"));
        Assert.Throws<SgException>(() => ReviewInbox.Query(root, offset: -1));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using (Cancellation.Use(cancelled.Token)) Assert.ThrowsAny<OperationCanceledException>(() => inbox.Read());
    }
}
