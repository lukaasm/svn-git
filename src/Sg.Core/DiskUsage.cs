using System.IO.Enumeration;

namespace Sg.Core;

/// <summary>What a folder costs on disk. Partial means some of it could not be read.</summary>
public sealed record FolderSize(long Bytes, int Files, bool Partial)
{
    public static readonly FolderSize Empty = new(0, 0, false);

    public override string ToString() => DiskUsage.Human(Bytes) + (Partial ? " or more" : "");
}

/// <summary>
/// What one shared folder of a worktree costs. Linked is a junction: the folder is the checkout's own
/// and this worktree spends nothing on it. Shared is a ReFS block clone: it looks like a full folder
/// to every API and to this walk, but the blocks are the checkout's until one side writes, so those
/// bytes are not spent either. Everything else is a real copy, and Bytes is what it really costs.
/// </summary>
public sealed record SharedCost(string Rel, bool Linked, bool Shared, bool Missing, long Bytes)
{
    /// <summary>Bytes this worktree really spends on the folder. A junction and a clone spend none.</summary>
    public long Own => Linked || Shared || Missing ? 0 : Bytes;

    /// <summary>A full copy of a folder the checkout already holds. A junction or a ReFS clone would not cost this.</summary>
    public bool Copied => !Linked && !Shared && !Missing;
}

/// <summary>
/// A worktree costs about 4 GB without the optional folders and about 16 GB with them, plus its own
/// build output. Nothing in the tool showed that, so old branches sat on the disk unnoticed.
/// </summary>
public static class DiskUsage
{
    /// <summary>
    /// A full copy of the shared folders is worth saying out loud from here up. Below it the copy is
    /// cheaper than the sentence explaining it; the folders this is about run to tens of gigabytes.
    /// </summary>
    public const long CopyWarnBytes = 1L << 30;

    /// <summary>
    /// What each shared folder of a worktree costs, one entry per folder the checkout shares.
    /// <paramref name="mode"/> is what the branch recorded at "branch.&lt;name&gt;.sgShared": "junction",
    /// "clone", "copy", or "" for a worktree older than the choice.
    ///
    /// A junction is spotted on disk rather than taken from the mode, because Populate skips a folder
    /// that is already there and warns, and that leaves a worktree whose mode says junction holding a
    /// real folder. A clone cannot be told from a copy by looking, so the mode is the only witness
    /// there; an unknown mode is read as a copy, which is the answer that costs the user nothing to
    /// hear and something to ignore.
    /// </summary>
    public static List<SharedCost> MeasureShared(string worktree, IEnumerable<string> rels, string mode, CancellationToken cancel = default)
    {
        var cloned = mode.Equals("clone", StringComparison.OrdinalIgnoreCase);
        var costs = new List<SharedCost>();
        foreach (var rel in rels)
        {
            cancel.ThrowIfCancellationRequested();
            var path = PathUtil.Join(worktree, rel);
            DirectoryInfo info;
            try { info = new DirectoryInfo(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                costs.Add(new SharedCost(rel, false, false, true, 0));
                continue;
            }
            if (!info.Exists) { costs.Add(new SharedCost(rel, false, false, true, 0)); continue; }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { costs.Add(new SharedCost(rel, true, false, false, 0)); continue; }
            // A clone's bytes are the checkout's, so there is nothing to learn from walking it.
            if (cloned) { costs.Add(new SharedCost(rel, false, true, false, 0)); continue; }
            costs.Add(new SharedCost(rel, false, false, false, Measure(path, null, cancel).Bytes));
        }
        return costs;
    }

    /// <summary>
    /// Walks the folder without following junctions. A worktree junctions folders like libs/prebuilt
    /// into the checkout, and those bytes belong to the checkout, not to this branch. A cloned folder
    /// reports its full size to every API while it shares every block with the checkout, so the
    /// caller names those folders in leaveOut and they are not walked either.
    /// </summary>
    public static FolderSize Measure(string folder, IEnumerable<string>? leaveOut = null, CancellationToken cancel = default)
    {
        if (!Directory.Exists(folder)) return FolderSize.Empty;
        var skip = new HashSet<string>(
            (leaveOut ?? []).Select(rel => Path.GetFullPath(PathUtil.Join(folder, rel)).TrimEnd('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        var top = ScanOne(folder, cancel);
        long bytes = top.Bytes;
        long files = top.Files;
        var partial = top.Partial ? 1 : 0;

        // The subtrees are walked side by side. The walk waits on the disk, and a worktree here is
        // several gigabytes over a hundred thousand files, so one thread left most of the disk idle.
        Parallel.ForEach(top.Dirs, new ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = cancel }, dir =>
        {
            if (skip.Contains(dir.TrimEnd('\\', '/'))) return;
            var (b, f, p) = WalkSerial(dir, skip, cancel);
            Interlocked.Add(ref bytes, b);
            Interlocked.Add(ref files, f);
            if (p) Interlocked.Exchange(ref partial, 1);
        });

        return new FolderSize(bytes, (int)files, partial != 0);
    }

    static int Workers => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    static (long Bytes, int Files, bool Partial) WalkSerial(string root, HashSet<string> skip, CancellationToken cancel)
    {
        long bytes = 0;
        var files = 0;
        var partial = false;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var one = ScanOne(stack.Pop(), cancel);
            bytes += one.Bytes;
            files += one.Files;
            partial |= one.Partial;
            foreach (var d in one.Dirs)
                if (skip.Count == 0 || !skip.Contains(d.TrimEnd('\\', '/'))) stack.Push(d);
        }
        return (bytes, files, partial);
    }

    /// <summary>
    /// One folder's own entries. Reads the size and the attributes straight out of the directory
    /// listing, so a folder of ten thousand files costs no object per file.
    /// </summary>
    static (long Bytes, int Files, bool Partial, List<string> Dirs) ScanOne(string dir, CancellationToken cancel)
    {
        long bytes = 0;
        var files = 0;
        var dirs = new List<string>();
        try
        {
            var walk = new FileSystemEnumerable<Found>(dir,
                (ref FileSystemEntry e) => new Found(e.IsDirectory, e.Length, e.IsDirectory ? e.ToFullPath() : ""),
                Options)
            {
                // A worktree junctions folders into the checkout, and those bytes belong to the checkout.
                ShouldIncludePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
            };
            foreach (var e in walk)
            {
                cancel.ThrowIfCancellationRequested();
                if (e.IsDir) dirs.Add(e.Path);
                else { bytes += e.Length; files++; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (bytes, files, true, dirs);
        }
        return (bytes, files, false, dirs);
    }

    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    readonly record struct Found(bool IsDir, long Length, string Path);

    /// <summary>The newest write time among the folder's own entries. A rough "last touched".</summary>
    public static DateTime? LastTouched(string folder)
    {
        try
        {
            var newest = DateTime.MinValue;
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (entry.Name is ".git" or ".gitignore") continue;
                if (entry.LastWriteTime > newest) newest = entry.LastWriteTime;
            }
            return newest == DateTime.MinValue ? null : newest;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Human(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
        : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0} MB"
        : $"{bytes / 1024.0:0} KB";
}
