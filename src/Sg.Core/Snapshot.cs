using System.IO.Enumeration;
using System.Text;

namespace Sg.Core;

public sealed record ExternalInfo(string Rel, long Revision, string Url);

public sealed class SnapshotInfo
{
    public string Sha = "";
    public long Revision;
    public string Url = "";
    public List<ExternalInfo> Externals = new();
    public List<string> Warnings = new();
    public int Overlaid;
    public bool Unchanged;
}

/// <summary>What a snapshot commit says about itself, read back from its trailers.</summary>
public sealed class SnapshotMeta
{
    public long Revision;
    public string Url = "";
    public Dictionary<string, long> Externals = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExternalUrls = new(StringComparer.OrdinalIgnoreCase);

    public static SnapshotMeta Parse(string body)
    {
        var m = new SnapshotMeta();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("svn-rev: ")) long.TryParse(line[9..].Trim(), out m.Revision);
            else if (line.StartsWith("svn-url: ")) m.Url = line[9..].Trim();
            else if (line.StartsWith("svn-external: "))
            {
                var parts = line[14..].Split(' ', 3);
                if (parts.Length >= 2 && long.TryParse(parts[1], out var rev)) m.Externals[parts[0]] = rev;
                if (parts.Length == 3) m.ExternalUrls[parts[0]] = parts[2].Trim();
            }
        }
        return m;
    }
}

/// <summary>
/// A snapshot is the exact content of a checkout as SVN has it, at the revisions of the root and of every external.
/// Local edits and skipped paths are left out. Only files that changed on disk get hashed.
/// </summary>
public static class Snapshot
{
    static readonly HashSet<string> OverlayItems = new(["modified", "deleted", "missing", "conflicted", "replaced"]);
    static readonly HashSet<string> ExcludeItems = new(["unversioned", "ignored", "added"]);

    public static SnapshotInfo Build(SgRoot root, CheckoutConfig co, string? parentSha, Func<SnapshotInfo, string>? extraMessage = null)
    {
        var git = root.Git;
        var svn = root.Svn;
        var wt = co.Path;
        var info = new SnapshotInfo();

        var status = svn.Status(wt, noIgnore: true);
        var externals = status.Where(e => e.Item == "external" && e.Path.Length > 0)
            .Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();

        // 1. What git must not add: svn metadata, the skip list, unversioned and ignored and added items, reserved names.
        var excludes = new List<string> { ".svn" };
        excludes.AddRange(externals.Select(e => e + "/.svn"));
        excludes.AddRange(co.Skip);
        var excludedEntries = status.Where(e => e.Path.Length > 0 && ExcludeItems.Contains(e.Item)).Select(e => e.Path).ToList();
        excludes.AddRange(excludedEntries);
        var reserved = status.Where(e => e.Path.Length > 0 && PathUtil.HasReservedName(e.Path)).Select(e => e.Path).ToList();
        excludes.AddRange(reserved);

        // 2. Stage everything else. -f makes git ignore any .gitignore that lives inside the SVN tree.
        // The first snapshot hashes every file, so count bytes first and show a real bar.
        var log = root.Log;
        Dictionary<string, long>? sizes = null;
        long totalBytes = 0;
        if (parentSha == null)
        {
            log.Progress("counting files", 0, 0, "files", null);
            var (found, reservedOnDisk) = WalkSizes(wt, excludes, log);
            sizes = found;
            totalBytes = sizes.Values.Sum();
            excludes.AddRange(reservedOnDisk);
            reserved.AddRange(reservedOnDisk);
            log.ProgressEnd("counting files", $"{sizes.Count} files, {Human(totalBytes)}");
        }
        long done = 0;
        var count = 0;
        var hashLabel = sizes != null ? "hashing" : "hashing changed files";
        var invalid = git.AddAllForced(wt, excludes.Distinct(StringComparer.OrdinalIgnoreCase), added =>
        {
            count++;
            if (sizes != null)
            {
                if (sizes.TryGetValue(added, out var sz)) done += sz;
                log.Progress(hashLabel, done, totalBytes, "B", added);
            }
            else log.Progress(hashLabel, count, 0, "files", added);
        });
        log.ProgressEnd(hashLabel, sizes != null ? $"{count} files, {Human(totalBytes)}" : $"{count} files");
        foreach (var p in invalid.Concat(reserved).Distinct(StringComparer.OrdinalIgnoreCase))
            info.Warnings.Add("skipped a path git cannot hold on Windows: " + p);

        // 3. Drop stale index entries for things that must not be tracked any more.
        git.RmCached(wt, excludedEntries.Concat(co.Skip).Concat(invalid).Distinct(StringComparer.OrdinalIgnoreCase));

        // 4. Local edits: put the pristine (BASE) content in the index instead of the working file.
        var candidates = status
            .Where(e => e.Path.Length > 0 && OverlayItems.Contains(e.Item) && !co.Skip.Any(s => PathUtil.IsUnder(e.Path, s)))
            .Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var o in status.Where(e => e.Item == "obstructed"))
            info.Warnings.Add("obstructed path taken as on disk: " + o.Path);

        if (candidates.Count > 0)
        {
            var infos = svn.InfoMany(wt, candidates, recursive: true);
            var files = infos.Where(i => i.Kind == "file" && i.Path.Length > 0).Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var tmpDir = root.NewTempDir();
            try
            {
                var tmpFiles = new List<string>();
                var paths = new List<string>();
                var n = 0;
                foreach (var f in files)
                {
                    var tf = Path.Combine(tmpDir, (n++).ToString());
                    var r = svn.CatToFile(wt, f, "BASE", tf);
                    if (!r.Ok)
                    {
                        info.Warnings.Add("no pristine copy for " + f + ", it is left out: " + r.StdErr.Trim());
                        git.RmCached(wt, [f]);
                        continue;
                    }
                    tmpFiles.Add(tf);
                    paths.Add(f);
                }
                var shas = git.HashObjects(tmpFiles);
                git.UpdateIndexInfo(wt, paths.Select((p, i) => ("100644", shas[i], p)));
                info.Overlaid = paths.Count;
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { /* best effort */ }
            }
        }

        // 5. Revisions of the root and of every external.
        var targets = new List<string> { "." };
        targets.AddRange(externals);
        var revInfos = svn.InfoMany(wt, targets, recursive: false);
        var rootInfo = revInfos.FirstOrDefault(i => i.Path.Length == 0) ?? throw new SgException("svn info failed for " + wt);
        info.Revision = rootInfo.Revision;
        info.Url = rootInfo.Url;
        foreach (var ext in externals)
        {
            var ei = revInfos.FirstOrDefault(i => i.Path.Equals(ext, StringComparison.OrdinalIgnoreCase));
            if (ei != null) info.Externals.Add(new ExternalInfo(ext, ei.Revision, ei.Url));
            else info.Warnings.Add("no svn info for external " + ext);
        }

        // 6. Commit, unless nothing changed since the parent.
        var tree = git.WriteTree(wt);
        var snapRef = root.SnapshotRef(co);
        if (parentSha != null && git.TreeOf(parentSha) == tree)
        {
            var prev = SnapshotMeta.Parse(git.Body(parentSha));
            var same = prev.Revision == info.Revision && prev.Externals.Count == info.Externals.Count
                       && info.Externals.All(e => prev.Externals.TryGetValue(e.Rel, out var r) && r == e.Revision);
            if (same)
            {
                info.Sha = parentSha;
                info.Unchanged = true;
                git.UpdateRef(snapRef, parentSha);
                git.SetHeadDetached(wt, parentSha);
                return info;
            }
        }

        var extra = extraMessage?.Invoke(info) ?? "";
        var sha = git.CommitTree(tree, parentSha ?? git.EnsureRootCommit(), BuildMessage(co, info, extra));
        git.UpdateRef(snapRef, sha);
        git.SetHeadDetached(wt, sha);
        info.Sha = sha;
        return info;
    }

    /// <summary>Sizes of every file git is about to hash, and the paths with reserved Windows names, which git would refuse.</summary>
    static (Dictionary<string, long> Sizes, List<string> Reserved) WalkSizes(string rootDir, List<string> excludes, ILog log)
    {
        var excluded = new HashSet<string>(excludes, StringComparer.OrdinalIgnoreCase);
        var seen = 0;

        void Report(int added)
        {
            if (added == 0) return;
            var now = Interlocked.Add(ref seen, added);
            if (now / 2000 != (now - added) / 2000) log.Progress("counting files", now, 0, "files", null);
        }

        var top = new Walked();
        ScanOne(rootDir, "", excluded, top, Report);

        // The subtrees are walked side by side. The walk waits on the disk, and this counts every file
        // of the checkout before the first snapshot hashes any of them.
        var parts = Fan.Map(top.Dirs, start =>
        {
            var found = new Walked();
            var stack = new Stack<(string Abs, string Rel)>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var (abs, rel) = stack.Pop();
                var here = found.Dirs.Count;
                ScanOne(abs, rel, excluded, found, Report);
                // Whatever that folder added is what is left to walk.
                for (var i = found.Dirs.Count - 1; i >= here; i--) stack.Push(found.Dirs[i]);
                found.Dirs.RemoveRange(here, found.Dirs.Count - here);
            }
            return found;
        });

        foreach (var part in parts)
        {
            foreach (var (path, size) in part.Sizes) top.Sizes[path] = size;
            top.Reserved.AddRange(part.Reserved);
        }
        return (top.Sizes, top.Reserved);
    }

    /// <summary>One subtree as it is walked. Each parallel walk fills its own, and WalkSizes merges them.</summary>
    sealed class Walked
    {
        public readonly Dictionary<string, long> Sizes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Reserved = new();
        public readonly List<(string Abs, string Rel)> Dirs = new();
    }

    static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    readonly record struct Entry(bool IsDir, bool IsLink, long Length, string Rel, string Abs);

    /// <summary>
    /// One folder's own entries, named the way git will name them. The size and the attributes come
    /// straight out of the directory listing, so a folder of ten thousand files costs no object per file.
    /// </summary>
    static void ScanOne(string abs, string rel, HashSet<string> excluded, Walked into, Action<int> report)
    {
        var files = 0;
        try
        {
            var walk = new FileSystemEnumerable<Entry>(abs,
                (ref FileSystemEntry e) => new Entry(
                    e.IsDirectory,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0,
                    e.Length,
                    rel.Length == 0 ? e.FileName.ToString() : string.Concat(rel.AsSpan(), "/".AsSpan(), e.FileName),
                    e.IsDirectory ? e.ToFullPath() : ""),
                WalkOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !e.FileName.Equals(".svn", StringComparison.Ordinal) && !e.FileName.Equals(".git", StringComparison.Ordinal),
            };
            foreach (var e in walk)
            {
                if (excluded.Contains(e.Rel)) continue;
                // A reserved segment is never walked into, so asking about the whole path is the same
                // question as asking about this entry's own name.
                if (PathUtil.HasReservedName(e.Rel)) { into.Reserved.Add(e.Rel); continue; }
                if (e.IsDir)
                {
                    // A worktree junctions folders into the checkout; those bytes are not this tree's.
                    if (!e.IsLink) into.Dirs.Add((e.Abs, e.Rel));
                }
                else
                {
                    into.Sizes[e.Rel] = e.Length;
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* what was read still counts */ }
        report(files);
    }

    static string Human(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB"
        : b >= 1 << 20 ? $"{b / (double)(1 << 20):0} MB"
        : $"{b / 1024.0:0} KB";

    static string BuildMessage(CheckoutConfig co, SnapshotInfo info, string extra)
    {
        var sb = new StringBuilder();
        sb.Append(co.Name).Append(" r").Append(info.Revision).Append('\n');
        foreach (var e in info.Externals) sb.Append("external ").Append(e.Rel).Append(" r").Append(e.Revision).Append(' ').Append(e.Url).Append('\n');
        if (extra.Trim().Length > 0) sb.Append('\n').Append(extra.Trim()).Append('\n');
        sb.Append('\n');
        sb.Append("svn-rev: ").Append(info.Revision).Append('\n');
        sb.Append("svn-url: ").Append(info.Url).Append('\n');
        foreach (var e in info.Externals) sb.Append("svn-external: ").Append(e.Rel).Append(' ').Append(e.Revision).Append(' ').Append(e.Url).Append('\n');
        return sb.ToString();
    }
}
