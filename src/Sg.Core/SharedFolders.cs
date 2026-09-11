using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sg.Core;

/// <summary>
/// The folders a worktree shares with its checkout, like libs/prebuilt: tens of gigabytes that git never stores.
/// A junction was the only way in, and every worktree looked into the same folder, so a build in
/// one changed it for all. On ReFS a copy can share its blocks with the original until one side
/// writes, and each worktree gets a folder of its own for nothing. On NTFS a full copy is the only
/// private one, and it costs the folder's size again.
///
/// A private folder is not an SVN working copy: .svn is left out, which is most of the bytes, and
/// sg never runs svn inside a worktree. It follows the checkout by being mirrored from it on every
/// rebase, which is the moment the code moves too.
/// </summary>
public static class SharedFolders
{
    public static SharedMode Parse(string s) => s.Trim().ToLowerInvariant() switch
    {
        "junction" or "link" => SharedMode.Junction,
        "clone" or "refs" => SharedMode.Clone,
        "copy" => SharedMode.Copy,
        _ => throw new SgException("shared mode is junction, clone or copy: " + s),
    };

    public static string Name(SharedMode m) => m switch
    {
        SharedMode.Clone => "clone",
        SharedMode.Copy => "copy",
        _ => "junction",
    };

    /// <summary>The one-line answer for a result line: "junctions into the checkout", and so on.</summary>
    public static string Describe(SharedMode m) => m switch
    {
        SharedMode.Clone => "cloned from the checkout",
        SharedMode.Copy => "copied from the checkout",
        _ => "junctions into the checkout",
    };

    // ---- detection ----

    /// <summary>
    /// Why a block clone cannot be made from the checkout into a worktree under worktreeRoot. Null
    /// means it can. Both folders must sit on one volume, and that volume must be ReFS.
    /// </summary>
    public static string? CloneProblem(string checkoutPath, string worktreeRoot)
    {
        if (!OperatingSystem.IsWindows()) return "block cloning needs Windows";
        var a = VolumeOf(checkoutPath);
        var b = VolumeOf(worktreeRoot);
        if (a == null) return "no volume found for " + checkoutPath;
        if (b == null) return "no volume found for " + worktreeRoot;
        if (!a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return $"the checkout is on {a} and worktrees go to {b}: a clone needs both on one ReFS volume";
        var fs = FileSystemOf(a);
        if (!fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            return $"{a} is {(fs.Length == 0 ? "not ReFS" : fs + ", not ReFS")}: a clone needs a ReFS volume, like a Dev Drive";
        return null;
    }

    /// <summary>The mount point that holds the path, like "D:\". The path itself need not exist yet.</summary>
    public static string? VolumeOf(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        string? p;
        try { p = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or IOException) { return null; }
        // A worktree root that is not there yet still has a volume: its nearest existing parent's.
        while (p != null && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
        if (p == null) return null;
        var buf = new char[1024];
        return GetVolumePathNameW(p, buf, buf.Length) ? new string(buf, 0, Array.IndexOf(buf, '\0') is var n && n >= 0 ? n : buf.Length) : null;
    }

    /// <summary>"NTFS", "ReFS", or "" when the volume did not answer.</summary>
    public static string FileSystemOf(string volume)
    {
        if (!OperatingSystem.IsWindows()) return "";
        var name = new char[261];
        return GetVolumeInformationW(volume, null, 0, out _, out _, out _, name, name.Length)
            ? new string(name, 0, Array.IndexOf(name, '\0') is var n && n >= 0 ? n : name.Length)
            : "";
    }

    public static long ClusterSizeOf(string volume)
    {
        if (!GetDiskFreeSpaceW(volume, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "cluster size of " + volume);
        return (long)sectorsPerCluster * bytesPerSector;
    }

    // ---- making the folders in a worktree ----

    /// <summary>
    /// Puts every shared folder of the checkout into the worktree the way mode says. A folder that is
    /// missing in the checkout, or already there in the worktree, is skipped with a warning, so one
    /// odd folder does not stop the branch. Returns the relative paths that were made.
    /// </summary>
    public static List<string> Populate(ILog log, CheckoutConfig co, string worktree, SharedMode mode)
    {
        var made = new List<string>();
        foreach (var rel in co.Junctions)
        {
            var source = PathUtil.Join(co.Path, rel);
            var target = PathUtil.Join(worktree, rel);
            if (!Directory.Exists(source)) { log.Warn($"{Name(mode)} source missing, skipped: " + source); continue; }
            if (Directory.Exists(target) || File.Exists(target)) { log.Warn($"{Name(mode)} path exists, skipped: " + target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            switch (mode)
            {
                case SharedMode.Junction:
                    Proc.Run("cmd", ["/c", "mklink", "/J", target, source], null, log).EnsureOk();
                    break;
                case SharedMode.Clone:
                    CopyTree(log, rel, source, target, clone: true);
                    break;
                default:
                    CopyTree(log, rel, source, target, clone: false);
                    break;
            }
            made.Add(rel);
        }
        return made;
    }

    /// <summary>
    /// The tree under source, file by file, into target, .svn left out. Junctions inside it are not
    /// followed: they point somewhere else, and that somewhere is not this folder's to copy. Clone
    /// asks ReFS to share the blocks; a file it refuses is copied instead and counted, so the folder
    /// is whole either way. Returns how many files went over.
    /// </summary>
    public static int CopyTree(ILog log, string label, string source, string target, bool clone)
    {
        var (dirs, files) = Collect(source, withSvn: false);
        var verb = clone ? "cloning " : "copying ";
        log.Info($"{verb}{label}: {files.Count} files");

        Directory.CreateDirectory(target);
        foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(target, d));
        Transfer(log, verb + label, files, source, target, clone);
        return files.Count;
    }

    /// <summary>What a mirror did: files that were new, files that differed, and entries that were gone.</summary>
    public sealed record MirrorResult(int Added, int Replaced, int Removed)
    {
        public bool Changed => Added + Replaced + Removed > 0;
        public override string ToString() => $"{Added} new, {Replaced} replaced, {Removed} removed";
    }

    /// <summary>
    /// Brings target in step with source: files that are new or differ in size or time come over,
    /// and files and folders that are gone from source go. A .svn in target goes too, because a
    /// private folder made before this rule had one. Local edits under target do not survive; a
    /// junction never kept them either. A target that is not there yet is simply made.
    /// </summary>
    public static MirrorResult Mirror(ILog log, string label, string source, string target, bool clone)
    {
        if (!Directory.Exists(target)) return new MirrorResult(CopyTree(log, label, source, target, clone), 0, 0);

        var (sDirs, sFiles) = Collect(source, withSvn: false);
        var (tDirs, tFiles) = Collect(target, withSvn: true);
        var have = tFiles.ToDictionary(f => f.Rel, StringComparer.OrdinalIgnoreCase);
        var over = new List<Entry>();
        var replaced = 0;
        foreach (var f in sFiles)
        {
            if (!have.Remove(f.Rel, out var t)) over.Add(f);
            else if (t.Length != f.Length || !SameTime(t.Written, f.Written)) { over.Add(f); replaced++; }
        }
        // What is left in have was not in the source.
        var goneFiles = have.Keys.ToList();
        var keep = sDirs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var goneDirs = tDirs.Where(d => !keep.Contains(d)).OrderByDescending(d => d.Length).ToList();

        log.Info($"refreshing {label}: {over.Count - replaced} new, {replaced} changed, {goneFiles.Count + goneDirs.Count} gone");
        foreach (var d in sDirs) Directory.CreateDirectory(Path.Combine(target, d));
        foreach (var rel in goneFiles) { Cancellation.ThrowIfRequested(); DeleteFile(Path.Combine(target, rel)); }
        foreach (var rel in goneDirs)
        {
            // Deepest first, and each one is empty by now unless it holds a junction, which stays.
            try { Directory.Delete(Path.Combine(target, rel)); }
            catch (IOException e) { log.Warn($"left in place: {rel} ({e.Message})"); }
        }
        // A file that differs is replaced under its old name, so the old one goes first.
        foreach (var f in over) if (File.Exists(Path.Combine(target, f.Rel))) DeleteFile(Path.Combine(target, f.Rel));
        Transfer(log, "refreshing " + label, over, source, target, clone);
        return new MirrorResult(over.Count - replaced, replaced, goneFiles.Count + goneDirs.Count);
    }

    /// <summary>NTFS and ReFS keep times to 100 ns, and a clone gets the source's exact time. Two seconds is FAT's step, and plenty.</summary>
    static bool SameTime(DateTime a, DateTime b) => Math.Abs((a - b).Ticks) <= 2 * TimeSpan.TicksPerSecond;

    /// <summary>The files, side by side, from source into target. Clone shares blocks; a refused file is copied and counted.</summary>
    static void Transfer(ILog log, string label, List<Entry> files, string source, string target, bool clone)
    {
        if (files.Count == 0) return;
        var cluster = clone ? ClusterSizeOf(VolumeOf(target) ?? throw new SgException("no volume for " + target)) : 0;
        var token = Cancellation.Current;
        long done = 0;
        var fellBack = 0;
        // The disk sits idle under one thread of small files, the way DiskUsage found when it walked them.
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, f =>
        {
            var from = Path.Combine(source, f.Rel);
            var to = Path.Combine(target, f.Rel);
            if (clone)
            {
                try { CloneFile(from, to, cluster); }
                catch (Win32Exception)
                {
                    // ReFS says no to this one file: too many references to a block, or a stream it
                    // cannot share. A real copy keeps the folder complete.
                    try { File.Delete(to); } catch (IOException) { /* CloneFile may not have made it */ }
                    File.Copy(from, to);
                    Interlocked.Increment(ref fellBack);
                }
            }
            else File.Copy(from, to);
            var n = Interlocked.Increment(ref done);
            if (n % 256 == 0 || n == files.Count) log.Progress(label, n, files.Count, "files", null);
        });
        log.ProgressEnd(label, fellBack > 0 ? $"{files.Count} files, {fellBack} copied in full" : $"{files.Count} files");
        if (fellBack > 0) log.Warn($"{fellBack} file(s) in {label} could not be cloned and were copied in full");
    }

    /// <summary>SVN keeps its pristine files read-only, and File.Delete refuses those until the bit is off.</summary>
    static void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    readonly record struct Entry(string Rel, long Length, DateTime Written);

    /// <summary>
    /// Every folder and file under root, as relative paths with what a compare needs. Reparse points
    /// are listed as neither. .svn is left out unless asked for: the source never wants it, the
    /// target of a mirror wants it listed so it can go.
    /// </summary>
    static (List<string> Dirs, List<Entry> Files) Collect(string root, bool withSvn)
    {
        var dirs = new List<string>();
        var files = new List<Entry>();
        var stack = new Stack<string>();
        stack.Push("");
        while (stack.Count > 0)
        {
            Cancellation.ThrowIfRequested();
            var rel = stack.Pop();
            var abs = rel.Length == 0 ? root : Path.Combine(root, rel);
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(abs).EnumerateFileSystemInfos("*", EnumerateAll); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var here = rel.Length == 0 ? e.Name : Path.Combine(rel, e.Name);
                if ((e.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!withSvn && e.Name.Equals(".svn", StringComparison.OrdinalIgnoreCase)) continue;
                    dirs.Add(here);
                    stack.Push(here);
                }
                else files.Add(new Entry(here, ((FileInfo)e).Length, e.LastWriteTimeUtc));
            }
        }
        return (dirs, files);
    }

    // Hidden and system entries are part of the folder too. The default listing leaves them out.
    static readonly EnumerationOptions EnumerateAll = new() { AttributesToSkip = 0, IgnoreInaccessible = true };

    // ---- the block clone itself ----

    /// <summary>
    /// A copy of one file that shares its blocks with the original. ReFS keeps one set of blocks and
    /// two files pointing at them, and gives a file its own block only when that block is written.
    /// The clusterSize is the volume's; the FSCTL wants every range aligned to it, and the last
    /// partial cluster of a file is asked for rounded up, which the file system allows.
    /// </summary>
    public static void CloneFile(string source, string target, long clusterSize)
    {
        using var s = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var d = File.OpenHandle(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var length = RandomAccess.GetLength(s);
        if (length > 0)
        {
            RandomAccess.SetLength(d, length);
            // One call takes at most 4 GB less a cluster. 1 GB stays well under it.
            const long chunk = 1L << 30;
            for (long offset = 0; offset < length; offset += chunk)
            {
                var count = Math.Min(chunk, length - offset);
                var rounded = (count + clusterSize - 1) / clusterSize * clusterSize;
                var data = new DUPLICATE_EXTENTS_DATA
                {
                    FileHandle = s.DangerousGetHandle(),
                    SourceFileOffset = offset,
                    TargetFileOffset = offset,
                    ByteCount = rounded,
                };
                if (!DeviceIoControl(d, FSCTL_DUPLICATE_EXTENTS_TO_FILE, ref data, Marshal.SizeOf<DUPLICATE_EXTENTS_DATA>(), IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "block clone of " + source);
            }
        }
        d.Dispose();
        // A build tool that compares times sees what the checkout has, and a read-only file stays so.
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        var attrs = File.GetAttributes(source) & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        if (attrs != 0) File.SetAttributes(target, attrs);
    }

    const uint FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x98344;

    [StructLayout(LayoutKind.Sequential)]
    struct DUPLICATE_EXTENTS_DATA
    {
        public IntPtr FileHandle;
        public long SourceFileOffset;
        public long TargetFileOffset;
        public long ByteCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref DUPLICATE_EXTENTS_DATA lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetVolumePathNameW(string lpszFileName, [Out] char[] lpszVolumePathName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetVolumeInformationW(string lpRootPathName, char[]? lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags,
        [Out] char[] lpFileSystemNameBuffer, int nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetDiskFreeSpaceW(string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);
}
