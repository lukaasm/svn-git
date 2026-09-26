using System.Security.Cryptography;
using System.Text;

namespace Sg.Core;

/// <summary>What a look at a server URL found: where its branch is now, the repository it is in, and its newest revisions.</summary>
public sealed record ServerRead(long Head, string Commit, string ReposRoot, List<LogRevision> Recent);

/// <summary>
/// The history of a server URL that nobody here has checked out, the way the project monitor reads one:
/// an SVN URL straight off the server, a git branch through a cache repository of its own. The same
/// three questions either way - what is new, what did one revision change, and what did one file hold.
/// </summary>
public interface IServerHistory
{
    CheckoutKind Kind { get; }

    ServerRead Read(string url, int limit);

    /// <summary>What one revision changed. folder narrows it to one folder of the log's paths.</summary>
    string Diff(string url, string reposRoot, LogRevision rev, string? folder);

    /// <summary>One file of the log before or after the revision. Empty when it did not exist.</summary>
    string FileAt(string url, string reposRoot, LogRevision rev, ChangedPath path, bool before);
}

public static class ServerHistory
{
    /// <summary>The history reader a URL needs: git for a git URL, svn for everything else.</summary>
    public static IServerHistory For(string url, Svn svn, string gitExe, string gitCache, ILog log) =>
        GitLocation.KindOfUrl(url) == CheckoutKind.Git ? new GitUrlHistory(gitExe, gitCache, log) : new SvnUrlHistory(svn);
}

/// <summary>An SVN URL: svn info, svn log -v, svn diff -c and svn cat, all against the server.</summary>
public sealed class SvnUrlHistory(Svn svn) : IServerHistory
{
    public CheckoutKind Kind => CheckoutKind.Svn;

    public ServerRead Read(string url, int limit)
    {
        var info = svn.InfoUrl(url);
        return new ServerRead(info.LastChangedRev, "", info.ReposRoot, svn.LogVerbose(null, url, limit));
    }

    public string Diff(string url, string reposRoot, LogRevision rev, string? folder) =>
        svn.DiffRevision(folder == null ? url : reposRoot.TrimEnd('/') + "/" + folder.TrimStart('/'), rev.Revision);

    public string FileAt(string url, string reposRoot, LogRevision rev, ChangedPath path, bool before)
    {
        if (before && path.Action == "A" && path.CopyFrom == null) return "";
        if (!before && path.Action == "D") return "";
        return svn.CatUrl(reposRoot.TrimEnd('/') + path.Path, before ? rev.Revision - 1 : rev.Revision);
    }
}

/// <summary>
/// A git branch, read through a bare repository kept for the purpose. Each repository watched is a remote
/// of it, fetched without file contents (a partial clone): the history is small, and the few files a diff
/// is asked for come over when git first needs them. A server that does not do partial clones gets a
/// plain fetch instead, which git falls back to by itself. Revision is the branch's first-parent height,
/// so newer is larger the way it is for an SVN revision, and the unread count works unchanged.
/// </summary>
public sealed class GitUrlHistory(string gitExe, string cacheDir, ILog log) : IServerHistory
{
    public CheckoutKind Kind => CheckoutKind.Git;

    readonly GitRepo _cache = new(gitExe, cacheDir, log);

    /// <summary>The monitor reads several repositories side by side, and they share this one cache: its config and its refs take one writer at a time.</summary>
    static readonly object Gate = new();

    /// <summary>A remote name for a repository URL: short, stable, and free of anything a remote name cannot hold.</summary>
    public static string KeyOf(string repoUrl) =>
        "m" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Export.NormalizeUrl(repoUrl))))[..16].ToLowerInvariant();

    void Ensure()
    {
        if (File.Exists(Path.Combine(cacheDir, "HEAD"))) return;
        Directory.CreateDirectory(cacheDir);
        _cache.Run(["init", "--bare", "-q", "."], env: Git.InitFormat).EnsureOk();
    }

    /// <summary>The branch the URL names, fetched fresh; the default branch when it names none.</summary>
    string Fetch(string url)
    {
        Ensure();
        var (repoUrl, branch) = GitLocation.Parse(url);
        var key = KeyOf(repoUrl);
        if (_cache.ConfigGet($"remote.{key}.url") != repoUrl)
        {
            _cache.Run("remote", "remove", key);
            _cache.Ok("remote", "add", "--no-tags", key, repoUrl);
            _cache.Ok("config", $"remote.{key}.promisor", "true");
            _cache.Ok("config", $"remote.{key}.partialclonefilter", "blob:none");
        }
        branch ??= DefaultBranch(repoUrl);
        var r = _cache.Run("fetch", "--quiet", "--no-tags", "--filter=blob:none", key, $"+refs/heads/{branch}:refs/remotes/{key}/{branch}");
        if (!r.Ok) throw new SgException($"git fetch {repoUrl} {branch} failed: " + r.StdErr.Trim());
        return $"refs/remotes/{key}/{branch}";
    }

    string DefaultBranch(string repoUrl)
    {
        var r = _cache.Run("ls-remote", "--symref", repoUrl, "HEAD");
        if (!r.Ok) throw new SgException($"git ls-remote {repoUrl} failed: " + r.StdErr.Trim());
        foreach (var line in r.StdOut.Split('\n'))
            if (line.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                return line[16..].Split('\t')[0].Trim();
        throw new SgException($"{repoUrl} names no default branch. Give one after a #, like {repoUrl}#main");
    }

    public ServerRead Read(string url, int limit)
    {
        string tip;
        lock (Gate) tip = _cache.Rev(Fetch(url)) ?? throw new SgException("nothing was fetched from " + url);
        var height = _cache.Height(tip);
        var log = _cache.Log(["--first-parent", "-n", limit.ToString(System.Globalization.CultureInfo.InvariantCulture), tip], renames: false);
        for (var i = 0; i < log.Count; i++) log[i].Revision = height - i;
        return new ServerRead(height, tip, GitLocation.Parse(url).Url, log);
    }

    public string Diff(string url, string reposRoot, LogRevision rev, string? folder) => _cache.CommitDiff(rev.Commit, folder);

    public string FileAt(string url, string reposRoot, LogRevision rev, ChangedPath path, bool before) => _cache.FileAt(rev, path, before);
}
