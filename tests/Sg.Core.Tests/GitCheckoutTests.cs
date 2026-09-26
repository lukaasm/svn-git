using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// A git clone as the main checkout: the whole workflow an SVN checkout has - register, sync, branch,
/// push, the changes window, shelves, blame, merge, server branches, export - against a bare repository
/// as the server.
/// </summary>
public sealed class GitCheckoutTests : IDisposable
{
    GitFixture? _fixture;
    GitFixture f => _fixture ??= new();
    public void Dispose() => _fixture?.Dispose();

    string SnapshotSha => f.Root.Git.RefSha(f.Root.SnapshotRef(f.Co))!;
    SnapshotMeta Snapshot => SnapshotMeta.Parse(f.Root.Git.Body(SnapshotSha));

    string Worktree(string name = "feature")
    {
        var wt = Ops.Branch(f.Root, name, f.Co).Path;
        return wt;
    }

    void CommitIn(string wt, string message)
    {
        f.Root.Git.Ok(wt, "add", "-A");
        f.Root.Git.Ok(wt, "commit", "-q", "-m", message);
    }

    // ---- registering and syncing ----

    [Fact]
    public void A_clone_registers_and_its_snapshot_is_the_server_branch()
    {
        f.Setup();
        Assert.Equal(CheckoutKind.Git, f.Co.Kind);
        Assert.Equal("origin", f.Co.Remote);
        Assert.Equal("main", f.Co.Branch);
        Assert.EndsWith("#main", f.Co.Url);
        Assert.Equal(GitLocation.Parse(f.Co.Url).Url, f.Co.ReposRoot);

        var meta = Snapshot;
        Assert.Equal(CheckoutKind.Git, meta.Kind);
        Assert.Equal(f.RemoteHead(), meta.Commit);
        Assert.Equal(1, meta.Revision);
        Assert.Equal(f.Co.Url, meta.Url);

        var files = f.Root.Git.LsTree(SnapshotSha, null, recursive: true).Select(e => e.Path).ToList();
        Assert.Contains("src/app.cpp", files);
        Assert.DoesNotContain(files, p => p.StartsWith("assets/big", StringComparison.Ordinal));

        // The clone is still the user's own, and nothing in it changed.
        Assert.True(Directory.Exists(Path.Combine(f.Checkout, ".git")));
        Assert.Equal("", f.CloneChanges());
        Assert.Contains(f.Co.Name, File.ReadAllText(f.Root.ConfigPath));
    }

    [Fact]
    public void A_worktree_builds_on_the_snapshot_and_shares_the_skipped_folder()
    {
        f.Setup();
        var wt = Worktree();
        Assert.Equal("int app = 1;\n", GitFixture.Read(wt, "src/app.cpp"));
        Assert.True(File.Exists(Path.Combine(wt, "assets", "big", "blob.bin")));
        Assert.Equal(0, f.Root.Git.CountCommits(f.Root.SnapshotRef(f.Co), "refs/heads/feature"));
    }

    [Fact]
    public void Sync_takes_in_what_someone_else_pushed_and_moves_the_clone()
    {
        f.Setup();
        var theirs = f.OtherCommit("src/app.cpp", "int app = 2;\n", "app two");

        var r = Ops.Sync(f.Root, f.Co);

        Assert.True(r.Changed);
        Assert.Equal(theirs, r.Commit);
        Assert.Equal(2, r.Revision);
        Assert.Contains("app two", f.Root.Git.Body(r.Sha));
        Assert.Equal("int app = 2;\n", GitFixture.Read(f.Checkout, "src/app.cpp"));
        Assert.Equal(theirs, GitFixture.Git(f.Checkout, "rev-parse", "HEAD"));
        // A second sync with nothing new keeps the snapshot.
        Assert.False(Ops.Sync(f.Root, f.Co).Changed);
    }

    [Fact]
    public void Sync_keeps_local_edits_and_merges_one_the_server_also_changed()
    {
        f.Setup();
        GitFixture.Put(f.Checkout, "docs/guide.txt", "line1 mine\r\nline2\r\nline3\r\n");
        GitFixture.Put(f.Checkout, "README.md", "hello from here\r\n");
        f.OtherCommit("docs/guide.txt", "line1\nline2\nline3 theirs\n", "guide three");

        var r = Ops.Sync(f.Root, f.Co);

        Assert.Equal(0, r.Conflicts);
        Assert.Equal("line1 mine\nline2\nline3 theirs\n", GitFixture.Read(f.Checkout, "docs/guide.txt"));
        Assert.Equal("hello from here\n", GitFixture.Read(f.Checkout, "README.md"));
        // The snapshot is the server's, local edits left out.
        Assert.Equal("line1\nline2\nline3 theirs\n", f.Root.Git.ShowText(r.Sha, "docs/guide.txt"));
        Assert.Equal("hello\n", f.Root.Git.ShowText(r.Sha, "README.md"));
    }

    [Fact]
    public void Status_counts_the_clone_s_local_edits()
    {
        f.Setup();
        GitFixture.Put(f.Checkout, "README.md", "changed\n");
        GitFixture.Put(f.Checkout, "scratch.txt", "untracked\n");
        var s = Ops.Status(f.Root, checkSvn: true).Checkouts.Single();
        Assert.Equal(CheckoutKind.Git, s.Kind);
        Assert.Equal(1, s.LocalEdits);
        Assert.Equal(f.RemoteHead(), s.Commit);
        Assert.Equal(Rev.Short(f.RemoteHead()), s.Label);
    }

    [Fact]
    public void The_remote_check_sees_the_server_move()
    {
        f.Setup();
        Assert.False(Ops.RemoteCheck(f.Root, f.Co).Behind);
        f.OtherCommit("src/app.cpp", "int app = 3;\n", "app three");
        var r = Ops.RemoteCheck(f.Root, f.Co);
        Assert.True(r.Behind);
        Assert.Equal(1, r.Commits);
    }

    [Fact]
    public void The_root_is_found_from_inside_a_clone_that_lives_elsewhere()
    {
        using var g = new GitFixture(checkoutInsideRoot: false);
        g.Setup();
        var found = SgRoot.Find(Path.Combine(g.Checkout, "src", "lib"), new NullLog());
        Assert.NotNull(found);
        Assert.Equal(g.RootDir, found!.RootPath, ignoreCase: true);
        Assert.Equal(g.Co.Name, Ops.ResolveCheckout(found, null, Path.Combine(g.Checkout, "src")).Name);
    }

    [Fact]
    public void A_git_url_is_cloned_and_registered()
    {
        var root = Ops.Init(f.RootDir, f.Log, fsmonitor: false);
        var r = Ops.CheckoutFromUrl(root, f.RemoteDir + "#main", Path.Combine(f.RootDir, "fresh"), name: "fresh");
        Assert.Equal(CheckoutKind.Git, r.Checkout.Kind);
        Assert.Equal("main", r.Checkout.Branch);
        Assert.Equal(f.RemoteHead(), r.Snapshot.Commit);
        Assert.True(File.Exists(Path.Combine(f.RootDir, "fresh", "src", "app.cpp")));
    }

    [Fact]
    public void Urls_say_which_server_they_belong_to()
    {
        Assert.Equal(CheckoutKind.Git, GitLocation.KindOfUrl("https://github.com/org/repo"));
        Assert.Equal(CheckoutKind.Git, GitLocation.KindOfUrl("https://example.com/org/repo.git"));
        Assert.Equal(CheckoutKind.Git, GitLocation.KindOfUrl("git@example.com:org/repo.git"));
        Assert.Equal(CheckoutKind.Git, GitLocation.KindOfUrl("https://example.com/org/repo#main"));
        Assert.Equal(CheckoutKind.Svn, GitLocation.KindOfUrl("https://svn.example.com/svn/mono/branches/fort"));
        Assert.Equal(CheckoutKind.Svn, GitLocation.KindOfUrl("svn+ssh://svn.example.com/mono/trunk"));
        Assert.Equal(("https://example.com/r.git", "feature/x"), GitLocation.Parse("https://example.com/r.git#feature/x"));
        Assert.Equal("r", GitLocation.RepoName("https://example.com/org/r.git#main"));
    }

    [Fact]
    public void Renaming_the_checkout_keeps_its_tunnel_and_history()
    {
        f.Setup();
        Ops.UpdateCheckout(f.Root, f.Co, new Ops.CheckoutEdit(Name: "renamed"));
        Assert.Equal("renamed", f.Co.Name);
        f.OtherCommit("src/app.cpp", "int app = 4;\n", "app four");
        var r = Ops.Sync(f.Root, f.Co);
        Assert.True(r.Changed);
        Assert.Equal(r.Sha, f.Root.Git.HeadSha(f.Checkout));
    }

    // ---- push ----

    [Fact]
    public void Push_sends_the_branch_as_one_commit_and_puts_the_branch_back_on_the_server()
    {
        f.Setup();
        var wt = Worktree();
        GitFixture.Put(wt, "src/app.cpp", "int app = 10;\n");
        GitFixture.Put(wt, "src/new.cpp", "int fresh = 1;\n");
        CommitIn(wt, "change the app");
        File.Delete(Path.Combine(wt, "docs", "guide.txt"));
        CommitIn(wt, "drop the guide");
        var before = f.RemoteHead();

        var r = Push.Run(f.Root, wt, "Two changes, sent from sg", interactive: true);

        Assert.True(r.AllCommitted);
        var head = f.RemoteHead();
        Assert.NotEqual(before, head);
        Assert.Equal(before, f.ParentOf(head));
        Assert.Equal("Two changes, sent from sg", f.MessageOf(head));
        Assert.Equal("Tester", f.AuthorOf(head));
        Assert.Equal("int app = 10;", f.RemoteShow(head, "src/app.cpp"));
        Assert.Equal("int fresh = 1;", f.RemoteShow(head, "src/new.cpp"));
        Assert.False(f.RemoteHas(head, "docs/guide.txt"));
        Assert.Equal(head, r.Groups.Single().Commit);
        Assert.Equal(head, r.Commit);

        // The clone is on the new commit with nothing left over, and the branch is the snapshot again.
        Assert.Equal(head, GitFixture.Git(f.Checkout, "rev-parse", "HEAD"));
        Assert.Equal("", f.CloneChanges());
        Assert.Equal("int app = 10;\n", GitFixture.Read(f.Checkout, "src/app.cpp"));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "docs", "guide.txt")));
        Assert.Equal(head, Snapshot.Commit);
        Assert.Equal(0, f.Root.Git.CountCommits(f.Root.SnapshotRef(f.Co), "refs/heads/feature"));
    }

    [Fact]
    public void Push_in_batches_makes_one_server_commit_per_batch()
    {
        f.Setup();
        var wt = Worktree();
        GitFixture.Put(wt, "src/app.cpp", "int app = 20;\n");
        CommitIn(wt, "first");
        var first = f.Root.Git.HeadSha(wt);
        GitFixture.Put(wt, "src/lib/util.cpp", "int util = 20;\n");
        CommitIn(wt, "second");
        var second = f.Root.Git.HeadSha(wt);
        var before = f.RemoteHead();

        var r = Push.Run(f.Root, wt, [new PushBatch(first, "the first batch"), new PushBatch(second, "the second batch")], null, interactive: true);

        Assert.True(r.AllCommitted);
        var head = f.RemoteHead();
        Assert.Equal("the second batch", f.MessageOf(head));
        Assert.Equal("the first batch", f.MessageOf(f.ParentOf(head)));
        Assert.Equal(before, f.ParentOf(f.ParentOf(head)));
        Assert.Equal("int util = 1;", f.RemoteShow(f.ParentOf(head), "src/lib/util.cpp"));
    }

    [Fact]
    public void A_partial_push_leaves_the_rest_on_the_branch()
    {
        f.Setup();
        var wt = Worktree();
        GitFixture.Put(wt, "src/app.cpp", "int app = 30;\n");
        CommitIn(wt, "first of two");
        GitFixture.Put(wt, "README.md", "hello again\n");
        CommitIn(wt, "second of two");

        var r = Push.Run(f.Root, wt, "only the first one goes", interactive: true, scope: PushScope.First(1));

        Assert.False(r.AllCommitted);
        Assert.Equal("int app = 30;", f.RemoteShow(f.RemoteHead(), "src/app.cpp"));
        Assert.Equal("hello", f.RemoteShow(f.RemoteHead(), "README.md"));
        Assert.Equal(1, f.Root.Git.CountCommits(f.Root.SnapshotRef(f.Co), "refs/heads/feature"));
    }

    [Fact]
    public void Push_refuses_a_file_that_has_a_local_edit_in_the_clone()
    {
        f.Setup();
        var wt = Worktree();
        GitFixture.Put(wt, "src/app.cpp", "int app = 40;\n");
        CommitIn(wt, "touch the app");
        GitFixture.Put(f.Checkout, "src/app.cpp", "int app = -1;\n");

        var preview = Push.Preview(f.Root, wt);
        Assert.False(preview.Checks.Single(c => c.Id == PushChecks.Collisions).Ok);
        var e = Assert.Throws<SgException>(() => Push.Run(f.Root, wt, "should not go", interactive: true));
        Assert.Contains("local edit", e.Message);
        Assert.Equal("int app = -1;\n", GitFixture.Read(f.Checkout, "src/app.cpp"));
    }

    [Fact]
    public void Push_refuses_while_the_clone_has_commits_the_server_has_not()
    {
        f.Setup();
        var wt = Worktree();
        GitFixture.Put(wt, "src/app.cpp", "int app = 50;\n");
        CommitIn(wt, "touch the app");
        GitFixture.Put(f.Checkout, "README.md", "local commit\n");
        GitFixture.Git(f.Checkout, "commit", "-q", "-am", "mine, not pushed");

        var preview = Push.Preview(f.Root, wt);
        Assert.Contains(preview.Checks, c => c.Id == PushChecks.Checkout && !c.Ok);
        var e = Assert.Throws<SgException>(() => Push.Run(f.Root, wt, "should not go", interactive: true));
        Assert.Contains("commit(s)", e.Message);
    }

    [Fact]
    public void A_push_can_stop_in_the_checkout_as_staged_changes()
    {
        f.Setup();
        var wt = Worktree();
        GitFixture.Put(wt, "src/app.cpp", "int app = 60;\n");
        GitFixture.Put(wt, "src/added.cpp", "int added = 1;\n");
        File.Delete(Path.Combine(wt, "docs", "guide.txt"));
        CommitIn(wt, "three kinds of change");
        var before = f.RemoteHead();

        var r = Push.Run(f.Root, wt, null, interactive: true, finish: PushFinish.LeaveInCheckout);

        Assert.True(r.AppliedOnly);
        Assert.Equal(before, f.RemoteHead());
        var changes = Ops.CheckoutChanges(f.Root, f.Co);
        Assert.Contains(changes, c => c.Path == "src/app.cpp" && c.Item == "modified");
        Assert.Contains(changes, c => c.Path == "src/added.cpp" && c.Item == "added");
        Assert.Contains(changes, c => c.Path == "docs/guide.txt" && c.Item == "deleted");
        Assert.Equal("int app = 60;\n", GitFixture.Read(f.Checkout, "src/app.cpp"));
        Assert.Equal(1, f.Root.Git.CountCommits(f.Root.SnapshotRef(f.Co), "refs/heads/feature"));

        // A real push now refuses the same files: they are local edits of the checkout.
        Assert.Throws<SgException>(() => Push.Run(f.Root, wt, "twice", interactive: true));
    }

    // ---- the changes window ----

    [Fact]
    public void Commit_from_the_checkout_sends_the_picked_changes_only()
    {
        f.Setup();
        GitFixture.Put(f.Checkout, "README.md", "edited in the clone\n");
        GitFixture.Put(f.Checkout, "notes/new.txt", "brand new\n");
        File.Delete(Path.Combine(f.Checkout, "docs", "guide.txt"));
        GitFixture.Put(f.Checkout, "src/app.cpp", "int app = 70;\n");

        var changes = Ops.CheckoutChanges(f.Root, f.Co);
        Assert.Contains(changes, c => c.Path == "README.md" && c.Item == "modified");
        Assert.Contains(changes, c => c.Path == "notes/new.txt" && c.Item == "unversioned");
        Assert.Contains(changes, c => c.Path == "docs/guide.txt" && c.Item == "missing");

        var r = Ops.SvnCommit(f.Root, f.Co, ["README.md", "notes", "docs/guide.txt"], "Commit straight from the clone");

        Assert.True(r.AllCommitted);
        var head = f.RemoteHead();
        Assert.Equal(head, r.Groups.Single().Commit);
        Assert.Equal("edited in the clone", f.RemoteShow(head, "README.md"));
        Assert.Equal("brand new", f.RemoteShow(head, "notes/new.txt"));
        Assert.False(f.RemoteHas(head, "docs/guide.txt"));
        Assert.Equal("int app = 1;", f.RemoteShow(head, "src/app.cpp"));
        // The one edit not picked is still an edit, and the snapshot has the commit.
        var left = Ops.CheckoutChanges(f.Root, f.Co);
        Assert.Equal(new[] { "src/app.cpp" }, left.Select(c => c.Path));
        Assert.Equal(head, Snapshot.Commit);
    }

    [Fact]
    public void Commit_goes_on_top_when_someone_else_pushed_other_files()
    {
        f.Setup();
        var theirs = f.OtherCommit("src/lib/util.cpp", "int util = 80;\n", "util eighty");
        GitFixture.Put(f.Checkout, "README.md", "mine\n");

        var r = Ops.SvnCommit(f.Root, f.Co, ["README.md"], "Mine, after theirs");

        Assert.True(r.AllCommitted);
        var head = f.RemoteHead();
        Assert.Equal(theirs, f.ParentOf(head));
        Assert.Equal("int util = 80;", f.RemoteShow(head, "src/lib/util.cpp"));
        Assert.Equal("int util = 80;\n", GitFixture.Read(f.Checkout, "src/lib/util.cpp"));
    }

    [Fact]
    public void Commit_is_out_of_date_when_the_server_changed_the_same_file()
    {
        f.Setup();
        f.OtherCommit("README.md", "theirs\n", "readme theirs");
        GitFixture.Put(f.Checkout, "README.md", "mine\n");

        var r = Ops.SvnCommit(f.Root, f.Co, ["README.md"], "Mine, over theirs");

        Assert.False(r.AllCommitted);
        Assert.Contains("out of date", r.Groups.Single().Error);
        Assert.Equal("theirs", f.RemoteShow(f.RemoteHead(), "README.md"));
    }

    [Fact]
    public void Revert_puts_files_back_and_deletes_the_untracked_ones()
    {
        f.Setup();
        GitFixture.Put(f.Checkout, "README.md", "oops\n");
        GitFixture.Put(f.Checkout, "junk.txt", "junk\n");

        Ops.SvnRevert(f.Root, f.Co, ["README.md", "junk.txt"], deleteUnversioned: true);

        Assert.Equal("hello\n", GitFixture.Read(f.Checkout, "README.md"));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "junk.txt")));
        Assert.Equal("", f.CloneChanges());
    }

    [Fact]
    public void A_folder_ignored_from_the_changes_window_lands_in_its_gitignore()
    {
        f.Setup();
        GitFixture.Put(f.Checkout, "src/build.log", "noise\n");
        f.Root.Vcs(f.Co).Ignore(f.Root, f.Co, "src", ["build.log"]);
        var changes = Ops.CheckoutChanges(f.Root, f.Co);
        Assert.DoesNotContain(changes, c => c.Path == "src/build.log");
        Assert.Contains(changes, c => c.Path == "src/.gitignore" && c.Item == "unversioned");
    }

    // ---- shelves ----

    [Fact]
    public void A_shelf_from_the_clone_holds_the_change_and_comes_back_byte_for_byte()
    {
        f.Setup();
        GitFixture.Put(f.Checkout, "docs/guide.txt", "line1\r\nline2 shelved\r\nline3\r\n");
        var before = GitFixture.Bytes(f.Checkout, "docs/guide.txt");

        var saved = Shelf.Save(f.Root, f.Checkout, null, "guide work").Shelf;

        Assert.True(saved.IsCheckout);
        Assert.Equal("", f.CloneChanges());
        // The shelf is the one line, not every line: the store read the file through the clone's line endings.
        var patch = Shelf.PatchText(f.Root, saved);
        Assert.Single(patch.Split('\n'), l => l.StartsWith('+') && !l.StartsWith("+++"));

        var back = Shelf.Restore(f.Root, saved.Id);
        Assert.Empty(back.Conflicted);
        Assert.Equal(before, GitFixture.Bytes(f.Checkout, "docs/guide.txt"));
        Assert.Equal("M docs/guide.txt", f.CloneChanges());
    }

    [Fact]
    public void A_shelf_that_meets_a_moved_file_merges_into_it()
    {
        f.Setup();
        GitFixture.Put(f.Checkout, "docs/guide.txt", "line1 shelved\r\nline2\r\nline3\r\n");
        var saved = Shelf.Save(f.Root, f.Checkout, null, "early line").Shelf;
        f.OtherCommit("docs/guide.txt", "line1\nline2\nline3 theirs\n", "late line");
        Ops.Sync(f.Root, f.Co);

        var back = Shelf.Restore(f.Root, saved.Id);

        Assert.Empty(back.Conflicted);
        Assert.Equal("line1 shelved\nline2\nline3 theirs\n", GitFixture.Read(f.Checkout, "docs/guide.txt"));
    }

    // ---- blame ----

    [Fact]
    public void Blame_answers_with_the_server_s_commits_and_the_branch_s_own()
    {
        f.Setup();
        var theirs = f.OtherCommit("docs/guide.txt", "line1\nline2 theirs\nline3\n", "guide two");
        Ops.Sync(f.Root, f.Co);
        var first = GitFixture.Git(f.Checkout, "rev-list", "--max-parents=0", "HEAD");

        var co = Blame.OfCheckout(f.Root, f.Co, "docs/guide.txt");
        Assert.Equal(first, co.Lines[0].Commit);
        Assert.Equal(theirs, co.Lines[1].Commit);
        Assert.All(co.Lines, l => Assert.False(l.Local));

        var wt = Worktree();
        GitFixture.Put(wt, "docs/guide.txt", "line1\nline2 theirs\nline3 mine\n");
        CommitIn(wt, "guide three");
        var b = Blame.OfWorktree(f.Root, wt, "docs/guide.txt");
        Assert.Equal(first, b.Lines[0].Commit);
        Assert.Equal(theirs, b.Lines[1].Commit);
        Assert.True(b.Lines[2].Local);
        Assert.Equal(Rev.Short(theirs)[..8], b.Lines[1].Mark);

        var (log, diff) = f.Root.Vcs(f.Co).BlameDetails(f.Root, f.Co, "docs/guide.txt", co.Lines[1].Server);
        Assert.Equal("guide two", log!.Message);
        Assert.Contains("+line2 theirs", diff);
    }

    // ---- history ----

    [Fact]
    public void The_log_reads_the_server_branch_with_the_paths_each_commit_touched()
    {
        f.Setup();
        f.OtherCommit("src/app.cpp", "int app = 90;\n", "app ninety");
        var vcs = f.Root.Vcs(f.Co);
        var source = vcs.HistorySources(f.Root, f.Co).Single();

        var log = vcs.Log(f.Root, f.Co, source, 10);

        Assert.Equal(2, log.Count);
        Assert.Equal("app ninety", log[0].Message);
        Assert.Equal(2, log[0].Revision);
        Assert.Contains(log[0].Paths, p => p.Path == "/src/app.cpp" && p.Action == "M");
        Assert.Contains("+int app = 90;", vcs.RevisionDiff(f.Root, f.Co, source, log[0], null));
        var path = log[0].Paths.Single();
        Assert.Equal("int app = 1;\n", vcs.FileAt(f.Root, f.Co, source, log[0], path, before: true));
        Assert.Equal("int app = 90;\n", vcs.FileAt(f.Root, f.Co, source, log[0], path, before: false));
    }

    [Fact]
    public void The_monitor_reads_a_git_branch_through_a_cache_of_its_own()
    {
        var cache = Path.Combine(f.Base, "monitor.git");
        var url = f.RemoteDir + "#main";
        var h = ServerHistory.For(url, new Svn("svn", f.Log), "git", cache, f.Log);
        Assert.Equal(CheckoutKind.Git, h.Kind);

        var first = h.Read(url, 10);
        Assert.Equal(1, first.Head);
        Assert.Equal(f.RemoteHead(), first.Commit);

        f.OtherCommit("src/app.cpp", "int app = 5;\n", "app five");
        var second = h.Read(url, 10);
        Assert.Equal(2, second.Head);
        var newest = second.Recent[0];
        Assert.Equal("app five", newest.Message);
        Assert.Equal(2, newest.Revision);
        Assert.Contains("+int app = 5;", h.Diff(url, second.ReposRoot, newest, null));
        var path = newest.Paths.Single();
        Assert.Equal("int app = 1;\n", h.FileAt(url, second.ReposRoot, newest, path, before: true));

        // No branch named: the repository's default one.
        Assert.Equal(2, h.Read(f.RemoteDir, 10).Head);
    }

    // ---- server branches ----

    [Fact]
    public void A_server_branch_is_pushed_and_checked_out_next_to_the_first()
    {
        f.Setup();
        var plan = Server.PlanBranch(f.Root, f.Co, "release");
        Assert.EndsWith("#release", plan.NewRootUrl);
        Assert.Contains("release", plan.Describe());

        Server.ExecuteBranch(f.Root, plan);
        Assert.Equal(f.RemoteHead("main"), f.RemoteHead("release"));
        Assert.Equal("committed", plan.Repos.Single().State);
        Assert.Throws<SgException>(() => Server.PlanBranch(f.Root, f.Co, "release"));

        var r = Server.Checkout(f.Root, f.Co, "release");
        Assert.Equal(CheckoutKind.Git, r.Checkout.Kind);
        Assert.Equal("release", r.Checkout.Branch);
        Assert.Equal(f.RemoteHead("release"), r.Snapshot.Commit);
        Assert.True(File.Exists(Path.Combine(r.Checkout.Path, "src", "app.cpp")));
        Assert.Equal(f.Co.Skip, r.Checkout.Skip);
    }

    // ---- merging ----

    [Fact]
    public void A_commit_is_cherry_picked_from_another_branch_as_a_local_change()
    {
        f.Setup();
        var fix = f.OtherBranch("hotfix", "src/lib/util.cpp", "int util = 99;\n", "fix the util");
        var target = Merge.Targets(f.Root, f.Co).Single();
        var source = Merge.Sources(f.Root, f.Co, target).Single(s => s.Name == "hotfix");
        var pairs = Merge.Pairs(f.Root, f.Co, target, source.Url);
        var offered = Merge.Offered(f.Root, f.Co, pairs);
        var pick = offered.Single(o => o.Entry.Commit == fix);
        Assert.False(pick.Merged);
        Assert.Contains(offered, o => o.Merged);   // the initial commit is on both

        var dry = Merge.RunAll(f.Root, f.Co, pairs, [pick], dryRun: true);
        Assert.Contains(dry.Changed, c => c.Path == "src/lib/util.cpp");
        Assert.Empty(dry.Conflicts);
        Assert.Equal("", f.CloneChanges());

        var real = Merge.RunAll(f.Root, f.Co, pairs, [pick], dryRun: false);
        Assert.True(real.Clean);
        Assert.Equal(new[] { fix }, real.Commits);
        Assert.Equal("int util = 99;\n", GitFixture.Read(f.Checkout, "src/lib/util.cpp"));
        Assert.Contains(Ops.CheckoutChanges(f.Root, f.Co), c => c.Path == "src/lib/util.cpp" && c.Item == "modified");
    }

    [Fact]
    public void Everything_a_branch_has_comes_in_as_local_changes_and_a_revert_takes_one_back_out()
    {
        f.Setup();
        f.OtherBranch("topic", "src/topic.cpp", "int topic = 1;\n", "topic file");
        var target = Merge.Targets(f.Root, f.Co).Single();
        var pairs = Merge.Pairs(f.Root, f.Co, target, GitLocation.WithBranch(f.Co.Url, "topic"));

        var all = Merge.RunAll(f.Root, f.Co, pairs, null, dryRun: false);
        Assert.True(all.Clean);
        Assert.Contains(Ops.CheckoutChanges(f.Root, f.Co), c => c.Path == "src/topic.cpp" && c.Item == "added");

        Ops.SvnCommit(f.Root, f.Co, ["src/topic.cpp"], "Bring the topic in");
        var ours = Merge.Offered(f.Root, f.Co, Merge.Pairs(f.Root, f.Co, target, f.Co.Url.Replace("#main", "#topic")));
        Assert.Contains(ours, o => o.Merged && o.Entry.Message == "topic file");
    }

    // ---- moving checkout edits to a worktree ----

    [Fact]
    public void Clone_edits_move_to_a_worktree_through_the_clone_s_line_endings_and_merge_into_its_own()
    {
        f.Setup();
        var text = "one\ntwo\nthree\nfour\nfive\nsix\nseven\n";
        f.OtherCommit("docs/long.txt", text, "a longer file to merge in");
        Ops.Sync(f.Root, f.Co);
        var wt = Worktree("existing");
        GitFixture.Put(wt, "docs/long.txt", text.Replace("seven", "destination"));
        // The clone writes CRLF; what goes across is what its git would commit.
        GitFixture.Put(f.Checkout, "docs/long.txt", text.Replace("one", "source").Replace("\n", "\r\n"));
        GitFixture.Put(f.Checkout, "notes/draft.txt", "draft\r\n");

        var plan = CheckoutTransfer.Preview(f.Root, f.Co, "existing", move: true);

        Assert.True(plan.CanApply);
        Assert.Contains(plan.Files, x => x.Path == "docs/long.txt" && x.Action == "Merge edits");
        var result = CheckoutTransfer.Apply(f.Root, plan);
        Assert.True(result.Moved);
        Assert.Equal(text.Replace("one", "source").Replace("seven", "destination"), File.ReadAllText(Path.Combine(wt, "docs", "long.txt")));
        Assert.Equal("draft\n", File.ReadAllText(Path.Combine(wt, "notes", "draft.txt")));
        Assert.Empty(Ops.CheckoutChanges(f.Root, f.Co));
        Assert.Equal("", f.CloneChanges());

        // The recovery shelf puts the clone's edit back the way the clone wrote it.
        Shelf.Restore(f.Root, result.SourceShelf, keep: true);
        Assert.Equal(text.Replace("one", "source").Replace("\n", "\r\n"), File.ReadAllText(Path.Combine(f.Checkout, "docs", "long.txt")));
    }

    [Fact]
    public void A_merge_refuses_a_clone_with_local_changes()
    {
        f.Setup();
        f.OtherBranch("hotfix2", "src/lib/util.cpp", "int util = 7;\n", "util seven");
        GitFixture.Put(f.Checkout, "README.md", "dirty\n");
        var target = Merge.Targets(f.Root, f.Co).Single();
        var pairs = Merge.Pairs(f.Root, f.Co, target, GitLocation.WithBranch(f.Co.Url, "hotfix2"));
        Assert.NotEmpty(Merge.Problems(f.Root, f.Co, target, pairs[0].SourceUrl));
        var r = Merge.RunAll(f.Root, f.Co, pairs, null, dryRun: false);
        Assert.NotNull(r.Failure);
    }

    // ---- export, import, backup ----

    [Fact]
    public void An_export_names_the_clone_s_commit_and_finds_the_checkout_again()
    {
        f.Setup();
        var wt = Worktree();
        GitFixture.Put(wt, "src/app.cpp", "int app = 100;\n");
        CommitIn(wt, "for the export");
        var file = Path.Combine(f.Base, "feature.sgx");

        Export.Write(f.Root, wt, file);
        var meta = Export.Read(file);

        Assert.Equal(f.Co.Url, meta.Root!.Url);
        Assert.Equal(f.RemoteHead(), meta.Root.Commit);
        Assert.Equal(GitFixture.Git(f.Checkout, "rev-list", "--max-parents=0", "HEAD"), meta.Root.Uuid);
        Assert.Equal("main", meta.Root.RepoPath);
        Assert.Same(f.Co, Export.MatchCheckout(f.Root, meta));
        Assert.Empty(Export.DriftOf(f.Root, meta, f.Co));

        f.OtherCommit("README.md", "moved on\n", "server moved");
        Ops.Sync(f.Root, f.Co);
        var drift = Export.DriftOf(f.Root, meta, f.Co).Single();
        Assert.Contains(Rev.Short(f.RemoteHead()), drift.ToString());

        var imported = Export.Import(f.Root, file, asBranch: "imported");
        Assert.Null(imported.Stopped);
        Assert.Equal("int app = 100;\n", GitFixture.Read(imported.Path, "src/app.cpp"));
    }

    [Fact]
    public void A_backup_marker_carries_the_git_trailers()
    {
        f.Setup();
        var marker = Thin.Marker(f.Root.Git, SnapshotSha);
        var body = f.Root.Git.Body(marker);
        Assert.Contains(SnapshotMeta.GitCommit + f.RemoteHead(), body);
        Assert.Contains(SnapshotMeta.GitUrl + f.Co.Url, body);
        Assert.Equal(f.RemoteHead(), SnapshotMeta.Parse(body).Commit);
    }
}
