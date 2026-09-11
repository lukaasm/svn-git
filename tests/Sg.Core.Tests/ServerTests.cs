using Xunit;

namespace Sg.Core.Tests;

public sealed class RuleTests
{
    [Fact]
    public void BranchRule_RewritesTheLastTrunkOrBranchesSegment()
    {
        Assert.Equal("http://s/svn/schmetterling/branches/x/dev", BranchRule.NewUrl("http://s/svn/schmetterling/branches/fort/dev", "x"));
        Assert.Equal("http://s/svn/riftbreaker/rbs/branches/x/dev", BranchRule.NewUrl("http://s/svn/riftbreaker/rbs/trunk/dev", "x"));
        Assert.Equal("http://s/svn/tools/branches/x", BranchRule.NewUrl("http://s/svn/tools/branches/fort_proto", "x"));
        Assert.Equal("^/branches/x/dev", BranchRule.NewUrl("^/trunk/dev", "x"));
        Assert.Equal("http://s/svn/fort_monorepo/branches/x", BranchRule.NewUrl("http://s/svn/fort_monorepo/trunk", "x"));
        Assert.Throws<SgException>(() => BranchRule.NewUrl("http://s/svn/odd/main", "x"));

        var overrides = new Dictionary<string, string> { ["http://s/svn/odd/main"] = "http://s/svn/odd/lines/{name}" };
        Assert.Equal("http://s/svn/odd/lines/x/sub", BranchRule.NewUrl("http://s/svn/odd/main/sub", "x", overrides));
    }

    [Fact]
    public void Externals_RewriteKeepsLayout()
    {
        var value = "# engine\n"
                    + "http://s/svn/engine/branches/fort/dev schmetterling\n"
                    + "-r148 http://s/svn/tools/trunk/dev tools\n"
                    + "http://s/svn/game/trunk/dev@77 \"my dev\"\n"
                    + "old-name http://s/svn/x/trunk\n";

        var got = Externals.Rewrite(value, u => BranchRule.NewUrl(u, "new"));

        Assert.Equal("# engine\n"
                     + "http://s/svn/engine/branches/new/dev schmetterling\n"
                     + "-r148 http://s/svn/tools/branches/new/dev tools\n"
                     + "http://s/svn/game/branches/new/dev@77 \"my dev\"\n"
                     + "old-name http://s/svn/x/branches/new\n", got);

        // A null answer keeps that line as it was, and the name beside the URL is how a caller picks it.
        var partly = Externals.Rewrite(value, (u, n) => n == "tools" ? null : BranchRule.NewUrl(u, "new"));
        Assert.Contains("-r148 http://s/svn/tools/trunk/dev tools\n", partly);
        Assert.Contains("http://s/svn/engine/branches/new/dev schmetterling\n", partly);

        var defs = Externals.Definitions(value);
        Assert.Equal(4, defs.Count);
        Assert.Equal(("http://s/svn/tools/trunk/dev", "tools"), defs[1]);
        Assert.Equal(("http://s/svn/game/trunk/dev", "my dev"), defs[2]);
        Assert.Equal(("http://s/svn/x/trunk", "old-name"), defs[3]);
    }
}

public sealed class ServerTests : IDisposable
{
    readonly Fixture f = new();

    public void Dispose() => f.Dispose();

    [Fact]
    public void ServerBranch_ThenCheckout_ThenPushIntoIt()
    {
        f.Setup();
        Fixture.Put(f.Checkout, "CMakeLists.txt", "project(fort)\n# local tweak\n");
        Fixture.Put(f.Checkout, "junk.txt", "unversioned");
        var engineBefore = f.Revision(f.EngineUrl);
        var gameBefore = f.Revision(f.GameUrl);

        var plan = Server.PlanBranch(f.Root, f.Co, "rel-1");

        Assert.Equal(3, plan.Repos.Count);
        Assert.EndsWith("/repos/engine", plan.Repos[0].ReposRoot);
        Assert.EndsWith("/repos/game", plan.Repos[1].ReposRoot);
        Assert.EndsWith("/repos/mono", plan.Repos[2].ReposRoot);
        Assert.Equal(f.MonoUrl + "/branches/rel-1", plan.NewRootUrl);
        Assert.Contains(plan.Repos, r => r.Copies.Any(c => c.Dst.EndsWith("/game/branches/rel-1/builds")));
        Assert.Contains(plan.Repos.SelectMany(r => r.Mkdirs), m => m.EndsWith("/game/branches/rel-1"));
        Assert.Equal(3, plan.Repos[1].Copies.Count);
        // the nested external dev/sub is inside the dev copy, so it is not copied twice
        Assert.Single(plan.Repos[0].Copies);
        Assert.EndsWith("/engine/branches/rel-1/dev", plan.Repos[0].Copies[0].Dst);
        Assert.DoesNotContain(plan.Repos[0].Mkdirs, m => m.EndsWith("/rel-1/dev"));

        Server.ExecuteBranch(f.Root, plan);

        Assert.All(plan.Repos, r => Assert.Equal("committed", r.State));
        Assert.Equal(engineBefore + 1, f.Revision(f.EngineUrl));
        Assert.Equal(gameBefore + 1, f.Revision(f.GameUrl));
        Assert.Contains("CMakeLists.txt", f.Ls(f.MonoUrl + "/branches/rel-1"));
        Assert.Equal("project(fort)", f.Cat(f.MonoUrl + "/branches/rel-1/CMakeLists.txt").Trim());
        Assert.Contains(f.EngineUrl + "/branches/rel-1/dev schmetterling", f.PropGet(f.MonoUrl + "/branches/rel-1", "svn:externals"));
        Assert.Contains(f.GameUrl + "/branches/rel-1/tools tools", f.PropGet(f.MonoUrl + "/branches/rel-1/fort", "svn:externals"));
        Assert.Equal("int engine = 1;", f.Cat(f.EngineUrl + "/branches/rel-1/dev/engine.cpp").Trim());

        var co2 = Server.Checkout(f.Root, f.Co, "rel-1").Checkout;

        Assert.Equal(Path.Combine(f.RootDir, "rel-1"), co2.Path);
        Assert.Equal(f.MonoUrl + "/branches/rel-1", f.Svn.Info(co2.Path, ".").Url);
        Assert.Equal(f.EngineUrl + "/branches/rel-1/dev", f.Svn.Info(co2.Path, "schmetterling").Url);
        Assert.Equal(f.GameUrl + "/branches/rel-1/tools", f.Svn.Info(co2.Path, "fort/tools").Url);
        Assert.Contains(f.EngineUrl + "/branches/rel-1/dev/sub data_engine", f.PropGet(f.GameUrl + "/branches/rel-1/builds", "svn:externals"));
        Assert.Equal(f.EngineUrl + "/branches/rel-1/dev/sub", f.Svn.Info(co2.Path, "fort/builds/data_engine").Url);
        Assert.Equal("project(fort)", File.ReadAllText(Path.Combine(co2.Path, "CMakeLists.txt")).Trim());
        Assert.False(File.Exists(Path.Combine(co2.Path, "junk.txt")));
        Assert.True(File.Exists(Path.Combine(co2.Path, ".git")));
        Assert.Empty(f.Svn.Status(co2.Path, noIgnore: false).Where(e => e.Path.Length > 0 && e.Item is not ("external" or "unversioned" or "ignored")));
        Assert.Contains("schmetterling/engine.cpp", f.Root.Git.LsTree(f.Root.SnapshotRef(co2), recursive: true).Select(e => e.Path));
        Assert.Equal(2, Ops.Status(f.Root, checkSvn: false).Checkouts.Count);
        Assert.Equal("int engine = 1;", File.ReadAllText(Path.Combine(f.Checkout, "schmetterling", "engine.cpp")).Trim());

        var wt = Ops.Branch(f.Root, "hotfix", co2).Path;
        Fixture.Put(wt, "schmetterling/engine.cpp", "int engine = 100; // hotfix\n");
        f.Root.Git.Ok(wt, "commit", "-q", "-am", "hotfix");
        var r = Push.Run(f.Root, wt, "hotfix on the release branch", interactive: true);

        Assert.True(r.AllCommitted, string.Join("\n", r.Groups.Select(g => $"{g.Wc} {g.State} {g.Error}")));
        Assert.Equal("int engine = 100; // hotfix", f.Cat(f.EngineUrl + "/branches/rel-1/dev/engine.cpp").Trim());
        Assert.Equal("int engine = 1;", f.Cat(f.EngineUrl + "/branches/fort/dev/engine.cpp").Trim());
    }

    [Fact]
    public void ServerBranch_KeepsAnExternal_AndNamesAnotherApart()
    {
        f.Setup();
        var parts = Server.Parts(f.Root, f.Co);

        Assert.Equal("", parts[0].Wc);
        Assert.Equal(f.MonoUrl + "/trunk", parts[0].Url);
        Assert.Contains(parts, p => p.Wc == "fort/builds" && p.Url == f.GameUrl + "/branches/fort/builds");
        Assert.Contains(parts, p => p.Wc == "schmetterling");

        var root = Assert.Throws<SgException>(() => Server.PlanBranch(f.Root, f.Co, "rel-1", null, [new BranchPart { Keep = true }]));
        Assert.Contains("root", root.Message);

        parts.Single(p => p.Wc == "fort/builds").Keep = true;
        parts.Single(p => p.Wc == "schmetterling").Branch = "rel-1-engine";
        var plan = Server.PlanBranch(f.Root, f.Co, "rel-1", null, parts);

        // builds stays, and the external nested inside it goes with it
        Assert.Contains(plan.Kept, k => k.Wc == "fort/builds" && k.Url == f.GameUrl + "/branches/fort/builds");
        Assert.Contains(plan.Kept, k => k.Wc == "fort/builds/data_engine");
        Assert.Contains("kept  fort/builds stays at", plan.Describe());
        var engine = plan.Repos.Single(r => r.ReposRoot.EndsWith("/repos/engine"));
        Assert.Single(engine.Copies);
        Assert.EndsWith("/engine/branches/rel-1-engine/dev", engine.Copies[0].Dst);
        var game = plan.Repos.Single(r => r.ReposRoot.EndsWith("/repos/game"));
        Assert.Equal(2, game.Copies.Count);
        Assert.DoesNotContain(game.Copies, c => c.Dst.EndsWith("/builds"));
        // the property on builds is not touched: builds is not copied
        Assert.Empty(game.PropSets);
        var mono = plan.Repos.Single(r => r.ReposRoot.EndsWith("/repos/mono"));
        var fortProp = mono.PropSets.Single(p => p.Url.EndsWith("/branches/rel-1/fort")).Value;
        Assert.Contains(f.GameUrl + "/branches/fort/builds builds", fortProp);
        Assert.Contains(f.GameUrl + "/branches/rel-1/tools tools", fortProp);
        Assert.Contains(f.EngineUrl + "/branches/rel-1-engine/dev schmetterling", mono.PropSets.Single(p => p.Url.EndsWith("/branches/rel-1")).Value);

        Server.ExecuteBranch(f.Root, plan);
        var co2 = Server.Checkout(f.Root, f.Co, "rel-1").Checkout;

        Assert.All(plan.Repos, r => Assert.Equal("committed", r.State));
        Assert.False(f.Svn.UrlExists(f.GameUrl + "/branches/rel-1/builds"));
        Assert.Equal(f.GameUrl + "/branches/fort/builds", f.Svn.Info(co2.Path, "fort/builds").Url);
        Assert.Equal(f.GameUrl + "/branches/rel-1/tools", f.Svn.Info(co2.Path, "fort/tools").Url);
        Assert.Equal(f.EngineUrl + "/branches/rel-1-engine/dev", f.Svn.Info(co2.Path, "schmetterling").Url);
    }

    [Fact]
    public void ServerBranch_RefusesAnExistingBranch()
    {
        f.Setup();
        var ex = Assert.Throws<SgException>(() => Server.PlanBranch(f.Root, f.Co, "fort"));
        Assert.Contains("already on the server", ex.Message);
    }

    [Fact]
    public void CheckoutFromUrl_ChecksOutAndRegisters_InTheRootFolder()
    {
        f.Setup();
        var r = Ops.CheckoutFromUrl(f.Root, f.MonoUrl + "/trunk", name: "trunk2", skip: ["fort/builds"]);
        Assert.Equal("trunk2", r.Checkout.Name);
        Assert.Equal(Path.Combine(f.RootDir, "trunk2"), r.Checkout.Path);
        Assert.True(File.Exists(Path.Combine(r.Checkout.Path, "CMakeLists.txt")));
        Assert.True(File.Exists(Path.Combine(r.Checkout.Path, "schmetterling", "engine.cpp")));
        Assert.True(File.Exists(Path.Combine(r.Checkout.Path, ".git")));
        Assert.True(r.Snapshot.Revision > 0);
        Assert.Contains(f.Root.Config.Checkouts, c => c.Name == "trunk2");

        var again = Assert.Throws<SgException>(() => Ops.CheckoutFromUrl(f.Root, f.MonoUrl + "/trunk", name: "trunk2"));
        Assert.Contains("not empty", again.Message);
        var missing = Assert.Throws<SgException>(() => Ops.CheckoutFromUrl(f.Root, f.MonoUrl + "/nowhere", name: "x"));
        Assert.Contains("not on the server", missing.Message);
    }
}
