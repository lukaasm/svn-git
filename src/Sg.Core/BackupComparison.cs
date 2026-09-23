namespace Sg.Core;

/// <summary>Immutable versions, newest commits first. Remote history omits thin marker/base commits.</summary>
public sealed record BackupHistory(string Tip, IReadOnlyList<ExportWc> Bases, IReadOnlyList<LogEntry> Commits);
public sealed record BackupComparison(string Name, string? LocalPath, BackupHistory? Local, BackupHistory Remote, DateTimeOffset Checked)
{
    internal string Root { get; init; } = "";
}

public static partial class Backup
{
    /// <summary>Fetch only the chosen version and read commit headers; no worktree scan or backup run.</summary>
    public static BackupComparison Compare(SgRoot root, BackupCatalog catalog, BackupReference item)
    {
        if (item.Kind != "branch") throw new SgException("Choose a worktree backup to compare.");
        using var operation = root.Lock();
        FetchSelection(root, catalog, item);
        var git = root.Git;
        var chain = Thin.Chain(git, item.Sha);
        if (chain.Count == 0 || chain[0].Kind != ThinKind.Marker)
            throw new SgException("This version is not an sg backup: its snapshot marker is missing.");
        if (chain[0].Version > Thin.Version) throw new SgException("This backup needs a newer sg version. Update sg before comparing.");
        var remote = new BackupHistory(item.Sha, MetaOf(item.Name, chain[0].Body).Bases,
            chain.Where(c => c.Kind == ThinKind.Change).Reverse()
                .Select(c => new LogEntry(c.Sha, c.Author.Name, c.Author.Date, c.Subject)).ToArray());
        var path = git.WorktreeList().FirstOrDefault(w => !w.Bare && w.Branch == item.Name)?.Path;
        if (path != null && !Directory.Exists(path)) path = null;
        BackupHistory? local = null;
        if (git.RefSha("refs/heads/" + item.Name) is { } tip)
        {
            var co = Ops.BaseCheckout(root, item.Name);
            var snapshot = git.RefSha(root.SnapshotRef(co));
            var basis = snapshot == null ? null : git.MergeBase(tip, snapshot);
            if (basis == null) throw new SgException("The local branch has no matching SVN snapshot to compare.");
            var output = git.Ok(null, "log", "--first-parent", "--format=%H%x1f%an%x1f%aI%x1f%s%x1e", basis + ".." + tip).StdOut;
            var commits = output.Split('\x1e').Select(r => r.TrimStart('\r', '\n').Split('\x1f', 4))
                .Where(p => p.Length == 4).Select(p => new LogEntry(p[0], p[1], p[2], Msg.Subject(p[3].TrimEnd()))).ToArray();
            local = new(tip, MetaOf(item.Name, git.Body(basis)).Bases, commits);
        }
        return new(item.Name, path, local, remote, DateTimeOffset.UtcNow) { Root = root.RootPath };
    }

    /// <summary>A pinned commit's patch, even if the branch or remote has since moved.</summary>
    public static string ComparisonPatch(SgRoot root, BackupComparison comparison, bool remote, string sha)
    {
        var history = remote ? comparison.Remote : comparison.Local;
        if (comparison.Root != root.RootPath || history?.Commits.Any(c => c.Sha == sha) != true)
            throw new SgException("Choose a commit from this comparison.");
        return root.Git.Ok(null, "show", "--first-parent", "--format=", "--no-ext-diff", "--no-textconv", "--no-color", sha, "--").StdOut;
    }
}
