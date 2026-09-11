using System.Text.Json;
using Xunit;

namespace Sg.Core.Tests;

public sealed class SharedFoldersTests
{
    [Fact]
    public void Parse_TakesTheThreeWords_AndRefsForClone()
    {
        Assert.Equal(SharedMode.Junction, SharedFolders.Parse("junction"));
        Assert.Equal(SharedMode.Clone, SharedFolders.Parse("Clone"));
        Assert.Equal(SharedMode.Clone, SharedFolders.Parse("refs"));
        Assert.Equal(SharedMode.Copy, SharedFolders.Parse(" copy "));
        Assert.Throws<SgException>(() => SharedFolders.Parse("symlink"));
    }

    [Fact]
    public void Config_WritesSharedAsAWord_AndReadsOldFilesAsJunction()
    {
        var cfg = new SgConfig { Checkouts = { new CheckoutConfig { Name = "a", Shared = SharedMode.Clone } } };
        var json = JsonSerializer.Serialize(cfg, SgConfig.JsonOptions);
        Assert.Contains("\"shared\": \"clone\"", json);
        Assert.Equal(SharedMode.Clone, JsonSerializer.Deserialize<SgConfig>(json, SgConfig.JsonOptions)!.Checkouts[0].Shared);

        // A config from before the choice existed says nothing, and means what it always did.
        var old = JsonSerializer.Deserialize<SgConfig>("""{"checkouts":[{"name":"a","junctions":["fort/builds"]}]}""", SgConfig.JsonOptions)!;
        Assert.Equal(SharedMode.Junction, old.Checkouts[0].Shared);
        Assert.Equal(["fort/builds"], old.Checkouts[0].Junctions);
    }

    [Fact]
    public void CloneProblem_NamesWhatIsInTheWay()
    {
        var temp = Path.GetTempPath();
        var volume = SharedFolders.VolumeOf(temp);
        Assert.NotNull(volume);
        var fs = SharedFolders.FileSystemOf(volume);
        Assert.NotEqual("", fs);

        if (!fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
        {
            var problem = SharedFolders.CloneProblem(temp, Path.Combine(temp, "not-there-yet", "either"));
            Assert.NotNull(problem);
            Assert.Contains("not ReFS", problem);
            Assert.Contains(volume, problem);
        }

        var refs = ReFsVolume();
        if (refs != null && !refs.Equals(volume, StringComparison.OrdinalIgnoreCase))
        {
            var problem = SharedFolders.CloneProblem(temp, refs);
            Assert.NotNull(problem);
            Assert.Contains("one ReFS volume", problem);
            Assert.Null(SharedFolders.CloneProblem(refs, Path.Combine(refs, "not-there-yet")));
        }
    }

    [Fact]
    public void CloneFile_GivesTheSameBytes_AndAWriteToTheCloneLeavesTheSourceAlone()
    {
        var volume = ReFsVolume();
        if (volume == null) return;   // no ReFS volume on this machine, nothing to clone on
        var dir = Path.Combine(volume, "sg-clone-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var cluster = SharedFolders.ClusterSizeOf(volume);
            Assert.True(cluster >= 512);

            // Not a multiple of the cluster size on purpose: the last partial cluster is the odd one.
            var bytes = new byte[3 * 1024 * 1024 + 123];
            new Random(7).NextBytes(bytes);
            var src = Path.Combine(dir, "a.bin");
            var dst = Path.Combine(dir, "b.bin");
            File.WriteAllBytes(src, bytes);
            File.SetLastWriteTimeUtc(src, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            File.SetAttributes(src, FileAttributes.ReadOnly);

            SharedFolders.CloneFile(src, dst, cluster);

            Assert.Equal(bytes, File.ReadAllBytes(dst));
            Assert.Equal(File.GetLastWriteTimeUtc(src), File.GetLastWriteTimeUtc(dst));
            Assert.True(File.GetAttributes(dst).HasFlag(FileAttributes.ReadOnly));

            File.SetAttributes(dst, FileAttributes.Normal);
            using (var w = new FileStream(dst, FileMode.Open, FileAccess.Write)) w.Write(new byte[64]);
            Assert.Equal(bytes, File.ReadAllBytes(src));
            Assert.Equal(new byte[64], File.ReadAllBytes(dst)[..64]);

            var empty = Path.Combine(dir, "empty.bin");
            File.WriteAllBytes(empty, []);
            SharedFolders.CloneFile(empty, Path.Combine(dir, "empty2.bin"), cluster);
            Assert.Equal(0, new FileInfo(Path.Combine(dir, "empty2.bin")).Length);
        }
        finally
        {
            foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Mirror_BringsNewAndChangedFilesOver_AndDropsWhatIsGone()
    {
        var root = Path.Combine(Path.GetTempPath(), "sg-mirror-" + Guid.NewGuid().ToString("N")[..8]);
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        Directory.CreateDirectory(Path.Combine(src, "old"));
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");
            File.WriteAllText(Path.Combine(src, "old", "c.txt"), "c");
            Assert.Equal(3, SharedFolders.CopyTree(new NullLog(), "x", src, dst, clone: false));
            Assert.False(SharedFolders.Mirror(new NullLog(), "x", src, dst, clone: false).Changed);

            // The source moves on: one file changes, one arrives, one folder goes.
            File.WriteAllText(Path.Combine(src, "a.txt"), "a2");
            Directory.CreateDirectory(Path.Combine(src, "new"));
            File.WriteAllText(Path.Combine(src, "new", "d.txt"), "d");
            Directory.Delete(Path.Combine(src, "old"), recursive: true);
            // The target holds things of its own: a local file, and a .svn from before the rule.
            File.WriteAllText(Path.Combine(dst, "mine.txt"), "mine");
            Directory.CreateDirectory(Path.Combine(dst, ".svn"));
            File.WriteAllText(Path.Combine(dst, ".svn", "wc.db"), "db");
            File.SetAttributes(Path.Combine(dst, ".svn", "wc.db"), FileAttributes.ReadOnly);

            var m = SharedFolders.Mirror(new NullLog(), "x", src, dst, clone: false);

            Assert.Equal(1, m.Added);
            Assert.Equal(1, m.Replaced);
            Assert.Equal(5, m.Removed);   // mine.txt, old/c.txt, .svn/wc.db, and the folders old and .svn
            Assert.Equal("a2", File.ReadAllText(Path.Combine(dst, "a.txt")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(dst, "sub", "b.txt")));
            Assert.Equal("d", File.ReadAllText(Path.Combine(dst, "new", "d.txt")));
            Assert.False(File.Exists(Path.Combine(dst, "mine.txt")));
            Assert.False(Directory.Exists(Path.Combine(dst, "old")));
            Assert.False(Directory.Exists(Path.Combine(dst, ".svn")));
            Assert.False(SharedFolders.Mirror(new NullLog(), "x", src, dst, clone: false).Changed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void CopyTree_TakesHiddenFiles_LeavesSvnOut_AndDoesNotFollowAJunction()
    {
        var root = Path.Combine(Path.GetTempPath(), "sg-tree-" + Guid.NewGuid().ToString("N")[..8]);
        var src = Path.Combine(root, "src");
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "b");
            File.WriteAllText(Path.Combine(src, ".hidden"), "h");
            File.SetAttributes(Path.Combine(src, ".hidden"), FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(elsewhere, "far.txt"), "far");
            Proc.Run("cmd", ["/c", "mklink", "/J", Path.Combine(src, "link"), elsewhere], null, new NullLog()).EnsureOk();
            Directory.CreateDirectory(Path.Combine(src, "sub", ".svn"));
            File.WriteAllText(Path.Combine(src, "sub", ".svn", "wc.db"), "db");

            var dst = Path.Combine(root, "dst");
            Assert.Equal(3, SharedFolders.CopyTree(new NullLog(), "src", src, dst, clone: false));

            Assert.Equal("a", File.ReadAllText(Path.Combine(dst, "a.txt")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(dst, "sub", "b.txt")));
            Assert.Equal("h", File.ReadAllText(Path.Combine(dst, ".hidden")));
            Assert.False(Directory.Exists(Path.Combine(dst, "link")));
            Assert.False(Directory.Exists(Path.Combine(dst, "sub", ".svn")));
        }
        finally
        {
            try { Directory.Delete(Path.Combine(src, "link")); } catch (IOException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    static string? ReFsVolume()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveFormat.Equals("ReFS", StringComparison.OrdinalIgnoreCase)) return d.RootDirectory.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a drive with no media */ }
        }
        return null;
    }
}
