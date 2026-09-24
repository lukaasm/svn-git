namespace Sg.Core.Tests;

public sealed class CodeReviewTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();
    string Setup()
    {
        f.Setup();
        return Ops.Branch(f.Root, "review", f.Co).Path;
    }
    CodeThread Comment(string path) => CodeReview.Add(f.Root, path, CodeReview.ReadFile(f.Root, path, "CMakeLists.txt"), "modified", 1, 1, "Explain this project name.");

    [Fact]
    public void Addressing_requires_current_feedback_and_code_and_survives_move()
    {
        var path = Setup();
        var identity = CodeReview.WorktreeIdentity(f.Root, path);
        using var feed = CodeReview.Follow(f.Root, path);
        Assert.Equal(identity, feed.Identity);
        var source = File.ReadAllText(Path.Combine(path, "CMakeLists.txt"));
        using var sourceFeed = CodeReview.FollowSource(f.Root, path, "CMakeLists.txt");
        Assert.Equal(CodeReview.ReadFile(f.Root, path, "CMakeLists.txt").Version, sourceFeed.ReadVersion());
        var thread = Comment(path);
        Assert.Equal(thread.Id, Assert.Single(feed.Read().Data.Threads).Id);
        Assert.Equal(source, File.ReadAllText(Path.Combine(path, "CMakeLists.txt")));
        var context = CodeReview.Context(f.Root, path, thread.Id);
        Fixture.Put(path, "CMakeLists.txt", "project(renamed)\n");
        Assert.Throws<SgException>(() => CodeReview.Address(f.Root, path, thread.Id, "resolve", "Fixed", thread.Revision, version: context.Version));
        context = CodeReview.Context(f.Root, path, thread.Id);
        Assert.Equal("changed", context.Location);
        var request = Guid.NewGuid().ToString("N");
        var resolved = CodeReview.Address(f.Root, path, thread.Id, "resolve", "Renamed; build checked.", thread.Revision, version: context.Version, requestId: request);
        Assert.Equal("resolved", resolved.State);
        Assert.Equal(resolved.Revision, CodeReview.Address(f.Root, path, thread.Id, "resolve", "Renamed; build checked.", thread.Revision, version: context.Version, requestId: request).Revision);
        Assert.Throws<SgException>(() => CodeReview.Address(f.Root, path, thread.Id, "reopen", "More feedback", thread.Revision));
        var reply = CodeReview.Address(f.Root, path, thread.Id, "reply", "Verified", resolved.Revision);
        Assert.Equal("resolved", reply.State);
        var moved = path + "-moved";
        f.Root.Git.Ok(null, "worktree", "move", path, moved);
        f.Root.Git.Ok(moved, "branch", "-m", "renamed");
        Assert.Equal(thread.Id, Assert.Single(CodeReview.Read(f.Root, moved).Threads).Id);
        Assert.Equal(identity, CodeReview.WorktreeIdentity(f.Root, moved));
        Assert.Equal(source, CodeReview.Context(f.Root, moved, thread.Id).Original);
    }

    [Fact]
    public void Independent_histories_merge_without_losing_feedback_and_gate_readiness()
    {
        var path = Setup();
        Review.RunChecks(f.Root, path);
        Review.MarkReady(f.Root, path);
        var thread = Comment(path);
        var baseData = CodeReview.Read(f.Root, path);
        Assert.Equal("Open code review comments", Review.Status(f.Root, path));
        Assert.Throws<SgException>(() => Review.MarkReady(f.Root, path));
        var context = CodeReview.Context(f.Root, path, thread.Id);
        CodeReview.Address(f.Root, path, thread.Id, "resolve", "Existing name is intentional", thread.Revision, version: context.Version);
        var remote = CodeReview.Decode(CodeReview.Encode(baseData));
        remote.Threads[0].Events.Add(new(Guid.NewGuid().ToString("N"), thread.Heads, "reply", "Please document why", "Other machine", DateTimeOffset.UtcNow, ""));
        var merged = CodeReview.Import(f.Root, path, remote);
        var conflict = Assert.Single(merged.Threads);
        Assert.True(conflict.Conflict);
        Assert.Equal("open", conflict.State);
        Assert.Equal(3, conflict.Events.Count);
        Assert.Equal(CodeReview.Encode(merged), CodeReview.Encode(CodeReview.Merge(remote, merged)));
        var replied = CodeReview.Address(f.Root, path, thread.Id, "reply", "Reviewing both responses", conflict.Revision);
        Assert.True(replied.Conflict);
        var resolved = CodeReview.Address(f.Root, path, thread.Id, "resolve", "Documented rationale in the discussion", replied.Revision, version: context.Version);
        Assert.False(resolved.Conflict);
        Review.MarkReady(f.Root, path);
        Assert.Equal("Ready for this version", Review.Status(f.Root, path));
        CodeReview.Address(f.Root, path, thread.Id, "reopen", "Needs another look", resolved.Revision);
        Assert.Equal("Open code review comments", Review.Status(f.Root, path));
    }

    [Fact]
    public void Scoped_backup_restores_comments_and_context_without_uncommitted_backup()
    {
        var path = Setup();
        var other = Ops.Branch(f.Root, "other", f.Co).Path;
        Comment(other);
        Fixture.Put(path, "CMakeLists.txt", "project(draft)\n");
        var thread = Comment(path);
        var remote = Path.Combine(f.Base, "review-backup.git");
        Proc.Run("git", ["init", "-q", "--bare", remote], null, f.Log).EnsureOk();
        Backup.Set(f.Root, remote);
        f.Root.Config.Backup!.Uncommitted = false;
        var result = Backup.Run(f.Root, worktree: "review");
        Assert.True(result.Ok, string.Join("\n", result.Items.Select(i => i.Why)));
        Assert.Contains(result.Items, i => i.Kind == "review" && i.State == "pushed");
        Assert.DoesNotContain(f.Root.Git.LsRemote(remote).Keys, k => k.EndsWith("/other"));
        Assert.True(Assert.Single(Backup.List(f.Root), e => e.Name == "review").HasReview);
        var restored = Backup.Restore(f.Root, "review", asBranch: "restored");
        Assert.True(restored.Ok);
        Assert.Equal(1, restored.ReviewThreads);
        var context = CodeReview.Context(f.Root, restored.Path, thread.Id);
        Assert.Equal("project(draft)\n", context.Original);
        Assert.Contains("project(fort)", context.Current);
        Assert.Equal("changed", context.Location);
        var receipt = Backup.Coverage(f.Root, path);
        Assert.Contains(receipt.Coverage, c => c.Kind == "review" && c.Files.Contains("CMakeLists.txt"));
        CodeReview.Address(f.Root, path, thread.Id, "reply", "Additional feedback", thread.Revision);
        Assert.StartsWith("Changed since receipt", Backup.LocalReceiptStatus(f.Root, path, receipt));
        Backup.Exclude(f.Root, "review");
        var before = f.Root.Git.LsRemote(remote);
        Backup.Run(f.Root);
        var after = f.Root.Git.LsRemote(remote);
        var key = before.Keys.Single(k => k.StartsWith("refs/sg/review/") && k.EndsWith("/review"));
        Assert.Equal(before[key], after[key]);
    }

    [Fact]
    public void Backup_merges_two_machines_and_rejects_a_stale_restore_preview()
    {
        var path = Setup();
        var original = Comment(path);
        var remote = Path.Combine(f.Base, "shared.git");
        Proc.Run("git", ["init", "-q", "--bare", remote], null, f.Log).EnsureOk();
        Backup.Set(f.Root, remote);
        Assert.True(Backup.Run(f.Root).Ok);

        var farDirectory = Path.Combine(f.Base, "far");
        Directory.CreateDirectory(farDirectory);
        var checkout = Path.Combine(farDirectory, "mono");
        f.Svn.Ok(null, "checkout", "--non-interactive", f.MonoUrl + "/trunk", checkout);
        var far = Ops.Init(farDirectory, f.Log, fsmonitor: false);
        Ops.CheckoutAdd(far, checkout, skip: ["fort/builds"]);
        Backup.Set(far, remote);
        var restored = Backup.Restore(far, "review");
        Assert.True(restored.Ok);
        var catalog = Backup.Browse(f.Root);
        var preview = Backup.Preview(f.Root, catalog, catalog.Items.Single(i => i.Kind == "branch" && i.Name == "review"));
        var current = CodeReview.Context(far, restored.Path, original.Id);
        CodeReview.Address(far, restored.Path, original.Id, "resolve", "Explained on the other machine", original.Revision, version: current.Version);
        Assert.True(Backup.Run(far).Ok);
        CodeReview.Address(f.Root, path, original.Id, "reply", "Please also add a test", original.Revision);
        Assert.Throws<SgException>(() => Backup.Restore(f.Root, "review", asBranch: "stale-preview", expectedRefs: preview.ExpectedRefs));
        Assert.False(Directory.Exists(Path.Combine(f.RootDir, "stale-preview")));
        Assert.True(Backup.Run(f.Root).Ok);
        var merged = Assert.Single(CodeReview.Read(f.Root, path).Threads);
        Assert.True(merged.Conflict);
        Assert.Equal("open", merged.State);
        Assert.Equal(3, merged.Events.Count);
        Assert.True(Backup.Pull(far, "review").Ok);
        Assert.Equal(merged.Revision, Assert.Single(CodeReview.Read(far, restored.Path).Threads).Revision);
    }

    [Fact]
    public void Corrupt_metadata_and_paths_are_rejected_without_touching_source()
    {
        var path = Setup();
        Assert.Throws<SgException>(() => CodeReview.ReadFile(f.Root, path, "../sg.json"));
        Assert.Throws<SgException>(() => CodeReview.ReadFile(f.Root, path, ".git/config"));
        Assert.Throws<SgException>(() => CodeReview.Decode("{\"schema\":1,\"threads\":[null],\"contents\":{}}"));
        var thread = Comment(path);
        var data = CodeReview.Read(f.Root, path);
        data.Contents[data.Contents.Keys.Single()] = "corrupt";
        Assert.Throws<SgException>(() => CodeReview.Import(f.Root, path, data));
        Assert.Equal(thread.Revision, Assert.Single(CodeReview.Read(f.Root, path).Threads).Revision);
    }
}
