namespace Sg.Core;

/// <summary>Names and exact advertised versions; reading this catalog downloads no backup histories.</summary>
public sealed record BackupReference(string Kind, string Name, string Sha, bool HasWip, bool ExistsHere, bool Excluded)
{
    public bool HasReview { get; init; }
    public bool HasAppearance { get; init; }
}

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
public sealed record BackupPreview(BackupEntry Entry, IReadOnlyDictionary<string, string> ExpectedRefs)
{
    /// <summary>Validated image bytes for an in-memory preview; Entry.HasAppearance distinguishes saved initials from no appearance.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? AppearanceIcon { get; init; }
}

public static partial class Backup
{
    public static BackupCatalog Browse(SgRoot root)
    {
        using var reading = root.Git.Reading();
        var cfg = Require(root);
        var remote = root.Git.LsRemote(cfg.Url);
        var here = LocalNames(root);
        var items = remote.Where(kv => Owned(cfg, kv.Key) is { Kind: not ("review" or "appearance") }).Select(kv =>
        {
            var (kind, name) = Owned(cfg, kv.Key)!.Value;
            return new BackupReference(kind, name, kv.Value,
                kind == "branch" && remote.ContainsKey(RemoteRef(cfg, "wip", name)),
                kind is "branch" or "wip" ? here.Branches.Contains(name) : kind == "edits" ? here.Checkouts.Contains(name) : here.Shelves.Contains(name),
                kind is "branch" or "wip" && IsExcluded(cfg, name)) {
                    HasReview = kind == "branch" && remote.ContainsKey(RemoteRef(cfg, "review", name)),
                    HasAppearance = kind == "branch" && remote.ContainsKey(RemoteRef(cfg, "appearance", name)) };
        }).OrderBy(e => e.Kind switch { "branch" => 0, "wip" => 1, "edits" => 2, _ => 3 })
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(root.RootPath, cfg.Url, cfg.Prefix, remote, items);
    }

    public static BackupPreview Preview(SgRoot root, BackupCatalog catalog, BackupReference item)
    {
        using var operation = root.Lock();
        var cfg = FetchSelection(root, catalog, item);
        var remoteRef = RemoteRef(cfg, item.Kind, item.Name);
        var entry = new BackupEntry { Kind = item.Kind, Name = item.Name, Sha = item.Sha, HasReview = item.HasReview, HasAppearance = item.HasAppearance };
        var expected = new Dictionary<string, string>(StringComparer.Ordinal) { [remoteRef] = item.Sha };
        var wipRef = RemoteRef(cfg, "wip", item.Name);
        if (item.Kind == "branch" && catalog.Refs.TryGetValue(wipRef, out var wipSha)) expected[wipRef] = wipSha;
        if (item.Kind == "branch" && catalog.Refs.TryGetValue(RemoteRef(cfg, "review", item.Name), out var reviewSha)) expected[RemoteRef(cfg, "review", item.Name)] = reviewSha;
        if (item.Kind == "branch" && catalog.Refs.TryGetValue(RemoteRef(cfg, "appearance", item.Name), out var appearanceSha)) expected[RemoteRef(cfg, "appearance", item.Name)] = appearanceSha;
        ReadEntry(root, cfg, LocalNames(root), item.HasWip ? new(StringComparer.Ordinal) { item.Name } : new(StringComparer.Ordinal), entry);
        var appearance = item.HasAppearance ? FetchAppearance(root, cfg, item.Name, catalog.Refs) : null;
        return new(entry, expected) { AppearanceIcon = appearance?.Icon };
    }

    // Callers hold the root lock so validation and the fetched version belong to the same destination.
    static BackupConfig FetchSelection(SgRoot root, BackupCatalog catalog, BackupReference item)
    {
        var cfg = Require(root);
        if (catalog.Root != root.RootPath || catalog.Url != cfg.Url || catalog.Prefix != cfg.Prefix || !catalog.Items.Contains(item))
            throw new SgException("The backup destination changed. Refresh the backup list before opening a preview.");
        var remoteRef = RemoteRef(cfg, item.Kind, item.Name);
        var localRef = FetchedRef(item.Kind, item.Name);
        root.Git.FetchRefs(cfg.Url, ["+" + remoteRef + ":" + localRef]);
        if (root.Git.RefSha(localRef) != item.Sha)
            throw new SgException("This backup changed since the list was read. Refresh the list to preview its current version.");
        return cfg;
    }
}
