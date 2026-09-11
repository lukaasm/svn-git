namespace Sg.Core.Tests;

/// <summary>
/// A branch leaves one machine as one file and arrives on another that has nothing in common with it
/// but the SVN repository. The far side builds its own snapshot, which is its own commit with its own
/// sha, so nothing here may lean on a sha from the near side: the base is named by revision, the
/// commits travel as patches, and the versions they start from travel with them.
/// </summary>
public sealed class ExportTests : IDisposable
{
    readonly Fixture f = new();

    public void Dispose() => f.Dispose();

    /// <summary>The remote PC: its own root, its own checkout of the same repository, its own snapshot.</summary>
    (SgRoot Root, CheckoutConfig Co) Far(string name = "far", string? url = null)
    {
        var dir = Path.Combine(f.Base, name);
        Directory.CreateDirectory(dir);
        var wc = Path.Combine(dir, "mono");
        f.Svn.Ok(null, "checkout", "--non-interactive", url ?? f.MonoUrl + "/trunk", wc);
        var root = Ops.Init(dir, f.Log, fsmonitor: false);
        var co = Ops.CheckoutAdd(root, wc, skip: ["fort/builds"], junctions: ["fort/builds"], optional: ["fort/tools"]).Checkout;
        return (root, co);
    }

    /// <summary>Two commits on a branch here: one that edits and adds, one that renames and deletes.</summary>
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

    string File_() => Path.Combine(f.Base, "carried" + Export.Extension);

    [Fact]
    public void Export_ThenImportOnAnotherRoot_RebuildsTheBranch()
    {
        f.Setup();
        var wt = MakeBranch("feature-x");
        var file = File_();

        var written = Export.Write(f.Root, wt, file);
        Assert.Equal(2, written.Commits);
        Assert.Equal("feature-x", written.Branch);
        Assert.True(written.Bytes > 0);
        Assert.Equal(0, written.Uncommitted);

        var far = Far();
        // The two snapshots agree on every byte and on nothing else: same tree, different commit.
        Assert.NotEqual(f.Root.Git.RefSha(f.Root.SnapshotRef(f.Co)), far.Root.Git.RefSha(far.Root.SnapshotRef(far.Co)));

        var meta = Export.Read(file);
        Assert.Equal(ExportMeta.Current, meta.Version);
        Assert.Equal(["[gui] first: edit one, add one", "second: rename one, delete one"], meta.Subjects);
        Assert.Equal(f.MonoUrl + "/trunk", meta.Root!.Url);

        var r = Export.Import(far.Root, file);
        Assert.True(r.Ok, r.Why);
        Assert.Equal(2, r.Applied);
        Assert.Empty(r.Drift);
        Assert.Equal("feature-x", r.Branch);

        // Every change is there, including the rename and the delete.
        Assert.Equal("int engine = 2;\n", Read(r.Path, "schmetterling/engine.cpp"));
        Assert.Equal("brand new\n", Read(r.Path, "fort/dev/new/file.txt"));
        Assert.True(File.Exists(Path.Combine(r.Path, "fort", "dev", "game_renamed.cpp")));
        Assert.False(File.Exists(Path.Combine(r.Path, "fort", "dev", "game.cpp")));
        Assert.False(File.Exists(Path.Combine(r.Path, "fort", "tools", "tool.py")));

        // And so is what the commits said, including a subject that starts with a bracket.
        var log = far.Root.Git.Log(r.Path, far.Root.SnapshotRef(far.Co) + "..refs/heads/feature-x", 10);
        Assert.Equal(["second: rename one, delete one", "[gui] first: edit one, add one"], log.Select(c => c.Subject).ToArray());
        Assert.Equal("Ada Lovelace", far.Root.Git.Details(log[1].Sha).Author);
    }

    [Fact]
    public void Import_OntoACheckoutThatMovedOn_MergesAndNamesEveryRevisionThatDiffers()
    {
        f.Setup();
        var wt = MakeBranch("feature-drift");
        var file = File_();
        Export.Write(f.Root, wt, file);

        // Somebody else commits to the engine, and only the far side has synced it.
        var other = f.OtherWc(f.EngineUrl + "/branches/fort/dev");
        Fixture.Put(other, "sub/data.txt", "engine data\nand a line from somebody else\n");
        f.Svn.Ok(other, "commit", "--non-interactive", "-m", "a line the export never saw");

        var far = Far();
        Ops.Sync(far.Root, far.Co);

        var r = Export.Import(far.Root, file);
        Assert.True(r.Ok, r.Why + "\n" + string.Join("\n", r.Conflicted));
        Assert.Equal(2, r.Applied);

        // The drift is named, working copy by working copy, rather than left for the reader to work out.
        Assert.NotEmpty(r.Drift);
        Assert.Contains(r.Drift, d => d.Where == "schmetterling" && d.Local > d.Exported);

        // Both sides survived: the branch's edit and the line it never saw.
        Assert.Equal("int engine = 2;\n", Read(r.Path, "schmetterling/engine.cpp"));
        Assert.Contains("somebody else", Read(r.Path, "schmetterling/sub/data.txt"));
    }

    [Fact]
    public void Import_UnderAnotherName_AndTwiceIsRefused()
    {
        f.Setup();
        var wt = MakeBranch("feature-name");
        var file = File_();
        Export.Write(f.Root, wt, file);

        var far = Far();
        var r = Export.Import(far.Root, file, asBranch: "carried-over");
        Assert.True(r.Ok, r.Why);
        Assert.Equal("carried-over", r.Branch);
        Assert.NotNull(far.Root.Git.RefSha("refs/heads/carried-over"));

        var again = Assert.Throws<SgException>(() => Export.Import(far.Root, file, asBranch: "carried-over"));
        Assert.Contains("branch exists", again.Message);
    }

    [Fact]
    public void Import_WhereAnExternalPointsAtAnotherBranch_SaysSoRatherThanComparingTwoHistories()
    {
        f.Setup();
        var wt = MakeBranch("feature-elsewhere");
        var file = File_();
        Export.Write(f.Root, wt, file);
        var meta = Export.Read(file);
        Assert.Equal(f.EngineUrl + "/branches/fort/dev", meta.Bases.First(b => b.Where == "schmetterling").Url);

        // Another branch of the monorepo, whose svn:externals points the engine at trunk instead. This is
        // the shape that matters: two checkouts of one repository whose externals do not agree.
        var trunkDev = f.EngineUrl + "/trunk/dev";
        var otherUrl = f.MonoUrl + "/branches/other";
        f.Svn.Ok(null, "copy", "--parents", "--non-interactive", "-m", "another branch", f.MonoUrl + "/trunk", otherUrl);
        var edit = f.OtherWc(otherUrl);
        var propFile = Path.Combine(f.Base, "ext.txt");
        File.WriteAllText(propFile, trunkDev + " schmetterling\n");
        f.Svn.Ok(edit, "propset", "svn:externals", "-F", propFile, ".");
        f.Svn.Ok(edit, "commit", "--non-interactive", "-m", "point the engine at trunk here");

        var far = Far("far-other", otherUrl);
        var drift = Export.DriftOf(far.Root, meta, far.Co);
        var engine = drift.FirstOrDefault(d => d.Where == "schmetterling");
        Assert.True(engine != null, "drift: " + string.Join(" | ", drift.Select(d => d.ToString())));

        // Two revision numbers out of two histories say nothing, so the URL is what is reported.
        Assert.True(engine!.Elsewhere);
        Assert.Equal(trunkDev, engine.LocalUrl);
        Assert.Contains("here it is", engine.ToString());
    }

    [Fact]
    public void Export_OfABranchWithNothingOnIt_SaysSoRatherThanWritingAnEmptyFile()
    {
        f.Setup();
        var wt = Ops.Branch(f.Root, "feature-empty", f.Co).Path;
        var file = File_();
        var ex = Assert.Throws<SgException>(() => Export.Write(f.Root, wt, file));
        Assert.Contains("equals its snapshot", ex.Message);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Read_RefusesAVersionItDoesNotKnow_AndAFileThatIsNotAnExport()
    {
        f.Setup();
        var wt = MakeBranch("feature-version");
        var file = File_();
        Export.Write(f.Root, wt, file);

        // Rewrite the metadata as if a newer sg had written it. Half an import is worse than none.
        using (var zip = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Update))
        {
            var e = zip.GetEntry("export.json")!;
            string json;
            using (var r = new StreamReader(e.Open())) json = r.ReadToEnd();
            e.Delete();
            var fresh = zip.CreateEntry("export.json");
            using var w = new StreamWriter(fresh.Open());
            w.Write(json.Replace("\"version\": 1", "\"version\": 99"));
        }
        var ex = Assert.Throws<SgException>(() => Export.Read(file));
        Assert.Contains("version 99", ex.Message);

        var notAnExport = Path.Combine(f.Base, "notes.txt");
        File.WriteAllText(notAnExport, "just some text");
        Assert.Throws<SgException>(() => Export.Read(notAnExport));
    }
}
