using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Sg.Core;

/// <summary>What the build workflow stamped into build.json next to sg.exe.</summary>
public sealed class BuildStamp
{
    public long RunId { get; set; }
    public int RunNumber { get; set; }
    public string Commit { get; set; } = "";
    public string Ref { get; set; } = "";
    public string BuiltUtc { get; set; } = "";

    public string ShortCommit => Commit.Length >= 7 ? Commit[..7] : Commit;

    public override string ToString() =>
        RunNumber > 0 ? $"build {RunNumber} ({ShortCommit}, {BuiltUtc})" : "unknown build";
}

/// <summary>The local build against the newest one on GitHub.</summary>
public sealed record UpdateCheck(BuildStamp? Local, BuildStamp Remote, string DownloadUrl, bool ThroughApi, long Size)
{
    /// <summary>True when GitHub has a build the install folder does not have. No build.json counts as older.</summary>
    public bool Newer => Local == null || Remote.RunId > Local.RunId;
}

public sealed record UpdateResult(BuildStamp? Before, BuildStamp After, string InstallDir, int Files, int Retired);

/// <summary>Reads the rolling 'latest-build' release on GitHub and installs it over the folder sg.exe runs from.</summary>
public static class Updater
{
    public const string DefaultRepo = "lukaasm/svn-git";
    public const string ReleaseTag = "latest-build";
    public const string ZipAsset = "sg-win-x64.zip";
    public const string StampFile = "build.json";

    const string OldSuffix = ".sg-old";

    /// <summary>The folder sg.exe runs from. That is what an update replaces.</summary>
    public static string InstallDir()
    {
        var exe = Environment.ProcessPath;
        var dir = exe != null ? Path.GetDirectoryName(exe) : null;
        return dir is { Length: > 0 } ? dir : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static string Repo(string? explicitRepo) =>
        NonEmpty(explicitRepo) ?? NonEmpty(Environment.GetEnvironmentVariable("SG_REPO")) ?? DefaultRepo;

    static string? _ghToken;
    static DateTime _ghAskedAt = DateTime.MinValue;

    /// <summary>A private repository needs a token. A public one does not.</summary>
    public static string? Token() =>
        NonEmpty(Environment.GetEnvironmentVariable("SG_GH_TOKEN"))
        ?? NonEmpty(Environment.GetEnvironmentVariable("GH_TOKEN"))
        ?? NonEmpty(Environment.GetEnvironmentVariable("GITHUB_TOKEN"))
        ?? GhCliToken();

    /// <summary>The GitHub CLI already holds a token for this user. Borrow it, so nothing has to be configured.</summary>
    static string? GhCliToken()
    {
        // Only a token is worth remembering. Someone who runs 'gh auth login' while the app is
        // open should be picked up, so a miss is retried, at a rate that cannot spawn gh in a loop.
        if (_ghToken != null) return _ghToken;
        if (DateTime.UtcNow - _ghAskedAt < TimeSpan.FromSeconds(60)) return null;
        _ghAskedAt = DateTime.UtcNow;
        try
        {
            var psi = new ProcessStartInfo("gh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("token");
            using var p = Process.Start(psi);
            if (p == null) return null;
            var text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(TimeSpan.FromSeconds(15))) { try { p.Kill(entireProcessTree: true); } catch (Exception) { } return null; }
            if (p.ExitCode == 0) _ghToken = NonEmpty(text.Trim());
        }
        catch (Exception) { /* no gh, or it refused. Carry on without a token. */ }
        return _ghToken;
    }

    public static BuildStamp? ReadStamp(string dir)
    {
        var path = Path.Combine(dir, StampFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<BuildStamp>(File.ReadAllText(path), SgConfig.JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>Asks GitHub for the rolling release. Does not touch the install folder.</summary>
    public static UpdateCheck Check(string repo, string installDir, ILog log)
    {
        var local = ReadStamp(installDir);
        var url = $"https://api.github.com/repos/{repo}/releases/tags/{ReleaseTag}";
        using var http = NewClient();
        log.Cmd("GET " + url);
        using var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new SgException(Token() == null
                ? $"github answered 404 for {repo}. Either the build workflow has not published a '{ReleaseTag}' release yet, "
                  + "or the repository is private, which answers 404 without a token. Sign in with 'gh auth login', or set GH_TOKEN."
                : $"no '{ReleaseTag}' release in {repo}. The build workflow has not published one yet.");
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SgException($"github answered {(int)resp.StatusCode} for {repo}. "
                                  + "For a private repository put a token with 'contents: read' in GH_TOKEN.");
        if (!resp.IsSuccessStatusCode)
            throw new SgException($"github answered {(int)resp.StatusCode} for {url}");

        using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = doc.RootElement;
        var remote = ParseNotes(root.TryGetProperty("body", out var b) ? b.GetString() : null)
                     ?? throw new SgException($"the '{ReleaseTag}' release has no build stamp in its notes. "
                                              + "The build workflow writes build.json there.");

        var withToken = Token() != null;
        string? download = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), ZipAsset, StringComparison.OrdinalIgnoreCase)) continue;
                download = (withToken ? asset.GetProperty("url") : asset.GetProperty("browser_download_url")).GetString();
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                break;
            }
        if (download == null) throw new SgException($"the '{ReleaseTag}' release has no {ZipAsset} asset.");

        return new UpdateCheck(local, remote, download, withToken, size);
    }

    /// <summary>Downloads the release zip and writes it over the install folder. Replaces sg.exe while it runs.</summary>
    public static UpdateResult Apply(UpdateCheck check, string installDir, ILog log)
    {
        if (!Directory.Exists(installDir)) throw new SgException("no install folder: " + installDir);

        var temp = Path.Combine(Path.GetTempPath(), "sg-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            var zip = Path.Combine(temp, ZipAsset);
            using (var http = NewClient()) Download(http, check.DownloadUrl, check.ThroughApi, zip, check.Size, log);

            var payload = Path.Combine(temp, "payload");
            ZipFile.ExtractToDirectory(zip, payload);
            payload = Unwrap(payload);
            if (!File.Exists(Path.Combine(payload, "sg.exe")))
                throw new SgException("the downloaded build has no sg.exe. Nothing was replaced.");

            var retired = Sweep(installDir);
            var files = 0;
            foreach (var src in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(installDir, Path.GetRelativePath(payload, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest)) { Retire(dest); retired++; }
                File.Copy(src, dest, overwrite: true);
                files++;
            }
            log.Info($"wrote {files} file(s) into {installDir}");
            return new UpdateResult(check.Local, check.Remote, installDir, files, retired);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Deletes the .sg-old files an earlier update left behind. They unlock once sg restarts.</summary>
    public static int Sweep(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*" + OldSuffix, SearchOption.AllDirectories))
        {
            try { File.Delete(f); n++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>Windows refuses to overwrite a running exe but allows renaming it. That is the whole trick.</summary>
    static void Retire(string dest)
    {
        try { File.Delete(dest); return; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        for (var n = 0; n < 100; n++)
        {
            var old = dest + (n == 0 ? "" : "." + n) + OldSuffix;
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(dest, old);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        throw new SgException($"cannot replace {dest}. Close sg-ui and any running sg, then try again.");
    }

    /// <summary>A zip built with one root folder inside it still installs correctly.</summary>
    static string Unwrap(string dir)
    {
        if (Directory.EnumerateFiles(dir).Any()) return dir;
        var subs = Directory.GetDirectories(dir);
        return subs.Length == 1 ? subs[0] : dir;
    }

    /// <summary>The release notes are the build.json the workflow produced. Take the first object in them.</summary>
    public static BuildStamp? ParseNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var start = notes.IndexOf('{');
        var end = notes.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<BuildStamp>(notes[start..(end + 1)], SgConfig.JsonOptions); }
        catch (JsonException) { return null; }
    }

    static void Download(HttpClient http, string url, bool throughApi, string dest, long expected, ILog log)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (throughApi)
        {
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/octet-stream");
        }
        log.Cmd("GET " + url);
        using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
            throw new SgException($"download answered {(int)resp.StatusCode} for {url}");

        var total = resp.Content.Headers.ContentLength ?? expected;
        using var body = resp.Content.ReadAsStream();
        using var file = File.Create(dest);
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
        {
            file.Write(buffer, 0, read);
            done += read;
            log.Progress("download", done, total, "B", null);
        }
        log.ProgressEnd("download", null);
        if (total > 0 && done != total)
            throw new SgException($"download stopped at {done} of {total} bytes.");
    }

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("sg-updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = Token();
        if (token != null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
