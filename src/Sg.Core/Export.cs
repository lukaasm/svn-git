using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Sg.Core;

/// <summary>One working copy an export was taken against: where it points, and what revision it was at.</summary>
public sealed class ExportWc
{
    /// <summary>"" is the checkout root. Anything else is the path of an external inside it.</summary>
    public string Rel { get; set; } = "";
    public string Url { get; set; } = "";
    public long Revision { get; set; }

    /// <summary>
    /// The repository's own id, as SVN reports it, and the path inside that repository. Together they say
    /// which repository and which folder without saying how to reach it, which is the part that differs
    /// between two machines: one reaches the server by name and the other by address, one over http and
    /// the other over https. Empty on an export written before sg recorded them.
    /// </summary>
    public string Uuid { get; set; } = "";
    public string RepoPath { get; set; } = "";

    public string Where => Rel.Length == 0 ? "root" : Rel;
}

/// <summary>
/// What an export says about itself, written as JSON beside the patches and read before anything else is
/// unpacked: a file from a newer sg is refused rather than half understood, the rule a shelf follows.
///
/// The base is named by SVN and never by a git sha. The same revision builds the same tree on both
/// machines, but not the same commit - the parent, the message and the date all differ - so a sha from
/// here means nothing there, and that is why this carries patches rather than objects.
/// </summary>
public sealed class ExportMeta
{
    public const int Current = 1;

    public int Version { get; set; } = Current;
    public string Branch { get; set; } = "";

    /// <summary>What the checkout was called where this was made. A hint for a reader; the URL is the key.</summary>
    public string Checkout { get; set; } = "";

    /// <summary>The root first, then every external the snapshot names.</summary>
    public List<ExportWc> Bases { get; set; } = new();

    /// <summary>One line per commit, oldest first, so the far side can say what is in the file without unpacking it.</summary>
    public List<string> Subjects { get; set; } = new();

    public DateTimeOffset Created { get; set; }

    /// <summary>The build that wrote it, and the machine and user it came from. For a file found months later.</summary>
    public string Sg { get; set; } = "";
    public string From { get; set; } = "";

    public ExportWc? Root => Bases.FirstOrDefault(b => b.Rel.Length == 0);
    public int Commits => Subjects.Count;
}

public sealed record ExportResult(string File, string Branch, string Checkout, int Commits, long Bytes, int Uncommitted);

/// <summary>A working copy that is not where the export was taken from: another revision, or another branch.</summary>
public sealed record ExportDrift(string Where, string Url, long Exported, long Local, string? LocalUrl = null)
{
    /// <summary>
    /// This working copy points somewhere else entirely. Then the two revisions are numbers out of two
    /// different histories and comparing them says nothing, so the URL is what gets reported.
    /// </summary>
    public bool Elsewhere => LocalUrl != null;

    /// <summary>The export names a working copy this checkout does not have at all.</summary>
    public bool Missing => Local < 0;

    public override string ToString() =>
        Elsewhere ? $"{Where}: exported from {Url}, here it is {LocalUrl}"
        : Missing ? $"{Where}: exported at r{Exported}, not here at all"
        : $"{Where}: exported at r{Exported}, here at r{Local}";
}

public sealed class ImportResult
{
    public string Branch = "";
    public string Path = "";
    public string Checkout = "";
    public int Commits;
    public int Applied;

    /// <summary>Every working copy whose revision differs from the one the export was taken at.</summary>
    public List<ExportDrift> Drift = new();

    /// <summary>The subject of the patch that would not merge, when one did not. Null when they all did.</summary>
    public string? Stopped;
    public List<string> Conflicted = new();

    /// <summary>What git said about the patch that stopped.</summary>
    public string? Why;

    /// <summary>
    /// The import is still sitting in the worktree, stopped on that patch, with the ones after it
    /// waiting behind it. Pick a version per file and carry on, or drop that one, or put it all
    /// back: Conflicts does all three, and it is the page a stopped rebase already uses.
    /// </summary>
    public bool Waiting;

    public bool Ok => Stopped == null;
}

/// <summary>
/// A branch, packed into one file that another machine can put back against a checkout of the same SVN
/// repository. The commits travel as a patch series with the blobs they start from, and the base travels
/// as SVN revisions, so the far side can say how far its own checkout has drifted and merge across it.
/// </summary>
public static class Export
{
    public const string Extension = ".sgexport";
    const string MetaEntry = "export.json";
    const string PatchPrefix = "commits/";
    const string PackEntry = "base.pack";

    /// <summary>The name to offer for a branch's file. A branch name may hold slashes; a file name may not.</summary>
    public static string SuggestName(string branch) =>
        branch.Replace('/', '-').Replace('\\', '-') + Extension;

    // ---- writing ----

    /// <summary>
    /// Packs everything the branch has that its snapshot does not. Uncommitted work is not in it - the
    /// count comes back so the caller can say so - and neither are shelves.
    /// </summary>
    public static ExportResult Write(SgRoot root, string worktree, string file)
    {
        var git = root.Git;
        var branch = git.CurrentBranch(worktree);
        var co = Ops.BaseCheckout(root, branch);
        var snapRef = root.SnapshotRef(co);
        var snapSha = git.RefSha(snapRef)
                      ?? throw new SgException($"{co.Name} has no snapshot yet, so there is nothing to export against. Run sg sync first.");

        var range = snapRef + "..refs/heads/" + branch;
        var commits = git.Log(worktree, range, 5000);
        if (commits.Count == 0)
            throw new SgException($"{branch} equals its snapshot: there are no commits of its own to export.");

        var snap = SnapshotMeta.Parse(git.Body(snapSha));
        var meta = new ExportMeta
        {
            Branch = branch,
            Checkout = co.Name,
            Bases = Bases(root, snap, co),
            // git log is newest first; a patch series is replayed oldest first, and the list is read as one.
            Subjects = commits.Select(c => c.Subject).Reverse().ToList(),
            Created = DateTimeOffset.Now,
            Sg = typeof(Export).Assembly.GetName().Version?.ToString() ?? "",
            From = Environment.MachineName + "\\" + Environment.UserName,
        };

        // On the store's volume: git packs by renaming, and a rename does not cross one.
        var temp = root.NewStoreTempDir();
        try
        {
            var patches = git.FormatPatch(worktree, range, Path.Combine(temp, "commits"));
            if (patches.Count == 0) throw new SgException("git wrote no patches for " + range);

            // The versions every change starts from. Without them the far side can only apply a patch onto
            // exactly the tree it was cut from; with them it can merge onto a checkout that has moved on.
            var pack = PackBases(root, worktree, snapRef, branch, Path.Combine(temp, "base"));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            if (File.Exists(file)) File.Delete(file);
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                Add(zip, MetaEntry, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta, SgConfig.JsonOptions)));
                foreach (var p in patches) AddFile(zip, PatchPrefix + Path.GetFileName(p), p);
                if (pack != null) AddFile(zip, PackEntry, pack);
            }

            var dirty = git.DirtyCount(worktree);
            return new ExportResult(Path.GetFullPath(file), branch, co.Name, commits.Count,
                new FileInfo(file).Length, dirty);
        }
        finally { Sweep(temp); }
    }

    /// <summary>The root and every external the snapshot names, each with the URL and revision it was at.</summary>
    static List<ExportWc> Bases(SgRoot root, SnapshotMeta snap, CheckoutConfig co)
    {
        var list = new List<ExportWc>
        {
            new() { Rel = "", Url = (snap.Url.Length > 0 ? snap.Url : co.Url).TrimEnd('/'), Revision = snap.Revision },
        };
        foreach (var (rel, rev) in snap.Externals.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            list.Add(new ExportWc
            {
                Rel = rel,
                Url = (snap.ExternalUrls.TryGetValue(rel, out var u) ? u : "").TrimEnd('/'),
                Revision = rev,
            });
        Identify(root, co, list);
        return list;
    }

    /// <summary>
    /// The repository id and the path inside it, for every working copy, read off the checkout on disk in
    /// one svn call. A URL says how this machine reaches the server, and that is the part that differs on
    /// the far side; the id and the path say which repository and which folder, and those do not.
    ///
    /// Best effort on purpose: svn not answering must not stop an export, because the URLs alone were
    /// enough before this and still are for a machine that reaches the server the same way.
    /// </summary>
    static void Identify(SgRoot root, CheckoutConfig co, List<ExportWc> list)
    {
        try
        {
            var paths = list.Select(b => b.Rel.Length == 0 ? co.Path : PathUtil.Join(co.Path, b.Rel)).ToList();
            var info = root.Svn.InfoMany(co.Path, paths, recursive: false)
                .ToDictionary(i => i.Url.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
            foreach (var b in list)
            {
                if (!info.TryGetValue(b.Url, out var one)) continue;
                b.Uuid = one.Uuid;
                var repoRoot = one.ReposRoot.TrimEnd('/');
                b.RepoPath = repoRoot.Length > 0 && b.Url.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                    ? b.Url[repoRoot.Length..].TrimStart('/')
                    : "";
            }
        }
        catch (Exception e) when (e is SgException or IOException)
        {
            root.Log.Warn("could not read the repository ids; the export will be matched by URL alone: " + e.Message);
        }
    }

    /// <summary>
    /// The blob every patch in the series starts from, packed. Commit by commit, not once across the
    /// whole branch: git merges each patch against the version that patch was cut from, and when a
    /// second commit touches a file the first one already changed, that version is an intermediate
    /// blob which is on no commit the far side will ever have. Without it git cannot three-way at
    /// all - it falls back to a plain apply, that fails against a file which has moved on, and the
    /// import stops with nothing in conflict and so nothing to resolve.
    ///
    /// A file the branch added has no version to start from and is left out. A file it renamed is
    /// looked up under the name it had. Null when the branch only ever added files.
    /// </summary>
    static string? PackBases(SgRoot root, string worktree, string snapRef, string branch, string baseName)
    {
        var git = root.Git;
        var ids = new List<string>();
        foreach (var sha in git.RevList(worktree, snapRef + "..refs/heads/" + branch))
        {
            var paths = new List<string>();
            // Renames off: a patch that will be applied again carries no rename header, so what git
            // wants by name is the old path's own blob rather than one end of a rename pair.
            foreach (var e in git.DiffNameStatus(worktree, sha + "^", sha, renames: false))
            {
                if (e.Status == 'A') continue;
                paths.Add(e.OldPath ?? e.Path);
            }
            if (paths.Count > 0) ids.AddRange(git.BlobIdsAt(sha + "^", paths));
        }
        return ids.Count == 0 ? null : git.PackObjects(ids, baseName);
    }

    static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    static void AddFile(ZipArchive zip, string name, string path)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        using var f = File.OpenRead(path);
        f.CopyTo(s);
    }

    // ---- reading ----

    /// <summary>What the file says about itself, without unpacking a single patch.</summary>
    public static ExportMeta Read(string file)
    {
        if (!File.Exists(file)) throw new SgException("no such file: " + file);
        using var zip = OpenZip(file);
        var entry = zip.GetEntry(MetaEntry)
                    ?? throw new SgException($"{Path.GetFileName(file)} is not an sg export: it has no {MetaEntry}.");
        using var s = entry.Open();
        using var r = new StreamReader(s, Encoding.UTF8);
        var meta = JsonSerializer.Deserialize<ExportMeta>(r.ReadToEnd(), SgConfig.JsonOptions)
                   ?? throw new SgException("the export names nothing: " + file);
        if (meta.Version > ExportMeta.Current)
            throw new SgException($"this export is version {meta.Version} and this sg reads {ExportMeta.Current}. Update sg, then import it again.");
        return meta;
    }

    static ZipArchive OpenZip(string file)
    {
        try { return ZipFile.OpenRead(file); }
        catch (InvalidDataException) { throw new SgException($"{Path.GetFileName(file)} is not an sg export: it is not even a zip."); }
    }

    /// <summary>
    /// A URL with everything that is only about reaching the server taken off it: the scheme, because one
    /// machine is set up for http and the other for https to the same place; a default port written out;
    /// a name before an @; a trailing slash. What is left is the host and the path.
    ///
    /// Lowercased whole. SVN paths are case-sensitive, so this is deliberately looser than the repository
    /// is - but sg has compared checkout URLs case-insensitively since before any of this, the two
    /// branches it could confuse would have to differ in nothing but case, and answering the wrong one is
    /// a thing the reader sees at once and fixes with --into. Refusing the right one is what they cannot.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var s = url.Trim().TrimEnd('/');
        if (s.Length == 0) return "";
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return s;
        var rest = s[(scheme + 3)..];
        var slash = rest.IndexOf('/');
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "" : rest[slash..];
        var at = authority.LastIndexOf('@');
        if (at >= 0) authority = authority[(at + 1)..];
        // Only a port, never the colon of an IPv6 address, which is bracketed and left alone.
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && authority.IndexOf(']') < colon && authority[(colon + 1)..].All(char.IsAsciiDigit))
        {
            var port = authority[(colon + 1)..];
            if (port is "80" or "443" or "3690" or "") authority = authority[..colon];
        }
        return (authority + path).ToLowerInvariant();
    }

    /// <summary>What a checkout is, for matching: its URL as it is, and the same URL with the reaching taken off.</summary>
    static bool SameUrl(string a, string b) =>
        a.TrimEnd('/').Equals(b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        || (a.Length > 0 && NormalizeUrl(a).Equals(NormalizeUrl(b), StringComparison.Ordinal));

    /// <summary>
    /// The checkout an export belongs to. The name it had on the other machine is a hint for a reader and
    /// never a match - the same repository is often registered under another name, and two repositories
    /// under the same one. Guessing by name would build the branch on a tree that has nothing to do with
    /// it, and every patch would merge into something enormous rather than fail.
    ///
    /// The URL is not the repository either, though, and that was the older mistake. It says how one
    /// machine reaches the server: http here and https there, a host name here and the address it
    /// resolves to there. An export of this very tool carried both spellings of one server in the same
    /// file. So the URL is tried as written, then with the reaching taken off it, then the repository's
    /// own id is asked for, and last the path inside the repository with the host ignored entirely.
    ///
    /// Every step after the first must be unambiguous. Two checkouts of one repository at one path is not
    /// a case to pick a winner in, and answering nothing is what --into is for.
    /// </summary>
    public static CheckoutConfig? MatchCheckout(SgRoot root, ExportMeta meta)
    {
        var wanted = meta.Root;
        var url = wanted?.Url.TrimEnd('/') ?? "";
        if (url.Length == 0) return null;
        var all = root.Config.Checkouts;

        var exact = all.Where(c => c.Url.TrimEnd('/').Equals(url, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];

        var same = all.Where(c => SameUrl(c.Url, url)).ToList();
        if (same.Count == 1) return same[0];

        // The repository's own id. Reading it is a local svn call on each checkout, so it is only paid for
        // when the URLs did not already answer, and a checkout that cannot be read is passed over.
        if (wanted is { Uuid.Length: > 0 })
        {
            var byId = all.Where(c => Identity(root, c) is { } id
                                      && id.Uuid.Equals(wanted.Uuid, StringComparison.OrdinalIgnoreCase)
                                      && (wanted.RepoPath.Length == 0 || id.Path.Equals(wanted.RepoPath, StringComparison.Ordinal)))
                .ToList();
            if (byId.Count == 1) return byId[0];
            if (byId.Count > 1) return null;
        }

        // Last: the path, with the host thrown away. One server answering to two names is the case this
        // catches, and requiring the whole path to match is what keeps it from catching anything else.
        var path = PathOf(url);
        if (path.Length == 0) return null;
        var byPath = all.Where(c => PathOf(c.Url).Equals(path, StringComparison.Ordinal)).ToList();
        return byPath.Count == 1 ? byPath[0] : null;
    }

    /// <summary>
    /// Why nothing matched, with what is registered written under it. The reader is the only one who
    /// knows whether two spellings are one server, and they cannot decide that from a URL they were not
    /// shown. A checkout at the same path on another host is named as the likely one.
    /// </summary>
    public static string NoMatch(SgRoot root, ExportMeta meta)
    {
        var url = meta.Root?.Url ?? "";
        var all = root.Config.Checkouts;
        if (all.Count == 0)
            return $"this root has no checkouts, so there is nothing to rebuild {meta.Branch} on. "
                   + $"Register a checkout of {url} first.";

        var path = PathOf(url);
        var near = all.Where(c => path.Length > 0 && PathOf(c.Url).Equals(path, StringComparison.Ordinal)).ToList();
        var lines = string.Join("", all.Select(c => $"\n  {c.Name}  {c.Url}"));
        var head = $"no checkout here is the repository {url} was taken from. Registered here:{lines}";
        return near.Count switch
        {
            1 => head + $"\n{near[0].Name} is at the same path on another host, so it is probably the same "
                      + $"repository reached another way. If it is: sg import <file> --into {near[0].Name}",
            > 1 => head + $"\n{near.Count} of them are at that path on another host. Pick one with --into if you know which.",
            _ => head + "\nUse --into <name> if one of them is the same repository reached another way.",
        };
    }

    static string PathOf(string url)
    {
        var n = NormalizeUrl(url);
        var slash = n.IndexOf('/');
        return slash < 0 ? "" : n[slash..];
    }

    /// <summary>The repository id and path of a registered checkout, read from the working copy on disk. Null when svn cannot say.</summary>
    static (string Uuid, string Path)? Identity(SgRoot root, CheckoutConfig co)
    {
        try
        {
            var info = root.Svn.Info(co.Path, co.Path);
            var repoRoot = info.ReposRoot.TrimEnd('/');
            var here = info.Url.TrimEnd('/');
            var rel = repoRoot.Length > 0 && here.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                ? here[repoRoot.Length..].TrimStart('/')
                : "";
            return (info.Uuid, rel);
        }
        catch (Exception e) when (e is SgException or IOException)
        {
            return null;
        }
    }

    /// <summary>How far the checkout here has moved from where the export was taken. Empty means they match.</summary>
    public static List<ExportDrift> DriftOf(SgRoot root, ExportMeta meta, CheckoutConfig co)
    {
        var sha = root.Git.RefSha(root.SnapshotRef(co));
        if (sha == null) return new List<ExportDrift>();
        var here = SnapshotMeta.Parse(root.Git.Body(sha));
        var drift = new List<ExportDrift>();
        foreach (var b in meta.Bases)
        {
            var isRoot = b.Rel.Length == 0;
            var mine = isRoot ? here.Revision : here.Externals.TryGetValue(b.Rel, out var r) ? r : -1;
            var myUrl = (isRoot ? here.Url : here.ExternalUrls.TryGetValue(b.Rel, out var u) ? u : "").TrimEnd('/');
            // A snapshot written before sg recorded external URLs has the revision and not the URL, so a
            // URL is only compared when both sides have one to compare - and compared the lenient way, or
            // http here against https there reported all seven working copies as somewhere else entirely.
            if (b.Url.Length > 0 && myUrl.Length > 0 && !SameUrl(myUrl, b.Url))
                drift.Add(new ExportDrift(b.Where, b.Url, b.Revision, mine, myUrl));
            else if (mine != b.Revision) drift.Add(new ExportDrift(b.Where, b.Url, b.Revision, mine));
        }
        return drift;
    }

    // ---- putting it back ----

    /// <summary>
    /// Makes the branch here and replays the export's commits onto it. The snapshot here is whatever this
    /// checkout last synced to, which is rarely the revision the export was taken at, so every patch is
    /// merged rather than only applied and every revision that differs is named in the answer.
    ///
    /// A patch that will not merge does not end the import: it stops it, and it is left stopped, with
    /// the files at three stages and the rest of the series queued behind it. That is the same state a
    /// rebase stops in, so the same page and the same three verbs move it on from there.
    /// </summary>
    public static ImportResult Import(SgRoot root, string file, string? asBranch = null, string? intoCheckout = null)
    {
        var meta = Read(file);
        var git = root.Git;

        var co = intoCheckout != null
            ? root.Checkout(intoCheckout)
            : MatchCheckout(root, meta) ?? throw new SgException(NoMatch(root, meta));

        var name = (asBranch ?? meta.Branch).Trim();
        if (name.Length == 0) throw new SgException("the export names no branch, so give one with --name.");
        if (git.RefSha("refs/heads/" + name) != null)
            throw new SgException($"branch exists here: {name}. Import it under another name with --name.");

        using var _ = root.Lock();

        var res = new ImportResult
        {
            Branch = name,
            Checkout = co.Name,
            Commits = meta.Commits,
            Drift = DriftOf(root, meta, co),
        };

        var temp = root.NewStoreTempDir();
        try
        {
            var patches = new List<string>();
            string? pack = null;
            using (var zip = OpenZip(file))
            {
                foreach (var e in zip.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
                {
                    if (e.FullName.StartsWith(PatchPrefix, StringComparison.Ordinal) && e.FullName.EndsWith(".patch", StringComparison.OrdinalIgnoreCase))
                        patches.Add(Extract(e, Path.Combine(temp, "commits", Path.GetFileName(e.FullName))));
                    else if (e.FullName == PackEntry)
                        pack = Extract(e, Path.Combine(temp, "base.pack"));
                }
            }
            if (patches.Count == 0) throw new SgException("the export holds no patches: " + Path.GetFileName(file));

            // Before the branch is made, so a failure here costs nothing but a temp folder.
            if (pack != null) git.UnpackObjects(pack);

            var made = Ops.Branch(root, name, co);
            res.Path = made.Path;

            // The whole series in one call. git splits it into a folder of its own before it applies
            // the first one, so the temp files below can go while the run is still stopped part way
            // through, and carrying on picks the rest of them up from there.
            var r = git.ApplyMailbox(made.Path, patches);
            res.Applied = git.CountCommits(root.SnapshotRef(co), "refs/heads/" + name);
            if (git.ReplayInProgress(made.Path) == Replay.Import)
            {
                var at = git.Progress(made.Path);
                res.Waiting = true;
                res.Stopped = at.Subject.Length > 0 ? at.Subject
                    : res.Applied < patches.Count ? SubjectOf(patches[res.Applied])
                    : "the last patch";
                res.Conflicted = git.ConflictedFiles(made.Path);
                res.Why = (r.StdErr.Trim() + "\n" + r.StdOut.Trim()).Trim();
                return res;
            }
            r.EnsureOk();
            return res;
        }
        finally { Sweep(temp); }
    }

    static string Extract(ZipArchiveEntry entry, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using var s = entry.Open();
        using var f = File.Create(to);
        s.CopyTo(f);
        return to;
    }

    /// <summary>The subject line a patch carries, for naming the one that stopped.</summary>
    static string SubjectOf(string patch)
    {
        foreach (var line in File.ReadLines(patch))
        {
            if (line.StartsWith("Subject: ", StringComparison.Ordinal)) return line[9..].Trim();
            if (line.Length == 0) break;
        }
        return Path.GetFileNameWithoutExtension(patch);
    }

    static void Sweep(string dir) => SgRoot.SweepTempDir(dir);
}
