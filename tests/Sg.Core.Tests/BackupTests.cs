namespace Sg.Core.Tests;

/// <summary>
/// A backup is a git repository somewhere else that holds every branch, the uncommitted changes and
/// the shelves, and never the SVN tree. What goes is a thin history: the snapshot as a marker with the
/// empty tree, each commit with only the files the branch wrote, and the version a file started from
/// under its first edit. Nothing the branch never touched leaves the machine.
/// </summary>
public sealed class BackupTests : IDisposable
{
    readonly Fixture f = new();

    public void Dispose() => f.Dispose();

    string _remote = "";

    /// <summary>A bare repository on disk, the way a share or a hosted remote would answer.</summary>
    string Remote()
    {
        if (_remote.Length > 0) return _remote;
        _remote = Path.Combine(f.Base, "backup.git");
        Proc.Run("git", ["init", "-q", "--bare", _remote], null, f.Log).EnsureOk();
        return _remote;
    }

    string RemoteGit(params string[] args) =>
        Proc.Run("git", new[] { "-C", _remote }.Concat(args).ToList(), null, f.Log).EnsureOk().StdOut;

    /// <summary>The other PC: its own root, its own checkout of the same repository, the same backup URL.</summary>
    (SgRoot Root, CheckoutConfig Co) Far(string name = "far")
    {
        var dir = Path.Combine(f.Base, name);
        Directory.CreateDirectory(dir);
        var wc = Path.Combine(dir, "mono");
        f.Svn.Ok(null, "checkout", "--non-interactive", f.MonoUrl + "/trunk", wc);
        var root = Ops.Init(dir, f.Log, fsmonitor: false);
        var co = Ops.CheckoutAdd(root, wc, skip: ["fort/builds"], junctions: ["fort/builds"], optional: ["fort/tools"]).Checkout;
        Backup.Set(root, Remote());
        return (root, co);
    }

    /// <summary>Two commits on a branch: one that edits and adds, one that renames and deletes.</summary>
    string MakeBranch(string name)
    {
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, name, f.Co).Path;

        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 2;\n");
        Fixture.Put(wt, "fort/dev/new/file.txt", "brand new\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "[gui] first: edit one, add one", "--author=Ada Lovelace <ada@example.com>");

        git.Ok(wt, "mv", "fort/dev/game.cpp", "fort/dev/game_renamed.cpp");
        File.Delete(Path.Combine(wt, "fort", "tools", "tool.py"));
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "second: rename one, delete one");
        return wt;
    }

    static string Read(string root, string rel) =>
        File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

    BackupItem Item(BackupResult r, string kind, string name) =>
        r.Items.Single(i => i.Kind == kind && i.Name == name);

    [Fact]
    public void Backup_SendsOnlyWhatTheBranchWrote_AndSendsNothingTwice()
    {
        f.Setup();
        var wt = MakeBranch("feature-x");
        Backup.Set(f.Root, Remote());

        var r = Backup.Run(f.Root);
        Assert.True(r.Ok, string.Join("\n", r.Items.Select(i => i.State + " " + i.Why)));
        var branch = Item(r, "branch", "feature-x");
        Assert.Equal("pushed", branch.State);
        Assert.Equal(2, branch.Commits);
        Assert.DoesNotContain(r.Items, i => i.Kind == "wip");

        // The remote holds the files the branch wrote, twice at most, and nothing else of the tree.
        var objects = RemoteGit("rev-list", "--objects", "--all").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', 2)).Where(p => p.Length == 2).Select(p => p[1]).ToList();
        Assert.Contains("schmetterling/engine.cpp", objects);
        Assert.Contains("fort/dev/new/file.txt", objects);
        Assert.Contains("fort/tools/tool.py", objects);
        // The renamed file's blob is listed once, under one of its names; the base commit under the
        // second change holds it under the old one, beside the file that change deletes.
        var underSecond = RemoteGit("ls-tree", "-r", "--name-only", "refs/heads/feature-x~1").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("fort/dev/game.cpp", underSecond);
        Assert.Contains("fort/tools/tool.py", underSecond);
        Assert.DoesNotContain("CMakeLists.txt", objects);
        Assert.DoesNotContain("schmetterling/sub/data.txt", objects);
        Assert.DoesNotContain(objects, p => p.EndsWith("readme.txt", StringComparison.Ordinal));

        // The marker is the snapshot with nothing in it, and its message says which revision that was.
        var marker = RemoteGit("rev-list", "--max-parents=0", "refs/heads/feature-x").Trim();
        Assert.Equal("", RemoteGit("ls-tree", marker).Trim());
        var body = RemoteGit("log", "-1", "--format=%B", marker);
        Assert.Contains("svn-url: " + f.MonoUrl + "/trunk", body);
        Assert.Contains("sg-thin: marker", body);
        Assert.DoesNotContain("initial content", body);

        // The same branch again is the same objects again, so there is nothing to send.
        var again = Backup.Run(f.Root);
        Assert.Equal("up to date", Item(again, "branch", "feature-x").State);
        var s1 = Ops.Status(f.Root, checkSvn: false).Worktrees.Single();
        Assert.Equal(0, s1.NotBackedUp);
        Assert.NotNull(s1.BackedUp);

        // One more commit goes on top of what is there: the thin tip that was pushed is under the new one.
        var was = f.Root.Git.RefSha(Backup.PushedRef("branch", "feature-x"))!;
        Fixture.Put(wt, "fort/dev/new/file.txt", "brand new\nand a line\n");
        f.Root.Git.Ok(wt, "commit", "-q", "-am", "third: one more line");
        Assert.Equal(1, Ops.Status(f.Root, checkSvn: false).Worktrees.Single().NotBackedUp);
        var third = Backup.Run(f.Root);
        var tip = Item(third, "branch", "feature-x");
        Assert.Equal("pushed", tip.State);
        Assert.Equal(3, tip.Commits);
        Assert.True(f.Root.Git.IsAncestor(was, tip.Thin));
        Assert.Equal(tip.Thin, RemoteGit("rev-parse", "refs/heads/feature-x").Trim());
    }

    [Fact]
    public void Restore_OnAnotherRoot_RebuildsTheBranch()
    {
        f.Setup();
        MakeBranch("feature-x");
        Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok);

        var far = Far();
        var list = Backup.List(far.Root);
        var entry = list.Single(e => e.Kind == "branch");
        Assert.Equal("feature-x", entry.Name);
        Assert.Equal(2, entry.Commits);
        Assert.Equal(["[gui] first: edit one, add one", "second: rename one, delete one"], entry.Subjects);
        Assert.Equal(far.Co.Name, entry.Checkout);
        Assert.Empty(entry.Drift);
        Assert.False(entry.ExistsHere);

        var r = Backup.Restore(far.Root, "feature-x");
        Assert.True(r.Ok, r.Why + "\n" + string.Join("\n", r.Conflicted));
        Assert.False(r.Relinked);
        Assert.Equal(2, r.Applied);
        Assert.Empty(r.Drift);

        Assert.Equal("int engine = 2;\n", Read(r.Path, "schmetterling/engine.cpp"));
        Assert.Equal("brand new\n", Read(r.Path, "fort/dev/new/file.txt"));
        Assert.True(File.Exists(Path.Combine(r.Path, "fort", "dev", "game_renamed.cpp")));
        Assert.False(File.Exists(Path.Combine(r.Path, "fort", "dev", "game.cpp")));
        Assert.False(File.Exists(Path.Combine(r.Path, "fort", "tools", "tool.py")));
        Assert.True(far.Root.Git.IsClean(r.Path));

        var log = far.Root.Git.Log(r.Path, far.Root.SnapshotRef(far.Co) + "..refs/heads/feature-x", 10);
        Assert.Equal(["second: rename one, delete one", "[gui] first: edit one, add one"], log.Select(c => c.Subject).ToArray());
        Assert.Equal("Ada Lovelace", far.Root.Git.Details(log[1].Sha).Author);
        Assert.DoesNotContain("sg-thin", far.Root.Git.Body(log[0].Sha));

        // The far side owns that branch on the remote now. Its commits are its own, so its first backup
        // writes the branch again under the lease the restore left it, and the one after that has nothing to send.
        var again = Backup.Run(far.Root);
        Assert.Equal("pushed", Item(again, "branch", "feature-x").State);
        Assert.Equal("up to date", Item(Backup.Run(far.Root), "branch", "feature-x").State);
        Assert.Equal(0, Ops.Status(far.Root, checkSvn: false).Worktrees.Single().NotBackedUp);
    }

    [Fact]
    public void Restore_OntoACheckoutThatMovedOn_MergesAndReportsTheDrift()
    {
        f.Setup();
        MakeBranch("feature-drift");
        Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok);

        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        Fixture.Put(other, "sub/data.txt", "engine data\nand a line from somebody else\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "a line the backup never saw");

        var far = Far();
        Ops.Sync(far.Root, far.Co);

        var r = Backup.Restore(far.Root, "feature-drift");
        Assert.True(r.Ok, r.Why + "\n" + string.Join("\n", r.Conflicted));
        Assert.Equal(2, r.Applied);
        Assert.NotEmpty(r.Drift);
        Assert.Contains(r.Drift, d => d.Where == "schmetterling");
        Assert.Equal("int engine = 2;\n", Read(r.Path, "schmetterling/engine.cpp"));
        Assert.Equal("engine data\nand a line from somebody else\n", Read(r.Path, "schmetterling/sub/data.txt"));
    }

    [Fact]
    public void Restore_WhenTheStoreStillHasTheCommits_PointsAtThem()
    {
        f.Setup();
        var wt = MakeBranch("feature-x");
        var tip = f.Root.Git.RefSha("refs/heads/feature-x")!;
        Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok);

        Ops.Remove(f.Root, "feature-x", force: true);
        Assert.Null(f.Root.Git.RefSha("refs/heads/feature-x"));

        var r = Backup.Restore(f.Root, "feature-x");
        Assert.True(r.Ok, r.Why);
        Assert.True(r.Relinked);
        Assert.Equal(tip, f.Root.Git.RefSha("refs/heads/feature-x"));
        Assert.Equal(wt, r.Path);
        Assert.Equal("int engine = 2;\n", Read(r.Path, "schmetterling/engine.cpp"));
    }

    [Fact]
    public void UncommittedChanges_GoUpAndComeBack_ForAWorktreeAndForTheCheckout()
    {
        f.Setup();
        var wt = MakeBranch("feature-x");
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 3; // not committed\n");
        Fixture.Put(wt, "fort/dev/untracked.txt", "never added\n");
        Fixture.Put(f.Checkout, "CMakeLists.txt", "project(fort)\n# a local edit in the checkout\n");
        Backup.Set(f.Root, Remote());

        var r = Backup.Run(f.Root);
        Assert.True(r.Ok, string.Join("\n", r.Items.Select(i => i.State + " " + i.Why)));
        Assert.Equal("pushed", Item(r, "wip", "feature-x").State);
        Assert.Equal("pushed", Item(r, "edits", f.Co.Name).State);
        // Backing up did not touch either working copy.
        Assert.Equal("int engine = 3; // not committed\n", Read(wt, "schmetterling/engine.cpp"));
        Assert.True(File.Exists(Path.Combine(wt, "fort", "dev", "untracked.txt")));
        Assert.Contains("a local edit", Read(f.Checkout, "CMakeLists.txt"));
        // The same changes again are the same commit again.
        Assert.Equal("up to date", Item(Backup.Run(f.Root), "wip", "feature-x").State);

        var far = Far();
        Assert.Contains(Backup.List(far.Root), e => e.Kind == "branch" && e.Name == "feature-x" && e.HasWip);
        var back = Backup.Restore(far.Root, "feature-x", wip: true);
        Assert.True(back.Ok, back.Why);
        Assert.True(back.WipWritten, back.WipShelf);
        Assert.Empty(back.WipConflicted);
        Assert.Equal("int engine = 3; // not committed\n", Read(back.Path, "schmetterling/engine.cpp"));
        Assert.Equal("never added\n", Read(back.Path, "fort/dev/untracked.txt"));
        Assert.Equal(2, far.Root.Git.CountCommits(far.Root.SnapshotRef(far.Co), "refs/heads/feature-x"));
        Assert.Empty(Shelf.List(far.Root));

        // The checkout's own local edits come back into the far checkout, as svn sees them.
        var edits = Backup.Restore(far.Root, f.Co.Name);
        Assert.True(edits.WipWritten, edits.WipShelf);
        Assert.Contains("a local edit", Read(far.Co.Path, "CMakeLists.txt"));
        Assert.Contains(far.Root.Svn.Status(far.Co.Path, noIgnore: false), e => e.Path == "CMakeLists.txt" && e.Item == "modified");

        // Committing the worktree's changes at home takes its wip off the remote on the next backup.
        f.Root.Git.Ok(wt, "add", "-A");
        f.Root.Git.Ok(wt, "commit", "-q", "-m", "third: the changes that were waiting");
        var after = Backup.Run(f.Root);
        Assert.DoesNotContain(after.Items, i => i.Kind == "wip" && i.Name == "feature-x");
        Assert.DoesNotContain("refs/sg/wip/feature-x", RemoteGit("ls-remote", "--refs", _remote));
    }

    /// <summary>
    /// Two checkouts of one root cannot have two worktrees of one name: the store has one branch
    /// namespace. What they can have is a branch named like a checkout, when the worktrees live
    /// elsewhere, and the two folders' uncommitted changes must not land on one ref.
    /// </summary>
    [Fact]
    public void ABranchNamedLikeACheckout_KeepsItsChangesApartFromTheCheckouts()
    {
        f.Setup();
        f.Root.Config.WorktreeRoot = Path.Combine(f.Base, "worktrees");
        f.Root.Save();
        var wt = Ops.Branch(f.Root, f.Co.Name, f.Co).Path;
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 5; // in the worktree\n");
        Fixture.Put(f.Checkout, "CMakeLists.txt", "project(fort)\n# in the checkout\n");
        Backup.Set(f.Root, Remote());

        var r = Backup.Run(f.Root);
        Assert.True(r.Ok, string.Join("\n", r.Items.Select(i => i.State + " " + i.Why)));
        Assert.Equal("refs/sg/wip/" + f.Co.Name, Item(r, "wip", f.Co.Name).RemoteRef);
        Assert.Equal("refs/sg/edits/" + f.Co.Name, Item(r, "edits", f.Co.Name).RemoteRef);
        var refs = RemoteGit("ls-remote", "--refs", _remote);
        Assert.Contains("refs/sg/wip/" + f.Co.Name, refs);
        Assert.Contains("refs/sg/edits/" + f.Co.Name, refs);

        // On the far side the branch wins the name, and the checkout's edits are reached by the checkout's.
        var far = Far();
        var branch = Backup.Restore(far.Root, f.Co.Name, asBranch: "same-name", wip: true);
        Assert.True(branch.Ok, branch.Why);
        Assert.Equal("int engine = 5; // in the worktree\n", Read(branch.Path, "schmetterling/engine.cpp"));
        Assert.DoesNotContain("in the checkout", Read(branch.Path, "CMakeLists.txt"));
    }

    [Fact]
    public void Shelves_GoUp_AndComeBackOnTheShelf()
    {
        f.Setup();
        var wt = MakeBranch("feature-x");
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 4; // half a fix\n");
        var shelf = Shelf.Save(f.Root, wt, null, "half a fix").Shelf;
        Backup.Set(f.Root, Remote());

        var r = Backup.Run(f.Root);
        Assert.True(r.Ok, string.Join("\n", r.Items.Select(i => i.State + " " + i.Why)));
        Assert.Equal("pushed", Item(r, "shelf", shelf.Id).State);

        var far = Far();
        var entry = Backup.List(far.Root).Single(e => e.Kind == "shelf");
        Assert.Equal("half a fix", entry.Title);
        Assert.Equal("feature-x", entry.Branch);

        var back = Backup.Restore(far.Root, "feature-x");
        Assert.True(back.Ok, back.Why);
        Assert.Single(back.Shelves);
        var made = Shelf.List(far.Root).Single();
        Assert.Equal("half a fix", made.Title);
        Assert.Equal("feature-x", made.Branch);
        Assert.Equal(back.Path, made.Path);
        var restored = Shelf.Restore(far.Root, made.Id);
        Assert.Empty(restored.Conflicted);
        Assert.Equal("int engine = 4; // half a fix\n", Read(back.Path, "schmetterling/engine.cpp"));
    }

    [Fact]
    public void Reconcile_WritesOverAnOlderCopyOnTheRemote_WithNoForce()
    {
        f.Setup();
        var wt = MakeBranch("feature-x");
        Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok);

        // The remote is rolled back to a commit this branch still sits above: an older copy of the same
        // history, the shape a second machine that is behind leaves. The tip here is ahead of it.
        var older = RemoteGit("rev-parse", "refs/heads/feature-x~1").Trim();
        RemoteGit("update-ref", "refs/heads/feature-x", older);

        Fixture.Put(wt, "fort/dev/new/file.txt", "brand new\nand a line\n");
        f.Root.Git.Ok(wt, "commit", "-q", "-am", "third: one more line");
        var r = Backup.Run(f.Root);
        var item = Item(r, "branch", "feature-x");
        Assert.True(r.Ok, item.State + " " + item.Why);
        Assert.Equal("pushed", item.State);
        Assert.True(item.Reconciled);
        Assert.Equal(item.Thin, RemoteGit("rev-parse", "refs/heads/feature-x").Trim());
    }

    [Fact]
    public void Divergence_IsRejected_UnlessForced()
    {
        f.Setup();
        MakeBranch("feature-x");
        Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok);

        // The other PC has its own branch of the same name, on commits this store has never seen. Neither
        // side is an ancestor of the other, so a backup would drop one of them: it is refused, exit 10.
        var far = Far();
        var fwt = Ops.Branch(far.Root, "feature-x", far.Co).Path;
        Fixture.Put(fwt, "schmetterling/engine.cpp", "int engine = 99; // the other PC's own work\n");
        far.Root.Git.Ok(fwt, "add", "-A");
        far.Root.Git.Ok(fwt, "commit", "-q", "-m", "the other PC");

        var r = Backup.Run(far.Root);
        var item = Item(r, "branch", "feature-x");
        Assert.True(item.Rejected, item.State + " " + item.Why);
        Assert.False(r.Ok);
        Assert.Contains("different work", item.Why);
        Assert.False(item.Reconciled);

        var forced = Backup.Run(far.Root, force: true);
        var taken = Item(forced, "branch", "feature-x");
        Assert.Equal("pushed", taken.State);
        Assert.Equal(taken.Thin, RemoteGit("rev-parse", "refs/heads/feature-x").Trim());
    }

    [Fact]
    public void RemoteNewer_IsReportedAsBehind_NotPushedOver()
    {
        f.Setup();
        var wt = MakeBranch("feature-x");
        Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok);
        Fixture.Put(wt, "fort/dev/new/file.txt", "brand new\nand a line\n");
        f.Root.Git.Ok(wt, "commit", "-q", "-am", "third: one more line");
        var newer = Item(Backup.Run(f.Root), "branch", "feature-x").Thin;

        // This root loses its memory of the push and rewinds a commit: the remote now holds a strict
        // descendant of the tip here, so a push would send an older copy. Backup says behind, sends nothing.
        f.Root.Git.DeleteRef(Backup.PushedRef("branch", "feature-x"));
        f.Root.Git.ResetHard(wt, "HEAD~1");

        var r = Backup.Run(f.Root);
        var item = Item(r, "branch", "feature-x");
        Assert.True(r.Ok, item.State + " " + item.Why);
        Assert.True(item.Behind, item.State);
        Assert.Equal(1, r.Behind);
        Assert.Equal(newer, RemoteGit("rev-parse", "refs/heads/feature-x").Trim());
    }

    [Fact]
    public void ForceRestore_WritesOverABranchThatIsHere()
    {
        f.Setup();
        MakeBranch("feature-x");
        Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok);

        var far = Far();
        var first = Backup.Restore(far.Root, "feature-x");
        Assert.True(first.Ok, first.Why);

        // The branch drifts on the far side - a commit that is not in the backup, and an uncommitted edit.
        Fixture.Put(first.Path, "schmetterling/engine.cpp", "int engine = 123; // drifted away\n");
        far.Root.Git.Ok(first.Path, "commit", "-q", "-am", "far drift");
        Fixture.Put(first.Path, "fort/dev/new/file.txt", "uncommitted, dropped on overwrite\n");

        // Without force, a name that is here is refused. With force, the branch and its worktree are the backup again.
        Assert.Throws<SgException>(() => Backup.Restore(far.Root, "feature-x"));
        var again = Backup.Restore(far.Root, "feature-x", force: true);
        Assert.True(again.Ok, again.Why);
        Assert.True(again.Replaced);
        Assert.Equal("int engine = 2;\n", Read(again.Path, "schmetterling/engine.cpp"));
        Assert.Equal("brand new\n", Read(again.Path, "fort/dev/new/file.txt"));
        Assert.Equal(2, far.Root.Git.CountCommits(far.Root.SnapshotRef(far.Co), "refs/heads/feature-x"));
    }

    [Fact]
    public void Rebase_RewritesTheBranch_AndTheBackupFollowsUnderItsLease()
    {
        f.Setup();
        var wt = MakeBranch("feature-x");
        Backup.Set(f.Root, Remote());
        var first = Item(Backup.Run(f.Root), "branch", "feature-x");

        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        Fixture.Put(other, "sub/data.txt", "engine data\nand a line from somebody else\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "somebody else");
        Ops.Sync(f.Root, f.Co);
        Assert.True(Ops.Rebase(f.Root, wt).Ok);
        Assert.Equal(2, Ops.Status(f.Root, checkSvn: false).Worktrees.Single().NotBackedUp);

        var r = Backup.Run(f.Root);
        var item = Item(r, "branch", "feature-x");
        Assert.Equal("pushed", item.State);
        Assert.NotEqual(first.Thin, item.Thin);
        Assert.False(f.Root.Git.IsAncestor(first.Thin, item.Thin));
        Assert.Equal(item.Thin, RemoteGit("rev-parse", "refs/heads/feature-x").Trim());
        Assert.Equal(0, Ops.Status(f.Root, checkSvn: false).Worktrees.Single().NotBackedUp);
    }

    [Fact]
    public void Prune_ListsWhatIsOnlyThere_AndDeletesItWhenAsked()
    {
        f.Setup();
        MakeBranch("feature-x");
        Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok);

        Ops.Remove(f.Root, "feature-x", force: true);
        var check = Backup.Run(f.Root, check: true);
        Assert.Contains("refs/heads/feature-x", check.RemoteOnly);
        Assert.Contains("refs/heads/feature-x", RemoteGit("ls-remote", "--refs", _remote));

        Assert.Equal(["refs/heads/feature-x"], Backup.Prune(f.Root, delete: false));
        Assert.Contains("refs/heads/feature-x", RemoteGit("ls-remote", "--refs", _remote));
        Assert.Equal(["refs/heads/feature-x"], Backup.Prune(f.Root, delete: true));
        Assert.DoesNotContain("refs/heads/feature-x", RemoteGit("ls-remote", "--refs", _remote));
        Assert.Empty(Backup.Prune(f.Root, delete: false));
    }

    [Fact]
    public void Prefix_KeepsTwoRootsApartInOneRepository()
    {
        f.Setup();
        MakeBranch("feature-x");
        Backup.Set(f.Root, Remote(), prefix: "desk");
        var r = Backup.Run(f.Root);
        Assert.Equal("refs/heads/desk/feature-x", Item(r, "branch", "feature-x").RemoteRef);
        Assert.Contains("refs/heads/desk/feature-x", RemoteGit("ls-remote", "--refs", _remote));

        var far = Far();
        Backup.Set(far.Root, Remote(), prefix: "laptop");
        Assert.Empty(Backup.List(far.Root));
        Backup.Set(far.Root, Remote(), prefix: "desk");
        Assert.Single(Backup.List(far.Root), e => e.Name == "feature-x");
        Assert.True(Backup.Restore(far.Root, "feature-x").Ok);
    }

    [Fact]
    public void Set_RefusesAUrlThatIsNotARepository()
    {
        f.Setup();
        var ex = Assert.Throws<SgException>(() => Backup.Set(f.Root, Path.Combine(f.Base, "nowhere.git")));
        Assert.Contains("cannot reach", ex.Message);
        Assert.Null(f.Root.Config.Backup);
        Assert.Throws<SgException>(() => Backup.Run(f.Root));
    }
}
