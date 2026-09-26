using System.Text;

namespace Sg.Core.Tests;

/// <summary>
/// A bare git repository as the server, a clone of it as the checkout, and a second clone for commits
/// made by "someone else". The checkout sits inside the root unless asked otherwise, and converts line
/// endings the way a stock Git for Windows does, so every path that crosses between the store and the
/// clone is tested against a clone whose files are not byte for byte its blobs.
///
/// With submodules, the app pins a second repository, lib, at libs/core; nested, lib pins a third, z,
/// at vendor/z. Each has its own bare server and its own "someone else" clone.
/// </summary>
public sealed class GitFixture : IDisposable
{
    static readonly UTF8Encoding Utf8 = new(false);

    static GitFixture()
    {
        // Git clones a submodule from a local path only when told it may: these servers are folders.
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
    }

    /// <summary>Where the submodule sits in the app, and where lib's own submodule sits in lib.</summary>
    public const string Sub = "libs/core";
    public const string Nested = "libs/core/vendor/z";
    public readonly string LibRemote;
    public readonly string LibOther;
    public readonly string ZRemote;
    public readonly string ZOther;

    public readonly string Base;
    public readonly string RemoteDir;
    public readonly string RootDir;
    public readonly string Checkout;
    public readonly string Other;
    public readonly CollectingLog Log = new();
    public SgRoot Root = null!;
    public CheckoutConfig Co = null!;

    public GitFixture(bool checkoutInsideRoot = true, bool autocrlf = true, bool submodule = false, bool nested = false)
    {
        Base = Path.Combine(Path.GetTempPath(), "sgg-" + Guid.NewGuid().ToString("N")[..8]);
        RemoteDir = Path.Combine(Base, "remote.git");
        RootDir = Path.Combine(Base, "root");
        Other = Path.Combine(Base, "other");
        LibRemote = Path.Combine(Base, "lib.git");
        LibOther = Path.Combine(Base, "libother");
        ZRemote = Path.Combine(Base, "z.git");
        ZOther = Path.Combine(Base, "zother");
        Checkout = checkoutInsideRoot ? Path.Combine(RootDir, "app") : Path.Combine(Base, "elsewhere", "app");
        Directory.CreateDirectory(Base);
        Directory.CreateDirectory(RootDir);

        Git(Base, "init", "-q", "--bare", "-b", "main", RemoteDir);
        Git(Base, "init", "-q", "-b", "main", Other);
        Identify(Other, "Someone Else", "else@example.com");
        Git(Other, "config", "core.autocrlf", "false");
        Put(Other, "README.md", "hello\n");
        Put(Other, "src/app.cpp", "int app = 1;\n");
        Put(Other, "src/lib/util.cpp", "int util = 1;\n");
        Put(Other, "docs/guide.txt", "line1\nline2\nline3\n");
        var bytes = new byte[50_000];
        new Random(2).NextBytes(bytes);
        File.WriteAllBytes(Ensure(Other, "assets/big/blob.bin"), bytes);
        Git(Other, "add", "-A");
        Git(Other, "commit", "-q", "-m", "initial content");
        Git(Other, "remote", "add", "origin", RemoteDir);
        Git(Other, "push", "-q", "-u", "origin", "main");
        if (submodule || nested) AddSubmodules(nested);

        Directory.CreateDirectory(Path.GetDirectoryName(Checkout)!);
        Git(Base, "clone", "-q", "--recurse-submodules", RemoteDir, Checkout);
        Identify(Checkout, "Tester", "tester@example.com");
        // A submodule has a config of its own. Line endings are set the same as the clone's, the way a
        // global setting sets both: the store reads and writes a submodule's files the clone's way.
        var subs = nested ? new[] { Sub, Nested } : submodule ? new[] { Sub } : [];
        foreach (var dir in subs.Select(s => Path.Combine(Checkout, s.Replace('/', Path.DirectorySeparatorChar))))
        {
            Identify(dir, "Tester", "tester@example.com");
            Git(dir, "config", "core.autocrlf", autocrlf ? "true" : "false");
            if (!autocrlf) continue;
            Git(dir, "rm", "-q", "-r", "--cached", ".");
            Git(dir, "reset", "-q", "--hard");
        }
        Git(Checkout, "config", "core.autocrlf", autocrlf ? "true" : "false");
        // The clone was checked out before the setting; write its files again the way it now says.
        if (autocrlf)
        {
            Git(Checkout, "rm", "-q", "-r", "--cached", ".");
            Git(Checkout, "reset", "-q", "--hard");
        }
    }

    /// <summary>lib, and z inside it when nested, each a server of its own, pinned by the app at libs/core.</summary>
    void AddSubmodules(bool nested)
    {
        NewServer(LibRemote, LibOther, ("lib.h", "int lib = 1;\n"), ("src/core.c", "int core = 1;\n"));
        if (nested)
        {
            NewServer(ZRemote, ZOther, ("z.h", "int z = 1;\n"));
            Git(LibOther, "submodule", "add", "-q", ZRemote, "vendor/z");
            Git(LibOther, "commit", "-q", "-m", "pin z");
            Git(LibOther, "push", "-q", "origin", "main");
        }
        Git(Other, "submodule", "add", "-q", LibRemote, Sub);
        if (nested) Git(Other, "submodule", "update", "-q", "--init", "--recursive");
        Git(Other, "commit", "-q", "-m", "pin lib");
        Git(Other, "push", "-q", "origin", "main");
    }

    static void NewServer(string remote, string other, params (string Rel, string Content)[] files)
    {
        Git(Path.GetDirectoryName(remote)!, "init", "-q", "--bare", "-b", "main", remote);
        Git(Path.GetDirectoryName(other)!, "init", "-q", "-b", "main", other);
        Identify(other, "Someone Else", "else@example.com");
        Git(other, "config", "core.autocrlf", "false");
        foreach (var (rel, content) in files) Put(other, rel, content);
        Git(other, "add", "-A");
        Git(other, "commit", "-q", "-m", "initial content");
        Git(other, "remote", "add", "origin", remote);
        Git(other, "push", "-q", "-u", "origin", "main");
    }

    /// <summary>A commit someone else pushes to lib, or to z, without moving any pin.</summary>
    public static string ServerCommit(string other, string rel, string content, string message, string branch = "main")
    {
        Git(other, "fetch", "-q", "origin");
        Git(other, "checkout", "-q", "-B", branch, Git(other, "branch", "-r", "--list", "origin/" + branch).Length > 0 ? "origin/" + branch : "HEAD");
        Put(other, rel, content);
        Git(other, "add", "-A");
        Git(other, "commit", "-q", "-m", message);
        Git(other, "push", "-q", "-u", "origin", branch);
        return Git(other, "rev-parse", "HEAD");
    }

    /// <summary>Someone else moves the app's pin of lib to lib's newest main, and pushes that.</summary>
    public string BumpPin(string message)
    {
        Git(Other, "fetch", "-q", "origin");
        Git(Other, "checkout", "-q", "main");
        Git(Other, "reset", "-q", "--hard", "origin/main");
        var sub = Path.Combine(Other, Sub.Replace('/', Path.DirectorySeparatorChar));
        Git(sub, "fetch", "-q", "origin");
        Git(sub, "checkout", "-q", "--detach", "origin/main");
        Git(Other, "add", Sub);
        Git(Other, "commit", "-q", "-m", message);
        Git(Other, "push", "-q", "origin", "main");
        return Git(Other, "rev-parse", "HEAD");
    }

    public string LibHead(string branch = "main") => Git(LibRemote, "rev-parse", "refs/heads/" + branch);
    public string LibShow(string rev, string path) => Git(LibRemote, "show", rev + ":" + path);

    /// <summary>The commit a commit of a server pins at a path.</summary>
    public static string PinIn(string remote, string rev, string path)
    {
        var line = Git(remote, "ls-tree", rev, "--", path);
        var parts = line.Split('\t')[0].Split(' ');
        return parts[0] == "160000" ? parts[2] : "";
    }

    public static void Identify(string repo, string name, string email)
    {
        Git(repo, "config", "user.name", name);
        Git(repo, "config", "user.email", email);
        Git(repo, "config", "commit.gpgsign", "false");
    }

    /// <summary>Init the root and register the clone, with a folder the worktrees share the way a big asset folder would be.</summary>
    public void Setup()
    {
        Root = Ops.Init(RootDir, Log, fsmonitor: false);
        Co = Ops.CheckoutAdd(Root, Checkout, skip: ["assets/big"], junctions: ["assets/big"]).Checkout;
    }

    public static string Git(string cwd, params string[] args)
    {
        var r = Proc.Run("git", new[] { "-C", cwd }.Concat(args).ToList(), cwd, new NullLog(), null,
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" });
        return r.EnsureOk().StdOut.Trim();
    }

    static string Ensure(string dir, string rel)
    {
        var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return p;
    }

    public static void Put(string dir, string rel, string content) => File.WriteAllText(Ensure(dir, rel), content, Utf8);

    public static string Read(string dir, string rel) =>
        File.ReadAllText(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

    public static byte[] Bytes(string dir, string rel) => File.ReadAllBytes(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>A commit someone else pushes to a branch of the server.</summary>
    public string OtherCommit(string rel, string? content, string message, string branch = "main")
    {
        Git(Other, "fetch", "-q", "origin");
        if (Git(Other, "branch", "--list", branch).Length == 0) Git(Other, "checkout", "-q", "-b", branch, "origin/" + branch);
        else
        {
            Git(Other, "checkout", "-q", branch);
            Git(Other, "reset", "-q", "--hard", "origin/" + branch);
        }
        if (content == null) Git(Other, "rm", "-q", rel);
        else
        {
            Put(Other, rel, content);
            Git(Other, "add", "-A");
        }
        Git(Other, "commit", "-q", "-m", message);
        Git(Other, "push", "-q", "origin", branch);
        return Git(Other, "rev-parse", "HEAD");
    }

    /// <summary>A new branch on the server, cut from main, with one commit of its own.</summary>
    public string OtherBranch(string branch, string rel, string content, string message)
    {
        Git(Other, "fetch", "-q", "origin");
        Git(Other, "checkout", "-q", "-B", branch, "origin/main");
        Put(Other, rel, content);
        Git(Other, "add", "-A");
        Git(Other, "commit", "-q", "-m", message);
        Git(Other, "push", "-q", "-u", "origin", branch);
        var sha = Git(Other, "rev-parse", "HEAD");
        Git(Other, "checkout", "-q", "main");
        return sha;
    }

    public string RemoteHead(string branch = "main") => Git(RemoteDir, "rev-parse", "refs/heads/" + branch);
    public string RemoteShow(string rev, string path) => Git(RemoteDir, "show", rev + ":" + path);
    public bool RemoteHas(string rev, string path) => Proc.Run("git", ["-C", RemoteDir, "cat-file", "-e", rev + ":" + path], RemoteDir, new NullLog()).Ok;
    public string ParentOf(string rev) => Git(RemoteDir, "rev-parse", rev + "^");
    public string MessageOf(string rev) => Git(RemoteDir, "log", "-1", "--format=%B", rev);
    public string AuthorOf(string rev) => Git(RemoteDir, "log", "-1", "--format=%an", rev);

    /// <summary>The clone's own status, the way its owner would read it: what is really changed, not what was only touched.</summary>
    public string CloneChanges()
    {
        Git(Checkout, "update-index", "-q", "--refresh");
        return Git(Checkout, "status", "--porcelain");
    }

    public void Dispose()
    {
        Profile.Write(Base, Log);
        try
        {
            foreach (var f in Directory.EnumerateFiles(Base, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            Directory.Delete(Base, true);
        }
        catch { /* leftovers in temp are acceptable */ }
    }
}
