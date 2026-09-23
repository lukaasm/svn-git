namespace Sg.Core;

/// <summary>Names and exact advertised versions; reading this catalog downloads no backup histories.</summary>
public sealed record BackupReference(string Kind, string Name, string Sha, bool HasWip, bool ExistsHere, bool Excluded);

public sealed class BackupCatalog
{
    public IReadOnlyList<BackupReference> Items { get; }
    internal string Root { get; }
    internal string Url { get; }
    internal string Prefix { get; }
    internal IReadOnlyDictionary<string, string> Refs { get; }
    internal BackupCatalog(string root, string url, string prefix, IReadOnlyDictionary<string, string> refs, IReadOnlyList<BackupReference> items)
        => (Root, Url, Prefix, Refs, Items) = (root, url, prefix, refs, items);
}

/// <summary>The selected version, including the refs a subsequent restore must still match.</summary>
public sealed record BackupPreview(BackupEntry Entry, IReadOnlyDictionary<string, string> ExpectedRefs);

public static partial class Backup
{
    public static BackupCatalog Browse(SgRoot root)
    {
        var cfg = Require(root);
        var remote = root.Git.LsRemote(cfg.Url);
        var here = LocalNames(root);
        var items = remote.Where(kv => Owned(cfg, kv.Key) != null).Select(kv =>
        {
            var (kind, name) = Owned(cfg, kv.Key)!.Value;
            return new BackupReference(kind, name, kv.Value,
                kind == "branch" && remote.ContainsKey(RemoteRef(cfg, "wip", name)),
                kind is "branch" or "wip" ? here.Branches.Contains(name) : kind == "edits" ? here.Checkouts.Contains(name) : here.Shelves.Contains(name),
                kind is "branch" or "wip" && IsExcluded(cfg, name));
        }).OrderBy(e => e.Kind switch { "branch" => 0, "wip" => 1, "edits" => 2, _ => 3 })
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(root.RootPath, cfg.Url, cfg.Prefix, remote, items);
    }

    public static BackupPreview Preview(SgRoot root, BackupCatalog catalog, BackupReference item)
    {
        var cfg = Require(root);
        if (catalog.Root != root.RootPath || catalog.Url != cfg.Url || catalog.Prefix != cfg.Prefix || !catalog.Items.Contains(item))
            throw new SgException("The backup destination changed. Refresh the backup list before opening a preview.");
        using var operation = root.Lock();
        var remoteRef = RemoteRef(cfg, item.Kind, item.Name);
        var localRef = FetchedRef(item.Kind, item.Name);
        root.Git.FetchRefs(cfg.Url, ["+" + remoteRef + ":" + localRef]);
        if (root.Git.RefSha(localRef) != item.Sha)
            throw new SgException("This backup changed since the list was read. Refresh the list to preview its current version.");
        var entry = new BackupEntry { Kind = item.Kind, Name = item.Name, Sha = item.Sha };
        var expected = new Dictionary<string, string>(StringComparer.Ordinal) { [remoteRef] = item.Sha };
        var wipRef = RemoteRef(cfg, "wip", item.Name);
        if (item.Kind == "branch" && catalog.Refs.TryGetValue(wipRef, out var wipSha)) expected[wipRef] = wipSha;
        ReadEntry(root, cfg, LocalNames(root), item.HasWip ? new(StringComparer.Ordinal) { item.Name } : new(StringComparer.Ordinal), entry);
        return new(entry, expected);
    }
}
