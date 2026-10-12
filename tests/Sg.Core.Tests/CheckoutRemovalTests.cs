using System.Text.Json;

namespace Sg.Core.Tests;

/// <summary>
/// Removing a checkout undoes checkout add and every branch built on it since. The folder stays with
/// every file in it; only what sg wrote there goes, so the same folder registers again afterwards.
/// </summary>
public sealed class CheckoutRemovalTests
{
    [Fact]
    public void Removing_a_checkout_takes_its_branches_and_sg_traces_and_keeps_the_folder()
    {
        using var f = new Fixture(); f.Setup();
        var git = f.Root.Git;
        var name = f.Co.Name;
        var ahead = Ops.Branch(f.Root, "ahead", f.Co).Path;
        Fixture.Put(ahead, "notes.txt", "committed on the branch\n");
        git.Ok(ahead, "add", "notes.txt");
        git.Ok(ahead, "commit", "-q", "-m", "not in SVN");
        var dirty = Ops.Branch(f.Root, "dirty", f.Co).Path;
        Fixture.Put(dirty, "CMakeLists.txt", "edited in the worktree\n");
        Fixture.Put(f.Checkout, "CMakeLists.txt", "edited in the checkout\n");
        var shelf = Shelf.Save(f.Root, f.Checkout, null, "kept by name").Shelf;
        Fixture.Put(f.Checkout, "CMakeLists.txt", "edited after the shelf\n");
        var shared = Directory.GetFiles(Path.Combine(f.Checkout, "fort", "builds"), "*", SearchOption.AllDirectories).Length;
        var ignores = f.Root.IgnoreFileFor(f.Co);
        Assert.True(File.Exists(ignores));

        var plan = CheckoutRemoval.Preview(f.Root, name);
        Assert.True(plan.Ready, string.Join("\n", plan.Blockers));
        Assert.Equal(["ahead", "dirty"], plan.Branches.Select(b => b.Branch));
        Assert.Equal(1, plan.Branches[0].Ahead);
        Assert.Equal(0, plan.Branches[0].DirtyFiles);
        Assert.True(plan.Branches[1].DirtyFiles > 0);
        Assert.Equal(1, plan.Shelves);

        CheckoutRemoval.Apply(f.Root, plan);

        Assert.Empty(f.Root.Config.Checkouts);
        Assert.Empty(SgConfig.Load(f.Root.ConfigPath).Checkouts);
        var reopened = SgRoot.Open(f.RootDir, f.Log);
        var store = reopened.Git;
        Assert.False(Directory.Exists(ahead));
        Assert.False(Directory.Exists(dirty));
        Assert.Null(store.RefSha("refs/heads/ahead"));
        Assert.Null(store.RefSha("refs/heads/dirty"));
        Assert.Empty(store.BranchBases());
        Assert.Null(store.RefSha(SgRoot.SnapshotRefPrefix + name));
        Assert.DoesNotContain(store.WorktreeList(), w => !w.Bare);
        Assert.False(File.Exists(ignores));

        // The working copy is the user's, and nothing in it changed but the pointer sg put there.
        Assert.False(File.Exists(Path.Combine(f.Checkout, ".git")));
        Assert.True(Directory.Exists(Path.Combine(f.Checkout, ".svn")));
        Assert.Equal("edited after the shelf\n", File.ReadAllText(Path.Combine(f.Checkout, "CMakeLists.txt")));
        Assert.Equal(shared, Directory.GetFiles(Path.Combine(f.Checkout, "fort", "builds"), "*", SearchOption.AllDirectories).Length);

        // The shelf is kept by name: the folder registered again under it finds the shelf again.
        var again = Ops.CheckoutAdd(reopened, f.Checkout, skip: ["fort/builds"], junctions: ["fort/builds"]).Checkout;
        Assert.Equal(name, again.Name);
        Assert.Equal(shelf.Id, Assert.Single(Shelf.For(reopened, name, null)).Id);
    }

    [Fact]
    public void A_stale_preview_or_an_unfinished_operation_keeps_the_checkout()
    {
        using var f = new Fixture(); f.Setup();
        var path = Ops.Branch(f.Root, "feature", f.Co).Path;
        var plan = CheckoutRemoval.Preview(f.Root, f.Co.Name);
        Fixture.Put(path, "CMakeLists.txt", "edited after the preview\n");
        Assert.Throws<SgException>(() => CheckoutRemoval.Apply(f.Root, plan));
        Assert.True(Directory.Exists(path));
        Assert.Single(f.Root.Config.Checkouts);

        var record = new OperationRecord { Checkout = f.Co.Name, Branch = "feature", Path = path, Phase = OperationPhase.NeedsReview };
        Directory.CreateDirectory(Path.Combine(f.Root.StorePath, "operations"));
        AtomicFile.WriteAllText(Path.Combine(f.Root.StorePath, "operations", record.Id + ".json"), JsonSerializer.Serialize(record, SgConfig.JsonOptions));
        var blocked = CheckoutRemoval.Preview(f.Root, f.Co.Name);
        Assert.False(blocked.Ready);
        Assert.Throws<SgException>(() => CheckoutRemoval.Apply(f.Root, blocked));
        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(f.Checkout, ".git")));
        Assert.Single(f.Root.Config.Checkouts);
    }

    [Fact]
    public void A_worktree_paused_in_a_rebase_goes_with_its_checkout()
    {
        using var f = new Fixture(); f.Setup();
        var git = f.Root.Git;
        var wt = Ops.Branch(f.Root, "paused", f.Co).Path;
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 2;\n");
        git.Ok(wt, "add", "-A");
        git.Ok(wt, "commit", "-q", "-m", "branch edit");
        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        Fixture.Put(other, "engine.cpp", "int engine = 9;\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "server edit");
        Ops.Sync(f.Root, f.Co);
        Assert.True(Ops.Rebase(f.Root, wt).Conflict);

        var plan = CheckoutRemoval.Preview(f.Root, f.Co.Name);
        Assert.NotNull(Assert.Single(plan.Branches).Path);
        CheckoutRemoval.Apply(f.Root, plan);

        Assert.False(Directory.Exists(wt));
        Assert.Null(SgRoot.Open(f.RootDir, f.Log).Git.RefSha("refs/heads/paused"));
    }

    [Fact]
    public void A_git_clone_keeps_its_own_git_folder_and_loses_only_sgs_marker()
    {
        using var f = new GitFixture(); f.Setup();
        var name = f.Co.Name;
        var wt = Ops.Branch(f.Root, "feature", f.Co).Path;
        var marker = Path.Combine(f.Checkout, ".git", GitCheckoutVcs.RootMarker);
        Assert.True(File.Exists(marker));

        CheckoutRemoval.Apply(f.Root, CheckoutRemoval.Preview(f.Root, name));

        Assert.Empty(f.Root.Config.Checkouts);
        Assert.False(Directory.Exists(wt));
        Assert.False(File.Exists(marker));
        Assert.True(Directory.Exists(Path.Combine(f.Checkout, ".git")));
        Assert.Equal("", f.CloneChanges());
        var store = SgRoot.Open(f.RootDir, f.Log).Git;
        Assert.Null(store.RefSha(SgRoot.SnapshotRefPrefix + name));
        Assert.Null(store.RefSha(GitCheckoutVcs.UpstreamPrefix + name));
        Assert.False(Directory.Exists(GitTunnel.DirOf(f.Root, name)));
        Assert.Equal(name, Ops.CheckoutAdd(f.Root, f.Checkout).Checkout.Name);
    }
}
