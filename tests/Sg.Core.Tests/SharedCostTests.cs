using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// What a worktree's shared folders really cost. A junction is the checkout's own folder and a ReFS
/// block clone shares its blocks until one side writes, so neither is bytes this branch spends. A full
/// copy is, once per worktree, and the overview says so rather than only adding it to a total.
/// </summary>
public sealed class SharedCostTests : IDisposable
{
    readonly string _dir;

    public SharedCostTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sgsc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { DeleteTree(_dir); }
        catch (IOException) { /* the test still said what it had to say */ }
    }

    /// <summary>
    /// Recursive delete that does not follow a junction. Directory.Delete(recursive) walks into one and
    /// then refuses it, so a test that made a junction could not clean up after itself.
    /// </summary>
    static void DeleteTree(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists) return;
        // The link goes; what it points at is not this folder's to delete.
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { info.Delete(); return; }
        foreach (var sub in info.EnumerateDirectories()) DeleteTree(sub.FullName);
        foreach (var f in info.EnumerateFiles()) { f.Attributes = FileAttributes.Normal; f.Delete(); }
        info.Delete();
    }

    /// <summary>A folder of one file of exactly this many bytes, so the walk's answer can be asserted.</summary>
    string Folder(string rel, int bytes)
    {
        var path = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "big.bin"), new byte[bytes]);
        return path;
    }

    SharedCost One(string rel, string mode) => DiskUsage.MeasureShared(_dir, [rel], mode).Single();

    [Fact]
    public void AFolderThatIsNotThere_IsMissing_AndCostsNothing()
    {
        var cost = One("libs/prebuilt", "junction");

        Assert.True(cost.Missing);
        Assert.False(cost.Copied);
        Assert.Equal(0, cost.Own);
    }

    [Fact]
    public void AFullCopy_IsCopied_AndCostsItsBytes()
    {
        Folder("libs/prebuilt", 4096);

        var cost = One("libs/prebuilt", "copy");

        Assert.True(cost.Copied);
        Assert.False(cost.Linked);
        Assert.False(cost.Shared);
        Assert.Equal(4096, cost.Bytes);
        Assert.Equal(4096, cost.Own);
    }

    /// <summary>
    /// A clone looks exactly like a copy on disk, so the mode the branch recorded is the only witness.
    /// Walking it would report the checkout's bytes as this branch's, which is the number that was wrong.
    /// </summary>
    [Fact]
    public void ACloneLooksLikeACopy_ButTheModeSaysItsBytesAreTheCheckouts()
    {
        Folder("libs/prebuilt", 4096);

        var cost = One("libs/prebuilt", "clone");

        Assert.True(cost.Shared);
        Assert.False(cost.Copied);
        Assert.Equal(0, cost.Own);
    }

    /// <summary>
    /// A worktree made before the sharing choice existed records no mode. It is read as a copy: the
    /// answer that costs nothing to hear and something to ignore.
    /// </summary>
    [Fact]
    public void AWorktreeWithNoRecordedMode_IsReadAsACopy()
    {
        Folder("libs/prebuilt", 2048);

        var cost = One("libs/prebuilt", "");

        Assert.True(cost.Copied);
        Assert.Equal(2048, cost.Own);
    }

    /// <summary>
    /// The junction is spotted on disk, not taken from the mode. Populate skips a folder that is already
    /// there and only warns, so a worktree whose mode says junction can be holding a real copy - and that
    /// is exactly the one worth warning about.
    /// </summary>
    [Fact]
    public void ModeSaysJunction_ButTheFolderIsReal_SoItIsACopy()
    {
        Folder("libs/prebuilt", 4096);

        var cost = One("libs/prebuilt", "junction");

        Assert.True(cost.Copied);
        Assert.Equal(4096, cost.Own);
    }

    [Fact]
    public void ARealJunction_IsLinked_AndCostsNothing()
    {
        var source = Folder("source/prebuilt", 4096);
        var target = Path.Combine(_dir, "libs", "prebuilt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var made = Proc.Run("cmd", ["/c", "mklink", "/J", target, source], _dir, new CollectingLog());
        Assert.True(made.Ok, made.StdErr);

        var cost = One("libs/prebuilt", "copy");

        Assert.True(cost.Linked);
        Assert.False(cost.Copied);
        Assert.Equal(0, cost.Own);
    }

    /// <summary>
    /// Every shared folder gets an entry, in the order they were asked for, so the badge can name the
    /// ones that are copies and leave out the ones that are not.
    /// </summary>
    [Fact]
    public void EveryFolderGetsAnEntry_AndOnlyTheCopiesCount()
    {
        Folder("libs/prebuilt", 4096);
        Folder("libs/tools", 1024);

        var costs = DiskUsage.MeasureShared(_dir, ["libs/prebuilt", "libs/tools", "libs/gone"], "copy");

        Assert.Equal(3, costs.Count);
        Assert.Equal(["libs/prebuilt", "libs/tools", "libs/gone"], costs.Select(c => c.Rel));
        Assert.Equal(5120, costs.Sum(c => c.Own));
        Assert.Equal(["libs/prebuilt", "libs/tools"], costs.Where(c => c.Copied).Select(c => c.Rel));
    }

    /// <summary>The walk of a shared folder does not follow a junction inside it, the way the worktree walk does not.</summary>
    [Fact]
    public void AJunctionInsideASharedFolder_IsNotWalked()
    {
        Folder("libs/prebuilt", 4096);
        var elsewhere = Folder("elsewhere", 8192);
        var inside = Path.Combine(_dir, "libs", "prebuilt", "linked");
        var made = Proc.Run("cmd", ["/c", "mklink", "/J", inside, elsewhere], _dir, new CollectingLog());
        Assert.True(made.Ok, made.StdErr);

        var cost = One("libs/prebuilt", "copy");

        Assert.Equal(4096, cost.Bytes);
    }
}
