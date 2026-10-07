using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// One commit out of the middle of a branch: sent to the server on its own, written into the checkout on
/// its own, put on the shelf, or thrown away. The commits under and over it stay on the branch every time,
/// and what cannot be done without the commits around it is refused before anything moves.
/// </summary>
public sealed class CommitPickTests : IDisposable
{
    Fixture? _fixture;
    Fixture f => _fixture ??= new();
    public void Dispose() => _fixture?.Dispose();

    string _wt = "";
    Git Git => f.Root.Git;

    /// <summary>A branch with three commits, one new file each, oldest first: one, two, three.</summary>
    void Branch(string name = "feature-pick")
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, name, f.Co).Path;
        Git.Config("user.name", "Test");
        Git.Config("user.email", "test@localhost");
        Commit("one", "the first commit");
        Commit("two", "the second commit");
        Commit("three", "the third commit");
    }

    void Commit(string file, string message, string? text = null)
    {
        Fixture.Put(_wt, "fort/dev/" + file + ".cpp", text ?? file + "\n");
        Git.Ok(_wt, "add", "-A");
        Git.Ok(_wt, "commit", "-q", "-m", message);
    }

    /// <summary>The branch's own commits, newest first.</summary>
    List<string> Own() => Git.RevList(_wt, f.Root.SnapshotRef(f.Co) + "..HEAD");
    List<string> Subjects() => Own().Select(Git.Subject).ToList();
    string Sha(string subject) => Own().Single(s => Git.Subject(s) == subject);
    bool Has(string file) => File.Exists(Path.Combine(_wt, "fort", "dev", file + ".cpp"));

    bool InSvn(string file)
    {
        try { return f.Cat(f.GameUrl + "/branches/fort/dev/" + file + ".cpp").Trim() == file; }
        catch (SgException) { return false; }
    }

    bool InCheckout(string file) => File.Exists(Path.Combine(f.Checkout, "fort", "dev", file + ".cpp"));

    // ---- discard ----

    [Fact]
    public void Discarding_a_commit_from_the_middle_replays_the_ones_above_it()
    {
        Branch();
        var before = Git.HeadSha(_wt);
        var r = Ops.Discard(f.Root, _wt, Sha("the second commit"));

        Assert.Equal(["the third commit", "the first commit"], Subjects());
        Assert.Equal(1, r.Replayed);
        Assert.Null(r.Shelf);
        Assert.True(Has("one"));
        Assert.False(Has("two"));
        Assert.True(Has("three"));
        Assert.True(Git.IsClean(_wt));
        Assert.Equal(r.Tip, Git.HeadSha(_wt));

        // Nothing is lost for good: Activity keeps the branch as it was, and makes a branch of it again.
        var record = Operations.Read(f.Root, r.Operation);
        Assert.Equal("Discard commit", record.Kind);
        Assert.Equal(before, Git.RefSha(record.Checkpoint));
        var back = Operations.RestoreCheckpoint(f.Root, r.Operation, "feature-pick-back");
        Assert.True(File.Exists(Path.Combine(back, "fort", "dev", "two.cpp")));
    }

    [Fact]
    public void Discarding_the_newest_commit_moves_the_branch_back_by_one()
    {
        Branch();
        var r = Ops.Discard(f.Root, _wt, Sha("the third commit")[..10]);
        Assert.Equal(0, r.Replayed);
        Assert.Equal(["the second commit", "the first commit"], Subjects());
        Assert.False(Has("three"));
    }

    [Fact]
    public void A_commit_the_ones_above_build_on_is_not_discarded_and_nothing_moves()
    {
        Branch();
        Commit("shared", "shared starts", "a\n");
        Commit("shared", "shared moves on", "b\n");
        Commit("shared", "shared moves again", "c\n");
        var before = Git.HeadSha(_wt);

        var ex = Assert.Throws<SgException>(() => Ops.Discard(f.Root, _wt, Sha("shared moves on")));
        Assert.Contains("shared moves again", ex.Message);
        Assert.Contains("fort/dev/shared.cpp", ex.Message);
        Assert.Equal(before, Git.HeadSha(_wt));
        Assert.True(Git.IsClean(_wt));
    }

    [Fact]
    public void A_snapshot_is_not_the_branch_s_to_discard()
    {
        Branch();
        var snap = Git.RefSha(f.Root.SnapshotRef(f.Co))!;
        Assert.Throws<SgException>(() => Ops.Discard(f.Root, _wt, snap));
        Assert.Equal(3, Own().Count);
    }

    [Fact]
    public void Uncommitted_work_stops_a_discard()
    {
        Branch();
        Fixture.Put(_wt, "fort/dev/one.cpp", "edited\n");
        Assert.Throws<SgException>(() => Ops.Discard(f.Root, _wt, Sha("the second commit")));
        Assert.Equal(3, Own().Count);
    }

    [Fact]
    public void A_discard_is_put_back_while_the_branch_has_not_moved_since()
    {
        Branch();
        var before = Git.HeadSha(_wt);
        var r = Ops.Discard(f.Root, _wt, Sha("the second commit"));
        Ops.PutBack(f.Root, _wt, r);
        Assert.Equal(before, Git.HeadSha(_wt));
        Assert.True(Has("two"));

        // Once something else happened on the branch, putting back would drop it, so it refuses.
        var again = Ops.Discard(f.Root, _wt, Sha("the second commit"));
        Commit("four", "the fourth commit");
        Assert.Throws<SgException>(() => Ops.PutBack(f.Root, _wt, again));
        Assert.Equal("the fourth commit", Git.Subject(Git.HeadSha(_wt)));
    }

    [Fact]
    public void Putting_a_shelved_commit_back_takes_it_off_the_shelf_again()
    {
        Branch();
        var before = Git.HeadSha(_wt);
        var r = Ops.Stash(f.Root, _wt, Sha("the second commit"));
        Ops.PutBack(f.Root, _wt, r);
        Assert.Equal(before, Git.HeadSha(_wt));
        Assert.Null(Git.RefSha(Shelf.RefPrefix + r.Shelf));
    }

    // ---- stash ----

    [Fact]
    public void Stashing_a_commit_puts_its_change_on_the_shelf_and_restoring_brings_it_back_uncommitted()
    {
        Branch();
        var r = Ops.Stash(f.Root, _wt, Sha("the second commit"));

        Assert.Equal(["the third commit", "the first commit"], Subjects());
        Assert.False(Has("two"));
        Assert.NotNull(r.Shelf);
        var shelf = Shelf.Read(f.Root, r.Shelf!);
        Assert.Equal("the second commit", shelf.Title);
        Assert.False(shelf.IsCheckout);
        Assert.Equal("feature-pick", shelf.Branch);
        Assert.Equal(Path.GetFullPath(_wt).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(shelf.Path).TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(["fort/dev/two.cpp"], Shelf.Changes(f.Root, shelf).Select(e => e.Path));
        Assert.Equal("Shelve commit", Operations.Read(f.Root, r.Operation).Kind);

        var back = Shelf.Restore(f.Root, shelf.Id);
        Assert.Empty(back.Conflicted);
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(_wt, "fort", "dev", "two.cpp")));
        Assert.Equal(2, Own().Count);   // back as a change in the worktree, not as a commit
        Assert.Contains(Git.StatusEntries(_wt, untracked: true), e => e.Path == "fort/dev/two.cpp");
    }

    [Fact]
    public void A_stashed_commit_merges_back_into_a_file_that_moved_on()
    {
        Branch();
        Commit("shared", "shared starts", "1\n2\n3\n4\n5\n6\n7\n8\n");
        Commit("shared", "change the top", "one\n2\n3\n4\n5\n6\n7\n8\n");
        Commit("shared", "change the bottom", "one\n2\n3\n4\n5\n6\n7\neight\n");

        var r = Ops.Stash(f.Root, _wt, Sha("change the top"), "the top, for later");
        Assert.Equal("1\n2\n3\n4\n5\n6\n7\neight\n", File.ReadAllText(Path.Combine(_wt, "fort", "dev", "shared.cpp")));
        Assert.Equal("the top, for later", Shelf.Read(f.Root, r.Shelf!).Title);

        var back = Shelf.Restore(f.Root, r.Shelf!);
        Assert.Empty(back.Conflicted);
        Assert.Equal("one\n2\n3\n4\n5\n6\n7\neight\n", File.ReadAllText(Path.Combine(_wt, "fort", "dev", "shared.cpp")));
    }

    // ---- one commit to the server ----

    [Fact]
    public void The_preview_of_one_commit_names_only_its_files_and_its_message()
    {
        Branch();
        var two = Sha("the second commit");
        var p = Push.Preview(f.Root, _wt, PushScope.Only(two));

        Assert.Equal(3, p.Commits.Count);
        Assert.Equal(1, p.Sending);
        Assert.True(p.Partial);
        Assert.Equal(two, p.Picked);
        Assert.Equal(["fort/dev/two.cpp"], p.Entries.Select(e => e.Path));
        Assert.Equal("the second commit", p.DefaultMessage);
        Assert.True(p.Ready, string.Join("; ", p.Checks.Where(c => !c.Ok).Select(c => c.Name + ": " + c.Detail)));
        Assert.Contains(p.Checks, c => c.Id == PushChecks.Alone && c.Ok);
        // What the diff shows is the snapshot with this one commit in it, nothing under it.
        Assert.Equal(Git.RefSha(f.Root.SnapshotRef(f.Co)), p.Base);
        Assert.Equal("", Git.ShowText(p.Tip, "fort/dev/one.cpp"));
    }

    [Fact]
    public void Pushing_one_commit_from_the_middle_sends_only_it_and_keeps_the_others()
    {
        Branch();
        var r = Push.Run(f.Root, _wt, null, interactive: true, scope: PushScope.Only(Sha("the second commit")));

        Assert.All(r.Groups, g => Assert.Equal("committed", g.State));
        Assert.False(r.AllCommitted);
        Assert.True(r.OnPurpose);
        Assert.Equal(TaskState.Succeeded, TaskResults.Describe(r).State);
        Assert.False(InSvn("one"));
        Assert.True(InSvn("two"));
        Assert.False(InSvn("three"));

        // The two that stayed are still the commits they were, now over a snapshot that has the middle one.
        Assert.Equal(["the third commit", "the first commit"], Subjects());
        Assert.Contains("2 commits still on the branch", r.BranchState);
        Assert.True(Has("one") && Has("two") && Has("three"));
        Assert.True(Git.IsClean(_wt));
        var head = f.Root.Svn.Info(f.Checkout, "fort/dev").Revision;
        Assert.Equal("the second commit", f.Root.Svn.Log(f.Checkout, "fort/dev", head, head, 1).Single().Message.Trim());

        // And the rest can go after it.
        var rest = Push.Run(f.Root, _wt, "the rest of the branch", interactive: true);
        Assert.True(rest.AllCommitted);
        Assert.True(InSvn("one") && InSvn("three"));
    }

    [Fact]
    public void One_commit_is_found_again_after_the_rebase_a_push_starts_with()
    {
        Branch();
        var two = Sha("the second commit");
        // Someone else commits first, so the push's sync moves the snapshot and its rebase renames every commit.
        var other = f.OtherWc(f.GameUrl + "/branches/fort/dev");
        Fixture.Put(other, "elsewhere.cpp", "elsewhere\n");
        f.Svn.Ok(other, "add", "elsewhere.cpp");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "someone else's commit");

        var r = Push.Run(f.Root, _wt, "only the second one", interactive: true, scope: PushScope.Only(two));
        Assert.All(r.Groups, g => Assert.Equal("committed", g.State));
        Assert.True(InSvn("two"));
        Assert.False(InSvn("one") || InSvn("three"));
        Assert.Equal(["the third commit", "the first commit"], Subjects());
        Assert.True(Has("elsewhere"));
    }

    [Fact]
    public void A_commit_that_needs_the_ones_under_it_does_not_go_on_its_own()
    {
        Branch();
        Commit("shared", "shared starts here", "a\n");
        Commit("shared", "shared changes after", "b\n");
        var before = Git.HeadSha(_wt);

        var p = Push.Preview(f.Root, _wt, PushScope.Only(Sha("shared changes after")));
        var alone = Assert.Single(p.Checks, c => c.Id == PushChecks.Alone);
        Assert.False(alone.Ok);
        Assert.Equal(["fort/dev/shared.cpp"], alone.Paths);
        Assert.False(p.Ready);

        var ex = Assert.Throws<SgException>(() => Push.Run(f.Root, _wt, "only the change", interactive: true, scope: PushScope.Only(Sha("shared changes after"))));
        Assert.Contains("without the commits under it", ex.Message);
        Assert.False(InSvn("shared"));
        Assert.Equal(before, Git.HeadSha(_wt));
    }

    [Fact]
    public void Pushing_the_only_commit_on_its_own_is_the_whole_branch()
    {
        f.Setup();
        _wt = Ops.Branch(f.Root, "feature-single", f.Co).Path;
        Commit("solo", "the only commit here");
        var r = Push.Run(f.Root, _wt, null, interactive: true, scope: PushScope.Only(Git.HeadSha(_wt)));
        Assert.True(r.AllCommitted);
        Assert.Empty(Own());
        Assert.True(InSvn("solo"));
    }

    [Fact]
    public void Applying_one_commit_writes_only_it_into_the_checkout()
    {
        Branch();
        var before = Git.HeadSha(_wt);
        var r = Push.Run(f.Root, _wt, null, interactive: true, scope: PushScope.Only(Sha("the second commit")), finish: PushFinish.LeaveInCheckout);

        Assert.True(r.AppliedOnly);
        Assert.All(r.Groups, g => Assert.Equal("applied", g.State));
        Assert.True(InCheckout("two"));
        Assert.False(InCheckout("one"));
        Assert.False(InCheckout("three"));
        Assert.False(InSvn("two"));
        Assert.Equal(before, Git.HeadSha(_wt));
    }

    [Fact]
    public void A_commit_picked_before_a_rewrite_is_no_longer_the_branch_s_own()
    {
        Branch();
        var three = Sha("the third commit");
        Assert.True(Push.IsOwnCommit(f.Root, _wt, three[..10]));
        Ops.Discard(f.Root, _wt, Sha("the second commit"));
        Assert.False(Push.IsOwnCommit(f.Root, _wt, three));
        Assert.True(Push.IsOwnCommit(f.Root, _wt, Sha("the third commit")));
        Assert.False(Push.IsOwnCommit(f.Root, _wt, Git.RefSha(f.Root.SnapshotRef(f.Co))!));
    }

    [Fact]
    public void A_commit_that_is_not_the_branch_s_own_is_refused()
    {
        Branch();
        var snap = Git.RefSha(f.Root.SnapshotRef(f.Co))!;
        Assert.Throws<SgException>(() => Push.Preview(f.Root, _wt, PushScope.Only(snap)));
        Assert.Throws<SgException>(() => Push.Run(f.Root, _wt, "a snapshot again", interactive: true, scope: PushScope.Only(snap)));
    }
}
