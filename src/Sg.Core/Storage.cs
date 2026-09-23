namespace Sg.Core;

public sealed class ArchivePlan
{
    public string Branch { get; set; } = "";
    public string Path { get; set; } = "";
    public string Head { get; set; } = "";
    public string Checkout { get; set; } = "";
    public long LogicalBytes { get; set; }
    public long? ReclaimableBytes => null;
    public List<string> Blockers { get; set; } = new();
    public bool Ready => Blockers.Count == 0;
}

public sealed class TemporaryDataPlan
{
    public string Path { get; set; } = "";
    public long LogicalBytes { get; set; }
    public string Version { get; set; } = "";
    public List<string> Blockers { get; set; } = new();
}

/// <summary>Conservative manual archive: preserve commits, refuse any content outside their coverage.</summary>
public static class Storage
{
    public static List<TemporaryDataPlan> TemporaryData(SgRoot root)
    {
        using var operation = root.Lock();
        var folder = Path.Combine(root.StorePath, "tmp");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder).Select(path => TemporaryPlan(root, path)).ToList();
    }
    static TemporaryDataPlan TemporaryPlan(SgRoot root, string path)
    {
        var folder = Path.GetFullPath(Path.Combine(root.StorePath, "tmp"));
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase)) throw new SgException("Cleanup target is outside this store's temporary directory.");
        var plan = new TemporaryDataPlan { Path = path };
        if (PathUtil.IsReparsePoint(folder) || PathUtil.IsReparsePoint(path)) { plan.Blockers.Add("Linked directories cannot be cleaned here."); return plan; }
        if (Operations.List(root).Any(x => !x.Terminal)) plan.Blockers.Add("An unfinished operation may own temporary data.");
        if (Directory.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-1)) plan.Blockers.Add("Recent temporary data is retained for at least 24 hours.");
        var content = new List<string>();
        void Visit(string dir)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal))
            {
                Cancellation.ThrowIfRequested();
                if (PathUtil.IsReparsePoint(entry)) { plan.Blockers.Add("Linked content: " + entry); continue; }
                if (Directory.Exists(entry)) { content.Add("directory:" + entry); Visit(entry); }
                else
                {
                    var file = new FileInfo(entry); plan.LogicalBytes += file.Length;
                    content.Add(entry + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks);
                    if (file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) plan.Blockers.Add("Recent file: " + entry);
                }
            }
        }
        Visit(path);
        plan.Version = WorkspaceVersion.Hash(string.Join("\n", content));
        return plan;
    }
    public static void CleanTemporaryData(SgRoot root, TemporaryDataPlan plan)
    {
        using var operation = root.Lock();
        var current = TemporaryPlan(root, plan.Path);
        if (current.Blockers.Count > 0) throw new SgException(string.Join("\n", current.Blockers));
        if (current.Version != plan.Version) throw new SgException("Temporary data changed. Refresh the preview first.");
        SgRoot.SweepTempDir(current.Path);
        if (Directory.Exists(current.Path)) throw new SgException("Some temporary files are still in use; the folder was retained.");
        Operations.Receipt(root, "Temporary data removed", "", [current.Path, "Logical bytes removed: " + current.LogicalBytes + "; physical savings unknown."]);
    }
    public static List<ArchivePlan> List(SgRoot root) => root.Git.WorktreeList().Where(w => !string.IsNullOrEmpty(w.Branch) && Directory.Exists(w.Path))
        .Select(w => Plan(root, w.Branch!)).ToList();
    public static ArchivePlan Plan(SgRoot root, string branch)
    {
        using var operation = root.Lock();
        var git = root.Git;
        var wt = git.WorktreeList().FirstOrDefault(w => w.Branch == branch) ?? throw new SgException("No worktree for " + branch);
        var plan = new ArchivePlan { Branch = branch, Path = wt.Path, Head = git.HeadSha(wt.Path), Checkout = Ops.BaseCheckout(root, branch).Name };
        if (Operations.Pending(root, wt.Path) != null || Conflicts.HasPending(git, wt.Path)) plan.Blockers.Add("An unfinished operation protects this worktree.");
        foreach (var edit in git.StatusEntries(wt.Path, untracked: true)) plan.Blockers.Add("Save or commit first: " + edit.Path);
        foreach (var file in git.Out(wt.Path, "ls-files", "--others", "--ignored", "--exclude-standard", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries))
            plan.Blockers.Add("Outside commit coverage: " + file);
        // Do not traverse junctions, symlinks or shared folders and then claim their contents were archived.
        void Measure(string dir)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                Cancellation.ThrowIfRequested();
                if (Path.GetFileName(entry) == ".git") continue;
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0) { plan.Blockers.Add("Linked content is not archived: " + entry); continue; }
                if ((attrs & FileAttributes.Directory) != 0) Measure(entry); else plan.LogicalBytes += new FileInfo(entry).Length;
            }
        }
        Measure(wt.Path);
        return plan;
    }
    public static string Archive(SgRoot root, ArchivePlan plan)
    {
        using var operation = root.Lock();
        var current = Plan(root, plan.Branch);
        if (!current.Ready) throw new SgException(string.Join("\n", current.Blockers));
        if (current.Head != plan.Head || current.Path != plan.Path || current.Checkout != plan.Checkout) throw new SgException("Worktree changed. Refresh the archive preview.");
        // A journal and reachable ref must both exist before removal. Failure preserves the branch.
        var record = Operations.ArchiveCheckpoint(root, plan.Branch, plan.Path, plan.Checkout, plan.Head);
        root.Git.Run(null, "fsck", "--connectivity-only", "--no-reflogs", record.Checkpoint).EnsureOk();
        var final = Plan(root, plan.Branch);
        if (!final.Ready || final.Head != plan.Head || final.Path != plan.Path || final.Checkout != plan.Checkout)
            throw new SgException("Worktree changed while its checkpoint was verified. The checkpoint is retained; refresh the archive preview.");
        Ops.Remove(root, plan.Branch, force: false);
        Operations.Receipt(root, "Archive completed", "", ["Removed " + plan.Path, "Recover commits through Activity checkpoint " + record.Id]);
        return record.Id;
    }
}
