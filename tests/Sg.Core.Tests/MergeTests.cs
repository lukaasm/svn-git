using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Taking changes from another server branch into the checkout: a cherry pick of named revisions, and
/// everything a branch has that this one has not. Both land as local changes; neither commits.
/// </summary>
public sealed class MergeTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();

    /// <summary>The game repository, whose "fort" branch the checkout has under fort/dev.</summary>
    string GameFort => f.GameUrl + "/branches/fort/dev";

    /// <summary>A second branch of the same repository, with commits of its own on it.</summary>
    string MakeFixBranch(params (string File, string Text, string Message)[] commits)
    {
        var url = f.GameUrl + "/branches/fix/dev";
        f.Svn.Ok(null, "copy", "--parents", "--non-interactive", "-m", "branch for the fix", GameFort, url);
        var wc = f.OtherWc(url);
        foreach (var (file, text, message) in commits)
        {
            Fixture.Put(wc, file, text);
            f.Svn.Ok(wc, "add", "--non-interactive", "--force", ".");
            f.Svn.Ok(wc, "commit", "--non-interactive", "-m", message);
        }
        return url;
    }

    MergeTarget GameTarget()
    {
        var t = Merge.Targets(f.Root, f.Co).FirstOrDefault(x => x.Wc.Equals("fort/dev", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(t);
        return t!;
    }

    static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void The_targets_are_the_checkout_root_and_every_external()
    {
        f.Setup();
        var targets = Merge.Targets(f.Root, f.Co);
        Assert.Equal("root", targets[0].Label);
        Assert.Contains(targets, t => t.Wc == "fort/dev");
        Assert.Contains(targets, t => t.Wc == "schmetterling");
        Assert.All(targets, t => Assert.NotEqual("", t.ReposRoot));
    }

    [Fact]
    public void The_sources_are_the_other_branches_of_the_same_repository()
    {
        f.Setup();
        MakeFixBranch(("bugfix.cpp", "fixed\n", "the fix"));
        var sources = Merge.Sources(f.Root, GameTarget());

        Assert.Contains(sources, s => s.Name == "fix");
        Assert.Contains(sources, s => s.Name == "trunk");
        // Its own branch is not one of them.
        Assert.DoesNotContain(sources, s => s.Name == "fort");
        // Every one of them names the same folder inside the branch, so the merge lines up.
        Assert.All(sources.Where(s => s.Name != "trunk"), s => Assert.EndsWith("/dev", s.Url));
    }

    [Fact]
    public void A_dry_run_says_what_would_land_and_writes_nothing()
    {
        f.Setup();
        var url = MakeFixBranch(("bugfix.cpp", "fixed\n", "the fix"));
        var target = GameTarget();

        var r = Merge.Run(f.Root, f.Co, target, url, null, dryRun: true);

        Assert.True(r.DryRun);
        Assert.Contains(r.Changed, c => c.Path.EndsWith("bugfix.cpp", StringComparison.OrdinalIgnoreCase));
        Assert.True(r.Clean);
        Assert.False(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "bugfix.cpp")));
        Assert.Empty(f.CheckoutChanges());
    }

    [Fact]
    public void Merging_a_whole_branch_brings_its_files_in_as_local_changes()
    {
        f.Setup();
        var url = MakeFixBranch(("bugfix.cpp", "fixed\n", "the fix"));

        var r = Merge.Run(f.Root, f.Co, GameTarget(), url, null, dryRun: false);

        Assert.True(r.Clean, r.Output);
        Assert.Equal("fixed\n", Read(f.Checkout, "fort/dev/bugfix.cpp"));
        // It is a local change of the checkout, not a commit: the SVN commit window is where it goes out.
        Assert.Contains(f.CheckoutChanges(), c => c.Path.Replace('\\', '/').EndsWith("fort/dev/bugfix.cpp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_cherry_pick_takes_the_named_revision_and_leaves_the_others()
    {
        f.Setup();
        var url = MakeFixBranch(
            ("one.cpp", "one\n", "the first one"),
            ("two.cpp", "two\n", "the second one"));

        var revisions = Merge.Revisions(f.Root, url, 10);
        var second = revisions.First(x => x.Message.Trim() == "the second one");

        var r = Merge.Run(f.Root, f.Co, GameTarget(), url, new[] { second.Revision }, dryRun: false);

        Assert.True(r.Clean, r.Output);
        Assert.Equal(new[] { second.Revision }, r.Revisions);
        Assert.True(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "two.cpp")));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "one.cpp")));
    }

    [Fact]
    public void Two_revisions_picked_at_once_both_land()
    {
        f.Setup();
        var url = MakeFixBranch(
            ("one.cpp", "one\n", "the first one"),
            ("skip.cpp", "skip\n", "the one nobody wants"),
            ("two.cpp", "two\n", "the second one"));

        var revisions = Merge.Revisions(f.Root, url, 10);
        long At(string message) => revisions.First(x => x.Message.Trim() == message).Revision;

        var r = Merge.Run(f.Root, f.Co, GameTarget(), url, new[] { At("the second one"), At("the first one") }, dryRun: false);

        Assert.True(r.Clean, r.Output);
        // Applied oldest first whatever order they were given in.
        Assert.Equal([At("the first one"), At("the second one")], r.Revisions);
        Assert.True(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "one.cpp")));
        Assert.True(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "two.cpp")));
        Assert.False(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "skip.cpp")));
    }

    [Fact]
    public void The_revisions_of_a_source_come_back_newest_first_with_their_paths()
    {
        f.Setup();
        var url = MakeFixBranch(("one.cpp", "one\n", "the first one"), ("two.cpp", "two\n", "the second one"));
        var revisions = Merge.Revisions(f.Root, url, 10);

        Assert.Equal("the second one", revisions[0].Message.Trim());
        Assert.True(revisions[0].Revision > revisions[1].Revision);
        Assert.Contains(revisions[0].Paths, p => p.Path.EndsWith("two.cpp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_working_copy_with_local_changes_is_refused()
    {
        f.Setup();
        var url = MakeFixBranch(("bugfix.cpp", "fixed\n", "the fix"));
        File.AppendAllText(Path.Combine(f.Checkout, "fort", "dev", "game.cpp"), "// edited here\n");

        var ex = Assert.Throws<SgException>(() => Merge.Run(f.Root, f.Co, GameTarget(), url, null, dryRun: false));
        Assert.Contains("local changes", ex.Message);
        Assert.False(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "bugfix.cpp")));
    }

    [Fact]
    public void A_dry_run_still_answers_over_a_working_copy_with_local_changes()
    {
        f.Setup();
        var url = MakeFixBranch(("bugfix.cpp", "fixed\n", "the fix"));
        File.AppendAllText(Path.Combine(f.Checkout, "fort", "dev", "game.cpp"), "// edited here\n");

        var r = Merge.Run(f.Root, f.Co, GameTarget(), url, null, dryRun: true);
        Assert.Contains(r.Changed, c => c.Path.EndsWith("bugfix.cpp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_revision_can_be_taken_back_out_again()
    {
        f.Setup();
        var url = MakeFixBranch(("crashfix.cpp", "fixed\n", "the fix"));
        var target = GameTarget();
        var fix = Merge.Revisions(f.Root, url, 10).First(x => x.Message.Trim() == "the fix").Revision;

        // In, committed, and then back out again: the shape of undoing a revision somebody regrets.
        Merge.Run(f.Root, f.Co, target, url, new[] { fix }, dryRun: false);
        Assert.True(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "crashfix.cpp")));
        Ops.SvnCommit(f.Root, f.Co, new List<string> { "fort/dev" }, "take the fix");

        var back = Merge.Run(f.Root, f.Co, target, url, new[] { fix }, dryRun: false, reverse: true);

        Assert.True(back.Reverse);
        Assert.True(back.Clean, back.Output);
        Assert.False(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "crashfix.cpp")));
    }

    [Fact]
    public void Taking_a_revision_out_without_naming_one_is_refused()
    {
        f.Setup();
        var url = MakeFixBranch(("crashfix.cpp", "fixed\n", "the fix"));
        var ex = Assert.Throws<SgException>(() => Merge.Run(f.Root, f.Co, GameTarget(), url, null, dryRun: true, reverse: true));
        Assert.Contains("needs the revisions named", ex.Message);
    }

    /// <summary>A branch of the mono repository whose externals point at the fix branches of the others.</summary>
    string MakeMonoFixBranch()
    {
        var url = f.MonoUrl + "/branches/fix";
        f.Svn.Ok(null, "copy", "--parents", "--non-interactive", "-m", "a branch of the whole thing", f.MonoUrl + "/trunk", url);
        var wc = f.OtherWc(url);
        // Point its externals at the fix branches, the way a real branch of this monorepo does.
        Prop(wc, ".", "svn:externals", f.EngineUrl + "/branches/fix/dev schmetterling\n");
        Prop(wc, "fort", "svn:externals",
            f.GameUrl + "/branches/fix/dev dev\n" + f.GameUrl + "/branches/fort/builds builds\n" + f.GameUrl + "/branches/fort/tools tools\n");
        f.Svn.Ok(wc, "commit", "--non-interactive", "-m", "point the externals at the fix branches");
        return url;
    }

    void Prop(string wc, string rel, string name, string value)
    {
        var file = Path.Combine(f.Base, "p-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(file, value);
        f.Svn.Ok(wc, "propset", name, "-F", file, rel);
        File.Delete(file);
    }

    MergeTarget RootTarget() => Merge.Targets(f.Root, f.Co).First(t => t.Wc.Length == 0);

    [Fact]
    public void A_merge_of_the_root_covers_every_external_the_source_has_at_the_same_folder()
    {
        f.Setup();
        // The fix branches the source's externals will point at.
        f.Svn.Ok(null, "copy", "--parents", "--non-interactive", "-m", "engine fix", f.EngineUrl + "/branches/fort/dev", f.EngineUrl + "/branches/fix/dev");
        f.Svn.Ok(null, "copy", "--parents", "--non-interactive", "-m", "game fix", GameFort, f.GameUrl + "/branches/fix/dev");
        var url = MakeMonoFixBranch();

        var pairs = Merge.Pairs(f.Root, f.Co, RootTarget(), url);

        // The root itself, plus the two externals the source branch redirects.
        Assert.Contains(pairs, p => p.Target.Wc.Length == 0 && p.SourceUrl == url);
        Assert.Contains(pairs, p => p.Target.Wc == "schmetterling" && p.SourceUrl.EndsWith("/branches/fix/dev"));
        Assert.Contains(pairs, p => p.Target.Wc == "fort/dev" && p.SourceUrl.EndsWith("/branches/fix/dev"));
        // fort/builds and fort/tools point at the same branch on both sides: never branched, nothing
        // on the other side of them, so they are left out rather than merged with themselves.
        Assert.DoesNotContain(pairs, p => p.Target.Wc == "fort/builds");
        Assert.DoesNotContain(pairs, p => p.Target.Wc == "fort/tools");
    }

    [Fact]
    public void The_externals_a_branch_declares_are_read_off_the_server_by_folder()
    {
        f.Setup();
        var url = MakeMonoFixBranch();
        var theirs = Merge.SourceExternals(f.Root, url);
        Assert.Equal(
            new[] { "fort/builds", "fort/dev", "fort/tools", "schmetterling" },
            theirs.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.EndsWith("/branches/fix/dev", theirs["fort/dev"]);
        Assert.EndsWith("/branches/fort/builds", theirs["fort/builds"]);
    }

    [Fact]
    public void An_external_target_is_only_itself()
    {
        f.Setup();
        var url = MakeFixBranch(("bugfix.cpp", "fixed\n", "the fix"));
        var pairs = Merge.Pairs(f.Root, f.Co, GameTarget(), url);
        Assert.Single(pairs);
        Assert.Equal("fort/dev", pairs[0].Target.Wc);
    }

    [Fact]
    public void A_merge_of_the_root_brings_in_what_the_externals_changed()
    {
        f.Setup();
        // A change on the game fix branch, which the mono fix branch's externals point at.
        f.Svn.Ok(null, "copy", "--parents", "--non-interactive", "-m", "engine fix", f.EngineUrl + "/branches/fort/dev", f.EngineUrl + "/branches/fix/dev");
        f.Svn.Ok(null, "copy", "--parents", "--non-interactive", "-m", "game fix", GameFort, f.GameUrl + "/branches/fix/dev");
        var gameWc = f.OtherWc(f.GameUrl + "/branches/fix/dev");
        Fixture.Put(gameWc, "deep.cpp", "deep in an external\n");
        f.Svn.Ok(gameWc, "add", "--non-interactive", "deep.cpp");
        f.Svn.Ok(gameWc, "commit", "--non-interactive", "-m", "a change inside an external");
        var url = MakeMonoFixBranch();

        var pairs = Merge.Pairs(f.Root, f.Co, RootTarget(), url);
        var r = Merge.RunAll(f.Root, f.Co, pairs, null, dryRun: false);

        Assert.True(r.Clean, r.Output);
        // The file lives in an external; a merge that stopped at the root would never have seen it.
        Assert.True(File.Exists(Path.Combine(f.Checkout, "fort", "dev", "deep.cpp")), r.Output);
        Assert.True(r.Parts.Count > 1, "the root merge covered more than one working copy");
    }

    [Fact]
    public void What_is_offered_says_which_working_copy_each_revision_belongs_to()
    {
        f.Setup();
        var url = MakeFixBranch(("one.cpp", "one\n", "the first one"), ("two.cpp", "two\n", "the second one"));
        var pairs = Merge.Pairs(f.Root, f.Co, GameTarget(), url);

        var offered = Merge.Offered(f.Root, f.Co, pairs, 20);

        Assert.NotEmpty(offered);
        Assert.All(offered, o => Assert.Equal("fort/dev", o.Pair.Target.Wc));
        Assert.Contains(offered, o => o.Entry.Message.Trim() == "the second one");
        // Newest first.
        Assert.Equal(offered.Select(o => o.Revision).OrderByDescending(x => x), offered.Select(o => o.Revision));
    }

    [Fact]
    public void A_revision_already_merged_is_marked_so_it_can_be_folded_away()
    {
        f.Setup();
        var url = MakeFixBranch(("one.cpp", "one\n", "the first one"), ("two.cpp", "two\n", "the second one"));
        var pairs = Merge.Pairs(f.Root, f.Co, GameTarget(), url);
        var first = Merge.Offered(f.Root, f.Co, pairs, 20).First(o => o.Entry.Message.Trim() == "the first one");

        Assert.False(first.Merged);
        Merge.RunAll(f.Root, f.Co, pairs, new[] { first }, dryRun: false);
        Ops.SvnCommit(f.Root, f.Co, new List<string> { "fort/dev" }, "take the first one");

        var after = Merge.Offered(f.Root, f.Co, pairs, 20);
        Assert.True(after.First(o => o.Revision == first.Revision).Merged, "the revision that went in is marked as merged");
        Assert.False(after.First(o => o.Entry.Message.Trim() == "the second one").Merged);
    }

    [Fact]
    public void Only_the_revisions_of_a_working_copy_reach_it()
    {
        f.Setup();
        var url = MakeFixBranch(("one.cpp", "one\n", "the first one"));
        var pairs = Merge.Pairs(f.Root, f.Co, GameTarget(), url);
        var one = Merge.Offered(f.Root, f.Co, pairs, 20).First(o => o.Entry.Message.Trim() == "the first one");

        // A pair that none of the picked revisions belong to takes nothing at all.
        var other = new MergePair(RootTarget(), f.MonoUrl + "/trunk");
        var r = Merge.RunAll(f.Root, f.Co, new[] { other, one.Pair }, new[] { one }, dryRun: false);

        Assert.Single(r.Parts);
        Assert.Equal("fort/dev", r.Parts[0].Target);
    }

    [Fact]
    public void A_source_in_another_repository_is_refused()
    {
        f.Setup();
        var problems = Merge.Problems(f.Root, f.Co, GameTarget(), f.EngineUrl + "/branches/fort/dev");
        Assert.Contains(problems, p => p.Contains("another repository"));
    }

    [Fact]
    public void Merging_a_branch_onto_itself_is_refused()
    {
        f.Setup();
        var problems = Merge.Problems(f.Root, f.Co, GameTarget(), GameFort);
        Assert.Contains(problems, p => p.Contains("same branch"));
    }

    [Fact]
    public void A_change_to_the_same_line_comes_back_as_a_conflict_rather_than_a_failure()
    {
        f.Setup();
        // The fix branch rewrites game.cpp; so does the fort branch, on the same line.
        var url = MakeFixBranch(("game.cpp", "int game = 2;\n", "changed on the fix branch"));
        var other = f.OtherWc(GameFort);
        Fixture.Put(other, "game.cpp", "int game = 3;\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "changed on the fort branch");
        f.Root.Svn.Update(f.Checkout);

        var r = Merge.Run(f.Root, f.Co, GameTarget(), url, null, dryRun: false);

        Assert.False(r.Clean);
        Assert.Contains(r.Conflicts, p => p.EndsWith("game.cpp", StringComparison.OrdinalIgnoreCase));
    }
}
