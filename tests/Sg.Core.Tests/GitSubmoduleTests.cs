using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Git submodules handled the way SVN externals are: in the snapshot at the commit their parent pins,
/// their changes listed and committed in their own repository, a push that commits in the submodule
/// first and pins the new commit in its parent after, and a switch onto a branch that stays local.
/// </summary>
public sealed class GitSubmoduleTests
{
    const string Sub = GitFixture.Sub;

    static string SnapshotSha(GitFixture f) => f.Root.Git.RefSha(f.Root.SnapshotRef(f.Co))!;
    static SnapshotMeta Snapshot(GitFixture f) => SnapshotMeta.Parse(f.Root.Git.Body(SnapshotSha(f)));
    static string InSnapshot(GitFixture f, string path) => f.Root.Git.Out(null, "show", SnapshotSha(f) + ":" + path);
    static string SubDir(GitFixture f, string rel = Sub) => Path.Combine(f.Checkout, rel.Replace('/', Path.DirectorySeparatorChar));

    static void CommitIn(GitFixture f, string wt, string message)
    {
        f.Root.Git.Ok(wt, "add", "-A");
        f.Root.Git.Ok(wt, "commit", "-q", "-m", message);
    }

    [Fact]
    public void The_snapshot_holds_the_submodule_s_files_at_the_commit_its_parent_pins()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        var pin = GitFixture.PinIn(f.RemoteDir, "main", Sub);

        var meta = Snapshot(f);
        Assert.Equal(pin, meta.ExternalCommits[Sub]);
        Assert.Equal(1, meta.Externals[Sub]);
        Assert.True(GitSubmodules.SameRepo(f.LibRemote.Replace('\\', '/'), meta.ExternalUrls[Sub].Replace('\\', '/')), meta.ExternalUrls[Sub]);
        Assert.Equal("int lib = 1;", InSnapshot(f, Sub + "/lib.h"));
        Assert.Equal("int core = 1;", InSnapshot(f, Sub + "/src/core.c"));
        // A folder of files in the snapshot, not a pin nothing in a worktree could open.
        Assert.StartsWith("040000 tree", f.Root.Git.Out(null, "ls-tree", SnapshotSha(f), "--", Sub));

        var wt = Ops.Branch(f.Root, "feature", f.Co).Path;
        Assert.Equal("int lib = 1;\n", GitFixture.Read(wt, Sub + "/lib.h"));

        var ext = Ops.ExternalsOf(f.Root, f.Co).Single();
        Assert.Equal(Sub, ext.Rel);
        Assert.False(ext.Switched);
        Assert.Equal(pin, ext.Commit);
        Assert.Equal(new[] { Sub }, f.Root.Vcs(f.Co).Scan(f.Root, f.Co).Externals);
    }

    [Fact]
    public void Sync_moves_a_pinned_submodule_to_its_new_pin_and_says_what_came_in()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        GitFixture.ServerCommit(f.LibOther, "lib.h", "int lib = 2;\n", "lib two");
        Ops.Sync(f.Root, f.Co);
        // A new commit on lib's branch is not the app's until the app pins it.
        Assert.Equal("int lib = 1;", InSnapshot(f, Sub + "/lib.h"));

        f.BumpPin("take lib two");
        var r = Ops.Sync(f.Root, f.Co);

        Assert.True(r.Changed);
        Assert.Equal("int lib = 2;", InSnapshot(f, Sub + "/lib.h"));
        Assert.Equal("int lib = 2;\n", GitFixture.Read(f.Checkout, Sub + "/lib.h"));
        Assert.Equal(f.LibHead(), GitFixture.Git(SubDir(f), "rev-parse", "HEAD"));
        Assert.Contains("lib two", f.Root.Git.Body(SnapshotSha(f)));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void A_submodule_not_cloned_yet_is_cloned_by_sync()
    {
        using var f = new GitFixture(submodule: true);
        GitFixture.Git(f.Checkout, "submodule", "deinit", "-q", "-f", Sub);
        f.Setup();
        Assert.False(Snapshot(f).ExternalCommits.ContainsKey(Sub));

        Ops.Sync(f.Root, f.Co);

        Assert.True(File.Exists(Path.Combine(SubDir(f), "lib.h")));
        Assert.Equal("int lib = 1;", InSnapshot(f, Sub + "/lib.h"));
    }

    [Fact]
    public void A_skipped_submodule_stays_out_of_everything()
    {
        using var f = new GitFixture(submodule: true);
        f.Root = Ops.Init(f.RootDir, f.Log, fsmonitor: false);
        f.Co = Ops.CheckoutAdd(f.Root, f.Checkout, skip: ["assets/big", "libs"], junctions: ["assets/big"]).Checkout;
        GitFixture.Put(f.Checkout, Sub + "/lib.h", "edited\n");

        Assert.Empty(Snapshot(f).ExternalCommits);
        Assert.Equal("", f.Root.Git.Out(null, "ls-tree", SnapshotSha(f), "--", "libs"));
        Assert.Empty(Ops.CheckoutChanges(f.Root, f.Co));
        Assert.Empty(Ops.ExternalsOf(f.Root, f.Co));
    }

    [Fact]
    public void Changes_in_a_submodule_are_listed_and_committed_there_then_pinned_in_the_app()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        GitFixture.Put(f.Checkout, Sub + "/lib.h", "int lib = 30;\n");
        GitFixture.Put(f.Checkout, Sub + "/new.h", "int fresh = 1;\n");
        GitFixture.Put(f.Checkout, "README.md", "app edit\n");

        var changes = Ops.CheckoutChanges(f.Root, f.Co);
        Assert.Contains(changes, c => c.Path == Sub + "/lib.h" && c.Item == "modified" && c.Wc == Sub);
        Assert.Contains(changes, c => c.Path == Sub + "/new.h" && c.Item == "unversioned" && c.Wc == Sub);
        Assert.Contains(changes, c => c.Path == "README.md" && c.Wc == "");
        // The submodule is not a change of the app's: its files are.
        Assert.DoesNotContain(changes, c => c.Path == Sub);
        Assert.Contains("+int lib = 30;", f.Root.Vcs(f.Co).DiffLocal(f.Root, f.Co, Sub + "/lib.h"));
        Assert.Contains("b/" + Sub + "/lib.h", f.Root.Vcs(f.Co).DiffLocal(f.Root, f.Co, ""));
        Assert.Equal("int lib = 1;\n", f.Root.Vcs(f.Co).BaseText(f.Root, f.Co, Sub + "/lib.h").Replace("\r\n", "\n"));

        var r = Ops.SvnCommit(f.Root, f.Co, [Sub], "Core changes from the checkout");

        Assert.True(r.AllCommitted);
        Assert.Equal(new[] { Sub, "" }, r.Groups.Select(g => g.Wc));
        var lib = f.LibHead();
        Assert.Equal(lib, r.Groups[0].Commit);
        Assert.Equal("int lib = 30;", f.LibShow(lib, "lib.h"));
        Assert.Equal("int fresh = 1;", f.LibShow(lib, "new.h"));
        Assert.Equal("Tester", GitFixture.Git(f.LibRemote, "log", "-1", "--format=%an", lib));
        // The app's commit is the new pin and nothing else: its own edit was not picked.
        var app = f.RemoteHead();
        Assert.Equal(app, r.Groups[1].Commit);
        Assert.Equal(lib, GitFixture.PinIn(f.RemoteDir, app, Sub));
        Assert.Equal("hello", f.RemoteShow(app, "README.md"));
        Assert.Equal("Core changes from the checkout", f.MessageOf(app));

        // Nothing is left changed but the edit that was not picked, and the snapshot has both commits.
        Assert.Equal(new[] { "README.md" }, Ops.CheckoutChanges(f.Root, f.Co).Select(c => c.Path));
        Assert.Equal("M README.md", f.CloneChanges());
        Assert.Equal(lib, GitFixture.Git(SubDir(f), "rev-parse", "HEAD"));
        Assert.Equal(app, Snapshot(f).Commit);
        Assert.Equal(lib, Snapshot(f).ExternalCommits[Sub]);
    }

    [Fact]
    public void A_push_commits_in_the_submodule_first_and_the_app_pins_it_with_its_own_change()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        var wt = Ops.Branch(f.Root, "feature", f.Co).Path;
        GitFixture.Put(wt, Sub + "/src/core.c", "int core = 7;\n");
        GitFixture.Put(wt, "src/app.cpp", "int app = 7;\n");
        CommitIn(f, wt, "core and app together");

        var preview = Push.Preview(f.Root, wt);
        Assert.Equal(new[] { "", Sub }, preview.Groups.Select(g => g.Wc));
        Assert.True(preview.Ready);

        var r = Push.Run(f.Root, wt, "Core and app together", interactive: true);

        Assert.True(r.AllCommitted);
        Assert.Equal(new[] { Sub, "" }, r.Groups.Select(g => g.Wc));
        var lib = f.LibHead();
        var app = f.RemoteHead();
        Assert.Equal("int core = 7;", f.LibShow(lib, "src/core.c"));
        Assert.Equal("int app = 7;", f.RemoteShow(app, "src/app.cpp"));
        Assert.Equal(lib, GitFixture.PinIn(f.RemoteDir, app, Sub));
        Assert.Equal("", f.CloneChanges());
        Assert.Equal(0, f.Root.Git.CountCommits(f.Root.SnapshotRef(f.Co), "refs/heads/feature"));
        Assert.Equal("int core = 7;", InSnapshot(f, Sub + "/src/core.c"));
    }

    [Fact]
    public void A_push_of_submodule_files_only_gives_the_app_a_commit_of_the_pin_alone()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        // lib moved on before this push, on a file the branch does not touch: the commit goes on top.
        GitFixture.ServerCommit(f.LibOther, "lib.h", "int lib = 5;\n", "lib five");
        var wt = Ops.Branch(f.Root, "feature", f.Co).Path;
        GitFixture.Put(wt, Sub + "/src/core.c", "int core = 8;\n");
        CommitIn(f, wt, "core only");
        var before = f.RemoteHead();

        var r = Push.Run(f.Root, wt, "Core file only", interactive: true);

        Assert.True(r.AllCommitted);
        var lib = f.LibHead();
        Assert.Equal("int lib = 5;", f.LibShow(lib, "lib.h"));
        Assert.Equal("int core = 8;", f.LibShow(lib, "src/core.c"));
        var app = f.RemoteHead();
        Assert.Equal(before, f.ParentOf(app));
        Assert.Equal(lib, GitFixture.PinIn(f.RemoteDir, app, Sub));
        Assert.Equal("", GitFixture.Git(f.RemoteDir, "diff", "--name-only", before, app).Replace(Sub, "").Trim());
        // lib five came along into the checkout with the new pin.
        Assert.Equal("int lib = 5;\n", GitFixture.Read(f.Checkout, Sub + "/lib.h"));
        Assert.Equal("", f.CloneChanges());
    }

    [Fact]
    public void A_pinned_submodule_is_out_of_date_when_its_branch_changed_the_same_file_after_the_pin()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        GitFixture.ServerCommit(f.LibOther, "lib.h", "int lib = 6;\n", "lib six");
        GitFixture.Put(f.Checkout, Sub + "/lib.h", "int lib = 60;\n");

        var r = Ops.SvnCommit(f.Root, f.Co, [Sub + "/lib.h"], "Would overwrite lib six");

        Assert.False(r.AllCommitted);
        Assert.Contains("out of date", r.Groups.Single(g => g.Wc == Sub).Error);
        Assert.Equal("skipped", r.Groups.Single(g => g.Wc == "").State);
        Assert.Equal("int lib = 6;", f.LibShow(f.LibHead(), "lib.h"));
    }

    [Fact]
    public void A_switched_submodule_follows_its_branch_and_commits_there_without_moving_the_pin()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        var pin = GitFixture.PinIn(f.RemoteDir, "main", Sub);
        GitFixture.ServerCommit(f.LibOther, "lib.h", "int lib = 9;\n", "lib nine on dev", branch: "dev");
        var ext = Ops.ExternalsOf(f.Root, f.Co).Single();
        Assert.Contains("dev", Ops.BranchNames(f.Root, f.Co, ext.Url));

        Ops.SwitchExternal(f.Root, f.Co, Sub, Ops.UrlForBranch(f.Root, f.Co, ext.Url, "dev"));
        var s = Ops.Sync(f.Root, f.Co);

        Assert.Contains(Sub, s.KeptSwitched);
        ext = Ops.ExternalsOf(f.Root, f.Co).Single();
        Assert.True(ext.Switched);
        Assert.EndsWith("#dev", ext.Url);
        Assert.Equal("int lib = 9;", InSnapshot(f, Sub + "/lib.h"));
        Assert.EndsWith("#dev", Snapshot(f).ExternalUrls[Sub]);
        // The app's status does not count a switched submodule as a change of its own.
        Assert.Empty(Ops.CheckoutChanges(f.Root, f.Co));

        GitFixture.Put(f.Checkout, Sub + "/lib.h", "int lib = 10;\n");
        var before = f.RemoteHead();
        var r = Ops.SvnCommit(f.Root, f.Co, [Sub], "Straight to dev");

        Assert.True(r.AllCommitted);
        Assert.Equal(Sub, r.Groups.Single().Wc);
        Assert.Equal("int lib = 10;", f.LibShow(f.LibHead("dev"), "lib.h"));
        Assert.Equal(before, f.RemoteHead());
        Assert.Equal(pin, GitFixture.PinIn(f.RemoteDir, "main", Sub));

        // Back to the pin, as the app declares it.
        Ops.SwitchExternal(f.Root, f.Co, Sub, ext.Declared);
        Ops.Sync(f.Root, f.Co);
        Assert.False(Ops.ExternalsOf(f.Root, f.Co).Single().Switched);
        Assert.Equal("int lib = 1;", InSnapshot(f, Sub + "/lib.h"));
    }

    [Fact]
    public void A_nested_submodule_is_grafted_and_a_push_into_it_pins_all_the_way_up()
    {
        using var f = new GitFixture(nested: true);
        f.Setup();
        Assert.Equal("int z = 1;", InSnapshot(f, GitFixture.Nested + "/z.h"));
        Assert.Equal(new[] { Sub, GitFixture.Nested }, Snapshot(f).ExternalCommits.Keys.OrderBy(k => k.Length));

        var wt = Ops.Branch(f.Root, "feature", f.Co).Path;
        GitFixture.Put(wt, GitFixture.Nested + "/z.h", "int z = 2;\n");
        CommitIn(f, wt, "z two");

        var r = Push.Run(f.Root, wt, "Z two, from the app", interactive: true);

        Assert.True(r.AllCommitted);
        Assert.Equal(new[] { GitFixture.Nested, Sub, "" }, r.Groups.Select(g => g.Wc));
        var z = GitFixture.Git(f.ZRemote, "rev-parse", "refs/heads/main");
        var lib = f.LibHead();
        Assert.Equal("int z = 2;", GitFixture.Git(f.ZRemote, "show", z + ":z.h"));
        Assert.Equal(z, GitFixture.PinIn(f.LibRemote, lib, "vendor/z"));
        Assert.Equal(lib, GitFixture.PinIn(f.RemoteDir, "main", Sub));
        Assert.Equal("", f.CloneChanges());
        Assert.Equal("", GitFixture.Git(SubDir(f), "status", "--porcelain"));
        Assert.Equal("int z = 2;", InSnapshot(f, GitFixture.Nested + "/z.h"));
    }

    [Fact]
    public void Blame_and_the_log_read_the_submodule_s_own_history()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        var two = GitFixture.ServerCommit(f.LibOther, "lib.h", "int lib = 2;\n", "lib two");
        f.BumpPin("take lib two");
        Ops.Sync(f.Root, f.Co);

        var b = Blame.OfCheckout(f.Root, f.Co, Sub + "/lib.h");
        Assert.Equal(two, b.Lines[0].Commit);

        var vcs = f.Root.Vcs(f.Co);
        var source = vcs.HistorySources(f.Root, f.Co).Single(s => s.Wc == Sub);
        Assert.Equal(2, source.SnapshotRevision);
        var log = vcs.Log(f.Root, f.Co, source, 10);
        Assert.Equal("lib two", log[0].Message);
        Assert.Contains(log[0].Paths, p => p.Path == "/lib.h");
        Assert.Contains("+int lib = 2;", vcs.RevisionDiff(f.Root, f.Co, source, log[0], null));
    }

    [Fact]
    public void A_shelf_takes_a_submodule_edit_out_of_the_checkout_and_puts_it_back()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        GitFixture.Put(f.Checkout, Sub + "/lib.h", "int lib = 40;\r\n");
        var before = GitFixture.Bytes(f.Checkout, Sub + "/lib.h");

        var saved = Shelf.Save(f.Root, f.Checkout, null, "lib work").Shelf;

        Assert.Equal("", GitFixture.Git(SubDir(f), "status", "--porcelain"));
        Assert.Empty(Ops.CheckoutChanges(f.Root, f.Co));
        Assert.Contains("+int lib = 40;", Shelf.PatchText(f.Root, saved));

        var back = Shelf.Restore(f.Root, saved.Id);
        Assert.Empty(back.Conflicted);
        Assert.Equal(before, GitFixture.Bytes(f.Checkout, Sub + "/lib.h"));
        Assert.Contains(Ops.CheckoutChanges(f.Root, f.Co), c => c.Path == Sub + "/lib.h" && c.Wc == Sub);
    }

    [Fact]
    public void A_merge_takes_a_commit_from_another_branch_of_the_submodule_s_repository()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        var fix = GitFixture.ServerCommit(f.LibOther, "src/core.c", "int core = 99;\n", "fix the core", branch: "hotfix");
        var target = Merge.Targets(f.Root, f.Co).Single(t => t.Wc == Sub);
        var source = Merge.Sources(f.Root, f.Co, target).Single(s => s.Name == "hotfix");
        var pairs = Merge.Pairs(f.Root, f.Co, target, source.Url);
        var pick = Merge.Offered(f.Root, f.Co, pairs).Single(o => o.Entry.Commit == fix);

        var real = Merge.RunAll(f.Root, f.Co, pairs, [pick], dryRun: false);

        Assert.True(real.Clean);
        Assert.Contains(real.Changed, c => c.Path == Sub + "/src/core.c");
        Assert.Equal("int core = 99;\n", GitFixture.Read(f.Checkout, Sub + "/src/core.c"));
        Assert.Contains(Ops.CheckoutChanges(f.Root, f.Co), c => c.Path == Sub + "/src/core.c" && c.Wc == Sub);
    }

    [Fact]
    public void An_export_names_each_submodule_s_commit_and_sees_it_move()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        var wt = Ops.Branch(f.Root, "feature", f.Co).Path;
        GitFixture.Put(wt, Sub + "/lib.h", "int lib = 70;\n");
        CommitIn(f, wt, "for the export");
        var file = Path.Combine(f.Base, "feature.sgx");

        Export.Write(f.Root, wt, file);
        var meta = Export.Read(file);
        var sub = meta.Bases.Single(b => b.Rel == Sub);
        Assert.Equal(GitFixture.PinIn(f.RemoteDir, "main", Sub), sub.Commit);
        Assert.Empty(Export.DriftOf(f.Root, meta, f.Co));

        GitFixture.ServerCommit(f.LibOther, "src/core.c", "int core = 2;\n", "core two");
        f.BumpPin("take core two");
        Ops.Sync(f.Root, f.Co);
        Assert.Contains(Export.DriftOf(f.Root, meta, f.Co), d => d.ToString().Contains(Rev.Short(f.LibHead())));

        var marker = Thin.Marker(f.Root.Git, SnapshotSha(f));
        Assert.Equal(f.LibHead(), SnapshotMeta.Parse(f.Root.Git.Body(marker)).ExternalCommits[Sub]);
    }

    [Fact]
    public void A_server_branch_branches_the_submodule_too_and_names_it_in_gitmodules()
    {
        using var f = new GitFixture(submodule: true);
        f.Setup();
        var parts = Server.Parts(f.Root, f.Co);
        Assert.Equal(new[] { "", Sub }, parts.Select(p => p.Wc));

        var plan = Server.PlanBranch(f.Root, f.Co, "release");
        Assert.Equal(new[] { Sub, "" }, plan.Repos.Select(r => r.Wc));
        Server.ExecuteBranch(f.Root, plan);

        var pin = GitFixture.PinIn(f.RemoteDir, "main", Sub);
        Assert.Equal(pin, f.LibHead("release"));
        var release = f.RemoteHead("release");
        Assert.Equal(f.RemoteHead("main"), f.ParentOf(release));
        Assert.Contains("branch = release", f.RemoteShow(release, ".gitmodules"));
        Assert.Equal(pin, GitFixture.PinIn(f.RemoteDir, release, Sub));

        // Kept, the submodule stays where it is and the app's branch is its tip as it stands.
        var kept = Server.PlanBranch(f.Root, f.Co, "hold", parts: [new BranchPart { Wc = Sub, Keep = true }]);
        Assert.Equal(new[] { "" }, kept.Repos.Select(r => r.Wc));
        Assert.Contains(kept.Kept, k => k.Wc == Sub);
    }
}
