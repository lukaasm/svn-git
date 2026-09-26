namespace Sg.Core;

/// <summary>A cheap comparison of committed tips only; saved edits and shelves are not compared.</summary>
public enum BackupCommitStatus { NotCompared, Saved, LocalDiffers }

/// <summary>A worktree's local folder and remote branch, joined by name without reading remote histories.</summary>
public sealed record BackupWorktree(string Name, string? Path, string? Checkout, bool Excluded, BackupReference? Remote)
{
    public bool CanBackUp => Path != null && !Excluded;
    public BackupCommitStatus CommitStatus { get; init; }
    public DateTimeOffset? LastConfirmed { get; init; }
    public bool NeedsAttention => CanBackUp && CommitStatus != BackupCommitStatus.Saved;
}

/// <summary>Exact remote versions shown in a prune confirmation. Execution never adds new candidates.</summary>
public sealed class BackupPrunePlan
{
    public IReadOnlyDictionary<string, string> Refs { get; }
    public string? Worktree { get; }
    internal string Root { get; }
    internal string Url { get; }
    internal string Prefix { get; }
    internal BackupPrunePlan(SgRoot root, BackupConfig cfg, string? worktree, Dictionary<string, string> refs)
        => (Root, Url, Prefix, Worktree, Refs) = (root.RootPath, cfg.Url, cfg.Prefix, worktree,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(refs));
}

public static partial class Backup
{
    public static IReadOnlyList<BackupWorktree> Worktrees(SgRoot root, BackupCatalog catalog)
    {
        using var reading = root.Git.Reading();
        var bases = root.Git.BranchBases();
        var local = root.Git.WorktreeList().Where(w => !w.Bare && w.Branch != null && bases.ContainsKey(w.Branch))
            .GroupBy(w => w.Branch!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var remote = catalog.Items.Where(i => i.Kind == "branch").ToDictionary(i => i.Name, StringComparer.Ordinal);
        // Read all tip metadata in one process. Browsing must not scan files or fetch each saved history.
        var tips = root.Git.RefIndex("refs/heads/", PushedPrefix + "heads/");
        var confirmed = root.Git.BranchConfig("sgBackedUp");
        return local.Keys.Union(remote.Keys, StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var row = new BackupWorktree(name, local.TryGetValue(name, out var w) && Directory.Exists(w.Path) ? w.Path : null,
                bases.GetValueOrDefault(name), IsExcluded(root.Config.Backup, name), remote.GetValueOrDefault(name));
            var pushed = tips.GetValueOrDefault(PushedRef("branch", name));
            // A changed remote may be equivalent after a replay. Do not guess which side is newer.
            // Timestamps from another destination are not evidence about this advertised version.
            if (row.Remote == null || pushed == null || row.Remote.Sha != pushed.Sha) return row;
            var source = Thin.Parse(pushed.Sha, pushed.Message, new Author("", "", "")).Source;
            var head = tips.GetValueOrDefault("refs/heads/" + name);
            return row with
            {
                LastConfirmed = BackedUpAt(confirmed.GetValueOrDefault(name)),
                CommitStatus = row.Path == null || head == null || source == null ? BackupCommitStatus.NotCompared
                    : source == head.Sha ? BackupCommitStatus.Saved : BackupCommitStatus.LocalDiffers,
            };
        }).ToArray();
    }

    static bool IsPresent(Local here, string kind, string name) => kind switch
    {
        "branch" or "wip" or "review" or "appearance" => here.Branches.Contains(name),
        "edits" => here.Checkouts.Contains(name),
        _ => here.Shelves.Contains(name),
    };

    public static BackupPrunePlan PlanPrune(SgRoot root, string? worktree = null)
    {
        if (worktree != null && string.IsNullOrWhiteSpace(worktree)) throw new SgException("Choose a worktree to prune.");
        using var operation = root.Lock();
        var cfg = Require(root);
        var remote = root.Git.LsRemote(cfg.Url);
        var here = LocalNames(root, cfg);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (remoteRef, sha) in remote.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Cancellation.ThrowIfRequested();
            if (Owned(cfg, remoteRef) is not { } item || IsPresent(here, item.Kind, item.Name)) continue;
            if (worktree != null)
            {
                if (item.Kind == "shelf")
                {
                    // Shelf ids do not encode their worktree. Read ownership only when explicitly planning prune.
                    root.Git.FetchRefs(cfg.Url, ["+" + remoteRef + ":" + FetchedRef(item.Kind, item.Name)]);
                    if (root.Git.RefSha(FetchedRef(item.Kind, item.Name)) != sha)
                        throw new SgException("Backup changed while planning prune. Refresh and try again.");
                    var shelf = Shelf.Parse(item.Name, sha, root.Git.Out(null, "show", "-s", "--format=%B", sha));
                    if (shelf.IsCheckout || shelf.Branch != worktree) continue;
                }
                else if (item.Kind is not ("branch" or "wip" or "review" or "appearance") || item.Name != worktree) continue;
            }
            refs.Add(remoteRef, sha);
        }
        return new(root, cfg, worktree, refs);
    }

    public static List<string> Prune(SgRoot root, BackupPrunePlan plan)
    {
        using var operation = root.Lock();
        var cfg = Require(root);
        if (plan.Root != root.RootPath || plan.Url != cfg.Url || plan.Prefix != cfg.Prefix)
            throw new SgException("The backup destination changed. Preview prune again.");
        var remote = root.Git.LsRemote(cfg.Url);
        var here = LocalNames(root, cfg);
        foreach (var (r, sha) in plan.Refs)
        {
            var item = Owned(cfg, r) ?? throw new SgException("Unrecognized backup ref.");
            if (remote.GetValueOrDefault(r) != sha || IsPresent(here, item.Kind, item.Name))
                throw new SgException("The selected backups or local worktrees changed. Preview prune again before deleting.");
        }
        var refs = plan.Refs.Keys.ToList();
        if (refs.Count == 0) return refs;
        var answers = root.Git.PushRefs(cfg.Url, plan.Refs.Select(p => new PushRef(null, p.Key, p.Value)), force: false);
        var failed = refs.Where(r => !(answers.TryGetValue(r, out var a) && a.Ok)).ToList();
        foreach (var r in refs.Except(failed))
        {
            var item = Owned(cfg, r)!.Value;
            root.Git.DeleteRef(PushedRef(item.Kind, item.Name));
            root.Git.DeleteRef(FetchedRef(item.Kind, item.Name));
        }
        if (failed.Count > 0) throw new SgException("not deleted: " + string.Join(", ", failed));
        return refs;
    }
}
