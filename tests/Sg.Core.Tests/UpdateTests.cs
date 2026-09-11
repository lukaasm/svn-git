using Xunit;

namespace Sg.Core.Tests;

public sealed class UpdateTests
{
    [Fact]
    public void ParseNotes_ReadsTheBuildJsonTheWorkflowWrote()
    {
        var notes = """
            {
              "runId": 17654321,
              "runNumber": 42,
              "commit": "e6f4b77abcdef0123456789",
              "ref": "main",
              "builtUtc": "2026-09-03T19:20:00Z"
            }
            """;
        var stamp = Updater.ParseNotes(notes);
        Assert.NotNull(stamp);
        Assert.Equal(17654321, stamp.RunId);
        Assert.Equal(42, stamp.RunNumber);
        Assert.Equal("e6f4b77", stamp.ShortCommit);
        Assert.Equal("main", stamp.Ref);
    }

    [Fact]
    public void ParseNotes_SurvivesTextAroundTheJsonAndRubbish()
    {
        Assert.Equal(7, Updater.ParseNotes("build 7\n\n{\"runId\":1,\"runNumber\":7}\n")!.RunNumber);
        Assert.Null(Updater.ParseNotes(null));
        Assert.Null(Updater.ParseNotes(""));
        Assert.Null(Updater.ParseNotes("no json here"));
        Assert.Null(Updater.ParseNotes("{ not json }"));
    }

    [Fact]
    public void Newer_ComparesRunIds_AndAnUnknownLocalBuildIsAlwaysOlder()
    {
        var remote = new BuildStamp { RunId = 100, RunNumber = 10 };
        Assert.True(Check(null, remote).Newer);
        Assert.True(Check(new BuildStamp { RunId = 99 }, remote).Newer);
        Assert.False(Check(new BuildStamp { RunId = 100 }, remote).Newer);
        Assert.False(Check(new BuildStamp { RunId = 101 }, remote).Newer);
    }

    static UpdateCheck Check(BuildStamp? local, BuildStamp remote) =>
        new(local, remote, "https://example.invalid/sg-win-x64.zip", false, 0);
}

public sealed class DiskUsageTests
{
    [Fact]
    public void Measure_AddsUpFilesAndNeverFollowsAJunction()
    {
        var root = Path.Combine(Path.GetTempPath(), "sgdu-" + Guid.NewGuid().ToString("N")[..8]);
        var real = Path.Combine(root, "worktree");
        var elsewhere = Path.Combine(root, "checkout", "builds");
        Directory.CreateDirectory(Path.Combine(real, "sub"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            File.WriteAllBytes(Path.Combine(real, "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(real, "sub", "b.bin"), new byte[2000]);
            // 8 MB that belongs to the checkout, reached through a junction like fort/builds.
            File.WriteAllBytes(Path.Combine(elsewhere, "big.bin"), new byte[8 * 1024 * 1024]);
            Directory.CreateSymbolicLink(Path.Combine(real, "builds"), elsewhere);

            var size = DiskUsage.Measure(real);

            Assert.Equal(3000, size.Bytes);
            Assert.Equal(2, size.Files);
            Assert.False(size.Partial);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Measure_LeavesOutTheFoldersItIsToldTo()
    {
        var root = Path.Combine(Path.GetTempPath(), "sgdu-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "fort", "builds", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "src", "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(root, "fort", "builds", "b.bin"), new byte[4000]);
            File.WriteAllBytes(Path.Combine(root, "fort", "builds", "deep", "c.bin"), new byte[8000]);

            Assert.Equal(13000, DiskUsage.Measure(root).Bytes);
            // A cloned fort/builds reports its full size while sharing every block, so it is left out.
            var size = DiskUsage.Measure(root, ["fort/builds"]);
            Assert.Equal(1000, size.Bytes);
            Assert.Equal(1, size.Files);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Human_ReadsAtTheScaleAWorktreeActuallyIs()
    {
        Assert.Equal("4.0 GB", DiskUsage.Human(4L * 1024 * 1024 * 1024));
        Assert.Equal("69 MB", DiskUsage.Human(69L * 1024 * 1024));
        Assert.Equal("512 KB", DiskUsage.Human(512 * 1024));
    }
}
