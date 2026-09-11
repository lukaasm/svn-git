namespace Sg.Core;

/// <summary>The root folder: holds the shared git store in .sg, the registered checkouts, and the branch worktrees.</summary>
public sealed class SgRoot
{
    public const string SnapshotRefPrefix = "refs/remotes/svn/";
    public const string RootRef = "refs/sg/root";

    public string RootPath { get; }
    public SgConfig Config { get; }
    public ILog Log { get; }
    public Git Git { get; }
    public Svn Svn { get; }

    public string StorePath => Path.Combine(RootPath, ".sg");
    public string ConfigPath => Path.Combine(StorePath, "sg.json");
    public string ExcludePath => Path.Combine(StorePath, "info", "exclude");

    FileStream? _lock;
    int _lockDepth;

    SgRoot(string rootPath, SgConfig config, ILog log)
    {
        RootPath = rootPath;
        Config = config;
        Log = log;
        Git = new Git(config.GitExe, StorePath, log);
        Svn = new Svn(config.SvnExe, log, config.SvnMuccExe);
    }

    public static SgRoot Create(string rootPath, SgConfig config, ILog log)
    {
        Directory.CreateDirectory(Path.Combine(rootPath, ".sg"));
        var r = new SgRoot(rootPath, config, log);
        r.Save();
        return r;
    }

    public static SgRoot Open(string rootPath, ILog log)
    {
        rootPath = Path.GetFullPath(rootPath);
        var cfgPath = Path.Combine(rootPath, ".sg", "sg.json");
        if (!File.Exists(cfgPath)) throw new SgException("not an sg root: " + rootPath);
        var cfg = SgConfig.Load(cfgPath);
        cfg.Root = rootPath;
        return new SgRoot(rootPath, cfg, log);
    }

    /// <summary>Walks up from a folder. Finds the root from inside the root, a checkout, or a branch worktree.</summary>
    public static SgRoot? Find(string startDir, ILog log)
    {
        var dir = Path.GetFullPath(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, ".sg", "sg.json"))) return Open(dir, log);
            var gitFile = Path.Combine(dir, ".git");
            if (File.Exists(gitFile))
            {
                var line = File.ReadAllText(gitFile).Trim();
                if (line.StartsWith("gitdir:"))
                {
                    var gd = line[7..].Trim();
                    if (!Path.IsPathRooted(gd)) gd = Path.Combine(dir, gd);
                    var store = Path.GetFullPath(Path.Combine(gd, "..", ".."));
                    if (File.Exists(Path.Combine(store, "sg.json"))) return Open(Path.GetDirectoryName(store)!, log);
                }
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    public static SgRoot Require(string startDir, ILog log) =>
        Find(startDir, log) ?? throw new SgException($"no sg root found from {startDir}. Run 'sg init <root>' first, or run from inside a root, a checkout, or a worktree.");

    public void Save() => Config.Save(ConfigPath);

    public string SnapshotRef(CheckoutConfig c) => SnapshotRefPrefix + c.Name;

    public CheckoutConfig Checkout(string name) =>
        Config.Checkouts.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new SgException($"unknown checkout '{name}'. Known: {string.Join(", ", Config.Checkouts.Select(c => c.Name))}");

    public CheckoutConfig? CheckoutContaining(string absPath)
    {
        var p = Path.GetFullPath(absPath).TrimEnd('\\', '/');
        return Config.Checkouts
            .Where(c => p.Equals(c.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                        || p.StartsWith(c.Path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Path.Length)
            .FirstOrDefault();
    }

    public string WorktreePathFor(string branch) =>
        Path.Combine(Config.WorktreeRoot ?? RootPath, branch.Replace('/', '-').Replace('\\', '-'));

    /// <summary>One bridge operation at a time per root. Re-entrant inside one process.</summary>
    public IDisposable Lock(int timeoutSeconds = 600)
    {
        if (_lockDepth > 0)
        {
            _lockDepth++;
            return new Releaser(this);
        }
        var lockPath = Path.Combine(StorePath, "sg.lock");
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var warned = false;
        while (true)
        {
            try
            {
                _lock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _lockDepth = 1;
                return new Releaser(this);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow > deadline) throw new SgException("another sg operation holds " + lockPath);
                if (!warned) { Log.Info("waiting for " + lockPath); warned = true; }
                Thread.Sleep(500);
            }
        }
    }

    void Unlock()
    {
        if (--_lockDepth > 0) return;
        _lock?.Dispose();
        _lock = null;
    }

    sealed class Releaser(SgRoot root) : IDisposable
    {
        bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            root.Unlock();
        }
    }

    public string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "sg-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// A temp folder inside the store. git writes a pack by renaming a temp file onto its final name,
    /// and a rename does not cross volumes: with the store on a Dev Drive and TEMP on the system disk,
    /// packing into Path.GetTempPath() fails with "Improper link". Anything git writes goes here.
    /// </summary>
    public string NewStoreTempDir()
    {
        var tmp = Path.Combine(StorePath, "tmp");
        // What a killed run left behind. This folder is inside the user's store, so nothing may pile up
        // in it: an hour is long enough that no run still using one is swept out from under itself.
        try
        {
            if (Directory.Exists(tmp))
                foreach (var old in Directory.EnumerateDirectories(tmp))
                    if (Directory.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1)) SweepTempDir(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* litter is not worth failing over */ }

        var d = Path.Combine(tmp, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// Takes a temp folder away. git writes a pack and its index read only, and Directory.Delete refuses
    /// a read only file, so the attribute comes off first: without that every export left its pack in the
    /// store. A folder that still will not go is left; it is litter, not a failure worth raising.
    /// </summary>
    public static void SweepTempDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public string NewTempFile(string suffix) =>
        Path.Combine(Path.GetTempPath(), "sg-" + Guid.NewGuid().ToString("N")[..12] + suffix);

    public string IgnoresDir => Path.Combine(StorePath, "ignores");
    public string IgnoreFileFor(CheckoutConfig co) => Path.Combine(IgnoresDir, co.Name + ".gitignore");

    /// <summary>
    /// gitignore lines for one checkout: the skip list, then every svn:ignore and svn:global-ignores property
    /// in the checkout and its externals. Slow on a big checkout, it walks the svn database.
    /// </summary>
    public List<string> ComputeIgnoreLines(CheckoutConfig co)
    {
        var lines = new List<string>();
        foreach (var s in co.Skip) lines.Add("/" + s.TrimEnd('/'));
        var wcs = new List<string> { "" };
        try
        {
            wcs.AddRange(Svn.Status(co.Path, noIgnore: false).Where(e => e.Item == "external").Select(e => e.Path));
        }
        catch (SgException ex) { Log.Warn("svn status failed for " + co.Name + ": " + ex.Message); }
        // Two svn processes per working copy, each walking that copy's whole database. They run side
        // by side, because a checkout with twenty externals waited for forty of them in a row. The
        // lines still go in working copy order, so the generated file does not move between runs.
        var read = Fan.Map(wcs, wc =>
        {
            var cwd = PathUtil.Join(co.Path, wc);
            return (Ignore: Svn.PropGetRecursive(cwd, "svn:ignore"), Global: Svn.PropGetRecursive(cwd, "svn:global-ignores"));
        });
        for (var i = 0; i < wcs.Count; i++)
        {
            var wc = wcs[i];
            foreach (var (dir, val) in read[i].Ignore)
                foreach (var pat in Patterns(val)) lines.Add(Anchor(wc, dir) + Escape(pat));
            foreach (var (dir, val) in read[i].Global)
                foreach (var pat in Patterns(val)) lines.Add(Anchor(wc, dir) + "**/" + Escape(pat));
        }
        return lines.Distinct().ToList();
    }

    /// <summary>
    /// Recomputes the ignore rules of every checkout. Caches them as .sg/ignores/&lt;checkout&gt;.gitignore,
    /// the file every new worktree gets, and rewrites the shared .sg/info/exclude from all of them.
    /// </summary>
    public void RefreshExcludes()
    {
        Directory.CreateDirectory(IgnoresDir);
        foreach (var co in Config.Checkouts)
        {
            var header = $"# Generated by sg from the svn:ignore and svn:global-ignores properties of {co.Name}. Not tracked. Rebuilt by 'sg sync --ignores'.";
            File.WriteAllLines(IgnoreFileFor(co), new[] { header }.Concat(ComputeIgnoreLines(co)));
        }
        WriteSharedExclude();
        RefreshWorktreeGitignores();
    }

    /// <summary>Existing worktrees get the new rules too, but only where the .gitignore is one sg wrote.</summary>
    void RefreshWorktreeGitignores()
    {
        var coPaths = Config.Checkouts.Select(c => c.Path.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var w in Git.WorktreeList())
        {
            if (w.Bare || coPaths.Contains(w.Path.TrimEnd('\\', '/')) || !Directory.Exists(w.Path)) continue;
            var branch = w.Branch ?? Git.RebaseHeadName(w.Path);
            if (branch == null) continue;
            var baseName = Git.ConfigGet($"branch.{branch}.sgBase");
            var co = baseName != null ? Config.Checkouts.FirstOrDefault(c => c.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)) : null;
            if (co == null) continue;
            var target = Path.Combine(w.Path, ".gitignore");
            var source = IgnoreFileFor(co);
            if (!File.Exists(source)) continue;
            if (File.Exists(target))
            {
                string first;
                using (var reader = new StreamReader(target)) first = reader.ReadLine() ?? "";
                if (!first.StartsWith("# Generated by sg")) continue;
            }
            File.Copy(source, target, overwrite: true);
        }
    }

    void WriteSharedExclude()
    {
        var lines = new List<string>
        {
            "# generated by sg. Do not edit. Runs again on 'sg checkout add' and 'sg sync --ignores'.",
            ".svn/",
            "/.gitignore",
            "CLAUDE.local.md",
            ".cursor/rules/sg.mdc",
        };
        foreach (var co in Config.Checkouts)
        {
            var f = IgnoreFileFor(co);
            if (File.Exists(f)) lines.AddRange(File.ReadAllLines(f).Where(l => !l.StartsWith('#')));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(ExcludePath)!);
        File.WriteAllLines(ExcludePath, lines.Distinct());
    }

    /// <summary>Copies the cached .gitignore of a checkout into a worktree. Leaves a .gitignore that the SVN tree itself tracks alone.</summary>
    public bool WriteWorktreeGitignore(CheckoutConfig co, string worktree)
    {
        var target = Path.Combine(worktree, ".gitignore");
        var source = IgnoreFileFor(co);
        if (File.Exists(target) || !File.Exists(source)) return false;
        File.Copy(source, target);
        return true;
    }

    static IEnumerable<string> Patterns(string value) =>
        value.Split(['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0);

    /// <summary>A leading # or ! means something else in gitignore.</summary>
    static string Escape(string pattern) => pattern.Length > 0 && pattern[0] is '#' or '!' ? "\\" + pattern : pattern;

    static string Anchor(string wc, string dir)
    {
        var p = PathUtil.Rel(wc.Length == 0 ? dir : (dir.Length == 0 ? wc : wc + "/" + dir));
        return "/" + (p.Length > 0 ? p + "/" : "");
    }
}
