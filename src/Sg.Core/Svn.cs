using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Sg.Core;

public sealed class SvnInfo
{
    public string Path = "";
    public string Kind = "";
    public string Url = "";
    public string RelativeUrl = "";
    public string ReposRoot = "";
    public string Uuid = "";
    public long Revision;
    public long LastChangedRev;
    public string WcRoot = "";
    public string Schedule = "";
    public string? Checksum;
}

public sealed class SvnStatusEntry
{
    public string Path = "";
    public string Item = "";
    public string Props = "";
}

public sealed class SvnLogEntry
{
    public long Revision;
    public string Author = "";
    public string Date = "";
    public string Message = "";
}

public sealed record SvnChangedPath(string Action, string Path, string Kind, string? CopyFrom);

public sealed class SvnLogRevision
{
    public long Revision;
    public string Author = "";
    public string Date = "";
    public string Message = "";
    public List<SvnChangedPath> Paths = new();
}

public sealed class SvnUpdateResult
{
    public long? Revision;
    public int Conflicts;
    public string Output = "";
}

/// <summary>Thin wrapper over svn.exe. Paths are relative to the folder each call runs in, with forward slashes.</summary>
public sealed class Svn
{
    static readonly UTF8Encoding Utf8 = new(false);
    readonly string _exe;
    readonly string _muccExe;
    readonly ILog _log;
    readonly Dictionary<string, string> _env = new() { ["LC_ALL"] = "C", ["LANG"] = "C" };

    public Svn(string exe, ILog log, string muccExe = "svnmucc")
    {
        _exe = exe;
        _muccExe = muccExe;
        _log = log;
    }

    public ProcResult Run(string? cwd, IEnumerable<string> args, byte[]? stdin = null, string? stdoutToFile = null) =>
        Proc.Run(_exe, args.ToList(), cwd, _log, stdin, _env, stdoutToFile);

    public ProcResult Run(string? cwd, params string[] args) => Run(cwd, (IEnumerable<string>)args);
    public ProcResult Ok(string? cwd, params string[] args) => Run(cwd, args).EnsureOk();

    public string Version()
    {
        var r = Run(null, "--version", "--quiet");
        return r.Ok ? r.StdOut.Trim() : throw new SgException("svn not found: " + _exe);
    }

    string WriteTargets(IEnumerable<string> targets)
    {
        var f = Path.Combine(Path.GetTempPath(), "sg-targets-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, string.Join("\n", targets) + "\n", Utf8);
        return f;
    }

    // ---- info ----

    public SvnInfo Info(string cwd, string target) =>
        InfoMany(cwd, [target], recursive: false).FirstOrDefault()
        ?? throw new SgException($"svn info failed for {target} in {cwd}");

    /// <summary>One call for many targets. Missing targets are skipped with a warning from svn; they simply do not appear.</summary>
    public List<SvnInfo> InfoMany(string cwd, IEnumerable<string> targets, bool recursive)
    {
        var tf = WriteTargets(targets);
        try
        {
            var a = new List<string> { "info", "--xml", "--non-interactive", "--targets", tf };
            if (recursive) a.Add("-R");
            var r = Run(cwd, a);
            if (string.IsNullOrWhiteSpace(r.StdOut) || !r.StdOut.Contains("<info")) r.EnsureOk();
            return ParseInfo(r.StdOut);
        }
        finally { File.Delete(tf); }
    }

    static List<SvnInfo> ParseInfo(string xml)
    {
        var doc = XDocument.Parse(xml);
        var res = new List<SvnInfo>();
        foreach (var e in doc.Root!.Elements("entry"))
        {
            var wc = e.Element("wc-info");
            res.Add(new SvnInfo
            {
                Path = PathUtil.Rel(e.Attribute("path")?.Value ?? ""),
                Kind = e.Attribute("kind")?.Value ?? "",
                Revision = long.TryParse(e.Attribute("revision")?.Value, out var rev) ? rev : 0,
                Url = e.Element("url")?.Value ?? "",
                RelativeUrl = e.Element("relative-url")?.Value ?? "",
                ReposRoot = e.Element("repository")?.Element("root")?.Value ?? "",
                Uuid = e.Element("repository")?.Element("uuid")?.Value ?? "",
                WcRoot = wc?.Element("wcroot-abspath")?.Value ?? "",
                Schedule = wc?.Element("schedule")?.Value ?? "",
                Checksum = wc?.Element("checksum")?.Value,
                LastChangedRev = long.TryParse(e.Element("commit")?.Attribute("revision")?.Value, out var lc) ? lc : 0,
            });
        }
        return res;
    }

    // ---- status ----

    /// <summary>Status of "." including externals. Lists only non-normal items, like svn status does.</summary>
    public List<SvnStatusEntry> Status(string cwd, bool noIgnore)
    {
        var a = new List<string> { "status", "--xml", "--non-interactive" };
        if (noIgnore) a.Add("--no-ignore");
        a.Add(".");
        return ParseStatus(Ok(cwd, a.ToArray()).StdOut);
    }

    /// <summary>Status of the given paths only, no recursion.</summary>
    public List<SvnStatusEntry> StatusTargets(string cwd, IEnumerable<string> targets)
    {
        // svn status has no --targets. The lists here are short: parent folders of new files.
        var res = new List<SvnStatusEntry>();
        foreach (var chunk in targets.Chunk(100))
        {
            var a = new List<string> { "status", "--xml", "--non-interactive", "--depth", "empty" };
            a.AddRange(chunk);
            var r = Run(cwd, a);
            if (string.IsNullOrWhiteSpace(r.StdOut) || !r.StdOut.Contains("<status")) r.EnsureOk();
            res.AddRange(ParseStatus(r.StdOut));
        }
        return res;
    }

    static readonly XmlReaderSettings ReaderSettings = new()
    {
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
    };

    /// <summary>
    /// The status of a checkout, read as it arrives rather than into a document. A snapshot asks for the
    /// status of the whole tree including ignored paths, and on a monorepo this size that answer is tens of
    /// megabytes of XML; building a node for every element of it cost more than the read itself.
    /// Entries outside a target, which is where a changelist puts them, are left out as they always were.
    /// </summary>
    internal static List<SvnStatusEntry> ParseStatus(string xml)
    {
        var res = new List<SvnStatusEntry>();
        using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
        var target = "";
        var inTarget = false;
        string? path = null;
        var item = "";
        var props = "";

        void Flush()
        {
            if (path == null) return;
            var p = path;
            if (target.Length > 0 && !PathUtil.IsUnder(p, target)) p = p.Length == 0 ? target : target + "/" + p;
            res.Add(new SvnStatusEntry { Path = p, Item = item, Props = props });
            path = null;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "target":
                        Flush();
                        target = PathUtil.Rel(reader.GetAttribute("path") ?? ".");
                        inTarget = !reader.IsEmptyElement;
                        break;
                    case "entry" when inTarget:
                        Flush();
                        path = PathUtil.Rel(reader.GetAttribute("path") ?? "");
                        item = "";
                        props = "";
                        if (reader.IsEmptyElement) Flush();
                        break;
                    case "wc-status" when path != null:
                        item = reader.GetAttribute("item") ?? "";
                        props = reader.GetAttribute("props") ?? "";
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Name == "entry") Flush();
                else if (reader.Name == "target") { Flush(); inTarget = false; }
            }
        }
        Flush();
        return res;
    }

    // ---- update ----

    static readonly Regex ChangeLine = new(@"^[ADUCGER][ ADUCGERB]?\s+(\S.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Updates "." and, unless told not to, every external under it. Leaving the externals out is how
    /// a caller keeps a switched one switched: the external pass is what points it back at whatever
    /// svn:externals declares. Whoever passes true owns updating the externals afterwards.
    /// </summary>
    public SvnUpdateResult Update(string cwd, bool ignoreExternals = false) =>
        UpdateLike(cwd, "svn update", ignoreExternals
            ? ["update", "--non-interactive", "--accept", "postpone", "--ignore-externals", "."]
            : ["update", "--non-interactive", "--accept", "postpone", "."]);

    /// <summary>Switches a working copy to another URL. Externals in the same repository switch in place.</summary>
    public SvnUpdateResult Switch(string cwd, string url) =>
        UpdateLike(cwd, "svn switch", ["switch", "--non-interactive", "--accept", "postpone", url, "."]);

    /// <summary>A fresh working copy of a URL, externals included. dir is made if it is not there; its parent must be.</summary>
    public SvnUpdateResult Checkout(string url, string dir) =>
        UpdateLike(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? ".", "svn checkout",
            ["checkout", "--non-interactive", url, Path.GetFullPath(dir)]);

    SvnUpdateResult UpdateLike(string cwd, string label, string[] args)
    {
        var count = 0;
        var r = Proc.RunStreaming(_exe, args, cwd, _log, line =>
        {
            var m = ChangeLine.Match(line);
            if (!m.Success) return;
            count++;
            _log.Progress(label, count, 0, "changes", m.Groups[1].Value);
        }, null, _env);
        if (count > 0) _log.ProgressEnd(label, count + " changes");
        var res = new SvnUpdateResult { Output = r.StdOut + r.StdErr };
        var m = Regex.Match(r.StdOut, @"(?:Updated to|At|Checked out) revision (\d+)\.");
        if (m.Success) res.Revision = long.Parse(m.Groups[1].Value);
        res.Conflicts = Regex.Matches(r.StdOut, @"(?m)^\s*C\s+\S").Count;
        // E205011 = an external failed. The root update itself went through.
        if (!r.Ok && !r.StdErr.Contains("E205011")) r.EnsureOk();
        if (!r.Ok) _log.Warn("svn update: some externals failed:\n" + r.StdErr.Trim());
        return res;
    }

    // ---- changes ----

    public void Add(string cwd, IEnumerable<string> paths)
    {
        var tf = WriteTargets(paths);
        try { Ok(cwd, "add", "--parents", "--non-interactive", "--targets", tf); }
        finally { File.Delete(tf); }
    }

    public void Rm(string cwd, IEnumerable<string> paths)
    {
        var tf = WriteTargets(paths);
        try { Ok(cwd, "rm", "--force", "--non-interactive", "--targets", tf); }
        finally { File.Delete(tf); }
    }

    public void Mv(string cwd, string from, string to) =>
        Ok(cwd, "mv", "--parents", "--non-interactive", from, to);

    /// <summary>Reverts recursively. Paths that are not versioned only produce warnings.</summary>
    public ProcResult Revert(string cwd, IEnumerable<string> paths)
    {
        var tf = WriteTargets(paths);
        try { return Run(cwd, "revert", "-R", "--non-interactive", "--targets", tf); }
        finally { File.Delete(tf); }
    }

    public long Commit(string cwd, IEnumerable<string> targets, string message)
    {
        var list = targets.ToList();
        var tf = WriteTargets(list);
        var mf = Path.Combine(Path.GetTempPath(), "sg-msg-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(mf, message, Utf8);
        try
        {
            var r = Run(cwd, "commit", "--non-interactive", "--depth", "empty", "--encoding", "UTF-8", "-F", mf, "--targets", tf).EnsureOk();
            var m = Regex.Match(r.StdOut, @"Committed revision (\d+)");
            if (m.Success) return long.Parse(m.Groups[1].Value);
            return Info(cwd, list[0]).LastChangedRev;
        }
        finally
        {
            File.Delete(tf);
            File.Delete(mf);
        }
    }

    public List<SvnLogEntry> Log(string cwd, string target, long from, long to, int limit)
    {
        var r = Run(cwd, "log", "--xml", "--non-interactive", "-r", $"{from}:{to}", "-l", limit.ToString(), target);
        if (!r.Ok || !r.StdOut.Contains("<log")) return new();
        var doc = XDocument.Parse(r.StdOut);
        return doc.Root!.Elements("logentry").Select(e => new SvnLogEntry
        {
            Revision = long.TryParse(e.Attribute("revision")?.Value, out var rev) ? rev : 0,
            Author = e.Element("author")?.Value ?? "",
            Date = e.Element("date")?.Value ?? "",
            Message = e.Element("msg")?.Value ?? "",
        }).ToList();
    }

    /// <summary>
    /// The folder names directly under a repository URL, sorted. This is what turns "switch this
    /// external" from a URL to type into a branch to pick.
    /// </summary>
    public List<string> ListDirs(string url)
    {
        var r = Run(null, "ls", "--xml", "--non-interactive", url);
        if (!r.Ok || !r.StdOut.Contains("<lists")) return new();
        return XDocument.Parse(r.StdOut).Root!.Elements("list").Elements("entry")
            .Where(e => e.Attribute("kind")?.Value == "dir")
            .Select(e => e.Element("name")?.Value ?? "")
            .Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when the URL exists on the server at HEAD.</summary>
    public bool UrlExists(string url)
    {
        var r = Run(null, "info", "--xml", "--non-interactive", url);
        return r.Ok && r.StdOut.Contains("<entry");
    }

    /// <summary>One svnmucc transaction. Actions are the svnmucc words and arguments, one per list item. Returns the new revision.</summary>
    public long Mucc(IEnumerable<string> actions, string message)
    {
        var f = Path.Combine(Path.GetTempPath(), "sg-mucc-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, string.Join("\n", actions) + "\n", Utf8);
        try
        {
            var r = Proc.Run(_muccExe, ["--non-interactive", "-m", message, "-X", f], null, _log, null, _env).EnsureOk();
            var m = Regex.Match(r.StdOut + r.StdErr, @"r(\d+) committed");
            return m.Success ? long.Parse(m.Groups[1].Value) : 0;
        }
        finally { File.Delete(f); }
    }

    /// <summary>Full status below one path, recursive. Paths come back relative to cwd.</summary>
    public List<SvnStatusEntry> StatusOf(string cwd, string path)
    {
        var r = Run(cwd, "status", "--xml", "--non-interactive", path);
        if (string.IsNullOrWhiteSpace(r.StdOut) || !r.StdOut.Contains("<status")) r.EnsureOk();
        return ParseStatus(r.StdOut);
    }

    /// <summary>svn log with changed paths, newest first.</summary>
    public List<SvnLogRevision> LogVerbose(string? cwd, string target, int limit)
    {
        var r = Run(cwd, "log", "--xml", "-v", "--non-interactive", "-l", limit.ToString(), target);
        // An svn that exits 0 and writes something that is not the log is still a failure, and it has to
        // arrive as one: XDocument.Parse throws XmlException, which nothing up the stack expects to catch.
        r.EnsureOk();
        if (!r.StdOut.Contains("<log")) throw new SgException("svn log gave no XML for " + target);
        var doc = XDocument.Parse(r.StdOut);
        return doc.Root!.Elements("logentry").Select(e => new SvnLogRevision
        {
            Revision = long.TryParse(e.Attribute("revision")?.Value, out var rev) ? rev : 0,
            Author = e.Element("author")?.Value ?? "",
            Date = e.Element("date")?.Value ?? "",
            Message = e.Element("msg")?.Value ?? "",
            Paths = e.Element("paths")?.Elements("path").Select(p => new SvnChangedPath(
                p.Attribute("action")?.Value ?? "",
                p.Value,
                p.Attribute("kind")?.Value ?? "",
                p.Attribute("copyfrom-path")?.Value)).ToList() ?? new List<SvnChangedPath>(),
        }).ToList();
    }

    /// <summary>Unified diff of one revision, straight from the server.</summary>
    public string DiffRevision(string url, long revision)
    {
        var r = Run(null, "diff", "--non-interactive", "-c", revision.ToString(), url + "@" + revision);
        return r.Ok ? r.StdOut : r.StdErr;
    }

    /// <summary>Unified diff of the local changes under a working copy path.</summary>
    public string DiffLocal(string cwd, string path)
    {
        var r = Run(cwd, "diff", "--non-interactive", path);
        return r.Ok ? r.StdOut : r.StdErr;
    }

    /// <summary>svn info of a URL: last changed revision, repository root, uuid.</summary>
    public SvnInfo InfoUrl(string url) =>
        InfoMany(null, [url], recursive: false).FirstOrDefault() ?? throw new SgException("svn info failed for " + url);

    /// <summary>Text of a repository path at a revision. Empty when it is not there.</summary>
    public string CatUrl(string url, long revision)
    {
        var r = Run(null, "cat", "--non-interactive", "-r", revision.ToString(), url + "@" + revision);
        return r.Ok ? r.StdOut : "";
    }

    /// <summary>Text of the pristine (BASE) copy of a working copy file, translated like the checkout. Empty when there is none.</summary>
    public string CatBase(string cwd, string path)
    {
        var r = Run(cwd, "cat", "--non-interactive", "-r", "BASE", path);
        return r.Ok ? r.StdOut : "";
    }

    /// <summary>How many revisions touched the target from a revision up to HEAD, capped at limit.</summary>
    public int LogCount(string cwd, string target, long from, int limit)
    {
        var r = Run(cwd, "log", "--xml", "-q", "--non-interactive", "-r", $"{from}:HEAD", "-l", limit.ToString(), target);
        if (!r.Ok || !r.StdOut.Contains("<log")) return 0;
        return XDocument.Parse(r.StdOut).Root!.Elements("logentry").Count();
    }

    /// <summary>svn cat with eol and keyword translation, like a checkout would give you.</summary>
    public ProcResult CatToFile(string cwd, string path, string rev, string outFile) =>
        Run(cwd, ["cat", "--non-interactive", "-r", rev, path], null, outFile);

    /// <summary>One property of one path, or null when it is not set there.</summary>
    public string? PropGet(string cwd, string prop, string target)
    {
        var r = Run(cwd, "propget", prop, "--non-interactive", target.Length == 0 ? "." : target);
        return r.Ok && r.StdOut.Length > 0 ? r.StdOut.TrimEnd('\n', '\r') : null;
    }

    /// <summary>Sets one property on one path. This is a local change; it goes to the server on the next commit of that folder.</summary>
    public void PropSet(string cwd, string prop, string value, string target) =>
        Run(cwd, ["propset", prop, value, "--non-interactive", target.Length == 0 ? "." : target]).EnsureOk();

    /// <summary>
    /// Adds names to the svn:ignore of a folder, the way TortoiseSVN's "add to ignore list" does. The
    /// property change is itself a local change to that folder, so it shows up as one and is committed with it.
    /// </summary>
    public void AddToIgnore(string cwd, string folder, IEnumerable<string> names)
    {
        var current = PropGet(cwd, "svn:ignore", folder) ?? "";
        var lines = current.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var added = false;
        foreach (var n in names)
        {
            if (n.Length == 0 || lines.Contains(n, StringComparer.Ordinal)) continue;
            lines.Add(n);
            added = true;
        }
        if (!added) return;
        PropSet(cwd, "svn:ignore", string.Join("\n", lines) + "\n", folder);
    }

    /// <summary>
    /// Who last changed each line of a file, in file order. A line svn has never seen, because it is a
    /// local edit, is left out; the caller pairs what comes back with the file by line number.
    /// </summary>
    public List<SvnBlameLine> Blame(string cwd, string path)
    {
        var r = Run(cwd, "blame", "--xml", "--non-interactive", path);
        if (!r.Ok || !r.StdOut.Contains("<blame")) return new();
        var res = new List<SvnBlameLine>();
        var doc = XDocument.Parse(r.StdOut);
        foreach (var e in doc.Root!.Elements("target").Elements("entry"))
        {
            var n = int.TryParse(e.Attribute("line-number")?.Value, out var line) ? line : res.Count + 1;
            var commit = e.Element("commit");
            // No commit element means the line is a local edit that has never been committed.
            if (commit == null) { res.Add(new SvnBlameLine(n, 0, "", "")); continue; }
            var rev = long.TryParse(commit.Attribute("revision")?.Value, out var v) ? v : 0;
            var date = commit.Element("date")?.Value ?? "";
            res.Add(new SvnBlameLine(n, rev, commit.Element("author")?.Value ?? "", date.Length >= 10 ? date[..10] : date));
        }
        return res;
    }

    /// <summary>
    /// The same, read off the server for a URL nobody has checked out. Directories come back relative
    /// to the URL that was asked for.
    /// </summary>
    public List<(string Dir, string Value)> PropGetRecursiveUrl(string url, string prop)
    {
        var r = Run(null, "propget", prop, "--xml", "-R", "--non-interactive", url);
        if (!r.Ok || !r.StdOut.Contains("<properties")) return new();
        var doc = XDocument.Parse(r.StdOut);
        var res = new List<(string, string)>();
        var root = url.TrimEnd('/');
        foreach (var t in doc.Root!.Elements("target"))
        {
            var raw = (t.Attribute("path")?.Value ?? "").TrimEnd('/');
            // svn names each target by its full URL. What the caller wants is the folder inside the
            // one it asked about, with no slash at the front, so it reads like every other relative path here.
            var dir = raw.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? raw[root.Length..].TrimStart('/') : raw;
            foreach (var p in t.Elements("property")) res.Add((PathUtil.Rel(dir), p.Value));
        }
        return res;
    }

    /// <summary>
    /// The revisions of a source that a working copy has not merged yet, oldest first. Empty when svn
    /// has nothing to say, which is also what a source with no mergeinfo answers.
    /// </summary>
    public List<long> EligibleRevisions(string cwd, string sourceUrl, string target)
    {
        var r = Run(cwd, "mergeinfo", "--show-revs", "eligible", "--non-interactive", sourceUrl, target);
        if (!r.Ok) return new();
        var res = new List<long>();
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim().TrimStart('r');
            if (line.Length > 0 && long.TryParse(line, out var n)) res.Add(n);
        }
        return res;
    }

    /// <summary>Property values by relative directory, recursive within one working copy.</summary>
    public List<(string Dir, string Value)> PropGetRecursive(string cwd, string prop)
    {
        var r = Run(cwd, "propget", prop, "--xml", "-R", "--non-interactive", ".");
        if (!r.Ok || !r.StdOut.Contains("<properties")) return new();
        var doc = XDocument.Parse(r.StdOut);
        var res = new List<(string, string)>();
        foreach (var t in doc.Root!.Elements("target"))
        {
            // svn prints absolute folder paths here, unlike status and info.
            var raw = t.Attribute("path")?.Value ?? "";
            var dir = Path.IsPathRooted(raw) ? PathUtil.RelativeTo(cwd, raw) : PathUtil.Rel(raw);
            foreach (var p in t.Elements("property")) res.Add((dir, p.Value));
        }
        return res;
    }
}
