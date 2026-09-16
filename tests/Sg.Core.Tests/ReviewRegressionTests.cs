namespace Sg.Core.Tests;

public sealed class ReviewRegressionTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sg-review-" + Guid.NewGuid().ToString("N"));
    public ReviewRegressionTests() => Directory.CreateDirectory(dir);
    public void Dispose() => SgRoot.SweepTempDir(dir);

    [Fact]
    public void RootLock_ExcludesOtherThreadsButAllowsNestedOwner()
    {
        var root = SgRoot.Create(dir, new SgConfig(), new NullLog());
        using (root.Lock())
        {
            using (root.Lock(0)) { }
            Exception? failure = null;
            var other = new Thread(() => failure = Record.Exception(() => { using var held = root.Lock(0); }));
            other.Start();
            Assert.True(other.Join(TimeSpan.FromSeconds(5)));
            Assert.IsType<SgException>(failure);
        }
        using var final = root.Lock(0);
    }

    [Fact]
    public void Mirror_UnavailableSourcePreservesDestination()
    {
        var target = Path.Combine(dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Assert.ThrowsAny<Exception>(() => SharedFolders.Mirror(new NullLog(), "test", Path.Combine(dir, "missing"), target, false));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void Mirror_UnreadableReplacementPreservesOldFiles()
    {
        var source = Path.Combine(dir, "source");
        var target = Path.Combine(dir, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "changed.txt"), "replacement contents");
        File.WriteAllText(Path.Combine(target, "changed.txt"), "old");
        File.WriteAllText(Path.Combine(target, "removed.txt"), "keep until copying succeeds");
        using var held = new FileStream(Path.Combine(source, "changed.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<Exception>(() => SharedFolders.Mirror(new NullLog(), "test", source, target, false));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "changed.txt")));
        Assert.True(File.Exists(Path.Combine(target, "removed.txt")));
    }

    [Fact]
    public void Update_RecoversInterruptedInstallationOnNextSweep()
    {
        var saved = Path.Combine(dir, ".sg-update-recovery", "files");
        Directory.CreateDirectory(saved);
        File.WriteAllText(Path.Combine(saved, "sg.exe"), "old version");
        File.WriteAllText(Path.Combine(dir, "sg.exe"), "partial version");
        AtomicFile.WriteAllText(Path.Combine(dir, ".sg-update-recovery", "journal.json"), "{\"sg.exe\":true}");
        Updater.Sweep(dir);
        Assert.Equal("old version", File.ReadAllText(Path.Combine(dir, "sg.exe")));
        Updater.Sweep(dir);
        Assert.Equal("old version", File.ReadAllText(Path.Combine(dir, "sg.exe")));
    }

    [Fact]
    public void RelativeTo_RejectsOutsideButAcceptsDottedFilename()
    {
        Assert.Equal("..notes", PathUtil.RelativeTo(dir, Path.Combine(dir, "..notes")));
        Assert.Throws<SgException>(() => PathUtil.RelativeTo(dir, Path.GetDirectoryName(dir)!));
        if (OperatingSystem.IsWindows())
            Assert.Throws<SgException>(() => PathUtil.RelativeTo(@"C:\repo", @"D:\outside.txt"));
    }

    [Fact]
    public void AtomicWrite_FailedPublishLeavesExistingTarget()
    {
        var target = Path.Combine(dir, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(target, "replacement"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Update_FailureRestoresFilesAlreadyReplaced()
    {
        var payload = Path.Combine(dir, "payload");
        var install = Path.Combine(dir, "install");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(payload, "a.exe"), "new");
        File.WriteAllText(Path.Combine(payload, "blocked"), "new file");
        File.WriteAllText(Path.Combine(install, "a.exe"), "original");
        Directory.CreateDirectory(Path.Combine(install, "blocked"));
        Assert.ThrowsAny<IOException>(() => Updater.InstallPayload(payload, install));
        Assert.Equal("original", File.ReadAllText(Path.Combine(install, "a.exe")));
        Assert.False(File.Exists(Path.Combine(install, ".sg-update-recovery", "journal.json")));
    }
}
