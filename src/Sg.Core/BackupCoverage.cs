using System.Text.Json;

namespace Sg.Core;

public sealed class CoverageItem
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public int Commits { get; set; }
    public DateTimeOffset? Uploaded { get; set; }
    public List<string> Files { get; set; } = new();
    public List<string> Excluded { get; set; } = new();
}
public sealed class HandoffReceipt
{
    public int Format { get; set; } = 1;
    public string Branch { get; set; } = "";
    public string Checkout { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string Version { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string RemoteIdentity { get; set; } = "";
    public DateTimeOffset Checked { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Refs { get; set; } = new();
    public List<CoverageItem> Coverage { get; set; } = new();
    public string Note { get; set; } = "";
    public string? RestorePath { get; set; }
    public DateTimeOffset? RestoreTested { get; set; }
    public string? TestedSnapshot { get; set; }
}

public static partial class Backup
{
    static string CoverageFile(SgRoot root, string branch) => Path.Combine(root.StorePath, "coverage", WorkspaceVersion.Hash(branch) + ".json");
    static string RemoteIdentity(BackupConfig cfg) => WorkspaceVersion.Hash(cfg.Url + "\n" + cfg.Prefix);
    public static HandoffReceipt Coverage(SgRoot root, string path)
    {
        using var operation = root.Lock();
        var cfg = Require(root);
        var branch = root.Git.CurrentBranch(path);
        var co = Ops.BaseCheckout(root, branch);
        var report = Run(root, check: true);
        var remote = root.Git.LsRemote(cfg.Url);
        var receipt = new HandoffReceipt { Branch = branch, Checkout = co.Name, Snapshot = root.Git.RefSha(root.SnapshotRef(co))!,
            Version = WorkspaceVersion.Of(root, path), Configuration = WorkspaceVersion.Hash(JsonSerializer.Serialize(cfg, SgConfig.JsonOptions)), RemoteIdentity = RemoteIdentity(cfg),
            Note = "Ignored and shared content is outside backup coverage. Remote equality is a point-in-time check." };
        var shelfInfos = Shelf.List(root).Where(x => x.Branch == branch).ToList();
        var shelves = shelfInfos.Select(x => x.Id).ToHashSet();
        var uploaded = Last(root);
        foreach (var item in report.Items.Where(x => (x.Kind is "branch" or "wip" && x.Name == branch) || (x.Kind == "shelf" && shelves.Contains(x.Name))))
        {
            remote.TryGetValue(item.RemoteRef, out var sha);
            receipt.Coverage.Add(new() { Kind = item.Kind, Name = item.Name, Commits = item.Commits,
                Uploaded = uploaded?.Items.Any(x => x.RemoteRef == item.RemoteRef && x.Thin == item.Thin && x.State == "pushed") == true ? uploaded.When : null,
                Files = item.Kind == "shelf" ? shelfInfos.First(x => x.Id == item.Name).Files.Select(x => x.Path).ToList() : item.Kind == "wip" ? root.Git.StatusEntries(path, true).Select(x => x.Path).ToList() : root.Git.Out(path, "diff", "--name-only", root.Git.MergeBase(receipt.Snapshot, "refs/heads/" + branch)!, "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                State = sha != null && sha == item.Thin ? "Remote refs checked" : item.State,
                Excluded = item.LeftOut.Concat(item.Why == null ? Array.Empty<string>() : new[] { item.Why }).ToList() });
            if (sha != null) receipt.Refs[item.RemoteRef] = sha;
        }
        if (IsExcluded(cfg, branch)) receipt.Coverage.Add(new() { Kind = "branch", Name = branch, State = "Excluded from backup" });
        if (!cfg.Uncommitted) receipt.Coverage.Add(new() { Kind = "wip", Name = branch, State = "Uncommitted backup disabled" });
        AtomicFile.WriteAllText(CoverageFile(root, branch), JsonSerializer.Serialize(receipt, SgConfig.JsonOptions));
        return receipt;
    }
    public static HandoffReceipt ReadReceipt(string file)
    {
        var receipt = JsonSerializer.Deserialize<HandoffReceipt>(File.ReadAllText(file), SgConfig.JsonOptions) ?? throw new SgException("Invalid handoff receipt.");
        if (receipt.Format != 1) throw new SgException("Unsupported handoff receipt format.");
        return receipt;
    }
    public static void WriteReceipt(HandoffReceipt receipt, string file) => AtomicFile.WriteAllText(file, JsonSerializer.Serialize(receipt, SgConfig.JsonOptions));
    public static List<string> ValidateReceipt(SgRoot root, HandoffReceipt receipt)
    {
        var cfg = Require(root);
        var issues = new List<string>();
        if (receipt.RemoteIdentity != RemoteIdentity(cfg)) issues.Add("Receipt belongs to a different configured backup location.");
        var remote = root.Git.LsRemote(cfg.Url);
        foreach (var pair in receipt.Refs)
            if (!remote.TryGetValue(pair.Key, out var current) || current != pair.Value) issues.Add("Remote ref changed or disappeared: " + pair.Key);
        if (!receipt.Refs.ContainsKey(RemoteRef(cfg, "branch", receipt.Branch))) issues.Add("No branch ref was recorded.");
        var wipRef = RemoteRef(cfg, "wip", receipt.Branch);
        if (remote.ContainsKey(wipRef) != receipt.Refs.ContainsKey(wipRef)) issues.Add("Uncommitted backup coverage changed.");
        if (issues.Count > 0) receipt.RestoreTested = null;
        return issues;
    }
    public static string LocalReceiptStatus(SgRoot root, string path, HandoffReceipt receipt)
    {
        if (root.Git.CurrentBranch(path) != receipt.Branch) return "Receipt describes a different branch. Preview its destination before restoring.";
        var cfg = Require(root);
        if (WorkspaceVersion.Of(root, path) != receipt.Version || root.Git.RefSha(root.SnapshotRef(Ops.BaseCheckout(root, receipt.Branch))) != receipt.Snapshot
            || WorkspaceVersion.Hash(JsonSerializer.Serialize(cfg, SgConfig.JsonOptions)) != receipt.Configuration)
            return "Changed since receipt: current work or backup configuration differs. Recorded coverage is historical.";
        return "Receipt matches this local branch version. Remote refs require a fresh check.";
    }

    public static HandoffReceipt TestRestore(SgRoot root, HandoffReceipt receipt)
    {
        using var operation = root.Lock();
        receipt.RestoreTested = null;
        var issues = ValidateReceipt(root, receipt);
        if (issues.Count > 0) throw new SgException(string.Join("\n", issues));
        var name = receipt.Branch + "-restore-test-" + Guid.NewGuid().ToString("N")[..8];
        // Force replay from the fetched thin objects; relinking existing local commits would not test the backup.
        var result = Restore(root, receipt.Branch, asBranch: name, wip: true, expectedRefs: receipt.Refs, rehearsal: true);
        receipt.RestorePath = result.Path;
        receipt.TestedSnapshot = root.Git.RefSha(root.SnapshotRef(root.Checkout(result.Checkout)));
        receipt.RestoreTested = result.Ok && result.WipConflicted.Count == 0 && result.WipWhy == null && result.Shelves.All(x => !x.Contains("conflict markers")) && ValidateReceipt(root, receipt).Count == 0 ? DateTimeOffset.UtcNow : null;
        WriteReceipt(receipt, CoverageFile(root, receipt.Branch));
        Operations.Receipt(root, "Backup restore test", result.Path, [receipt.RestoreTested != null ? "Recorded backup refs replayed successfully." : "Restore needs review; no verification granted.", "Rehearsal worktree retained at " + result.Path]);
        return receipt;
    }
}
