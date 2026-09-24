using System.Text;

namespace Sg.Core.Tests;

/// <summary>
/// A bare git repository as the server, a clone of it as the checkout, and a second clone for commits
/// made by "someone else". The checkout sits inside the root unless asked otherwise, and converts line
/// endings the way a stock Git for Windows does, so every path that crosses between the store and the
/// clone is tested against a clone whose files are not byte for byte its blobs.
/// </summary>
public sealed class GitFixture : IDisposable
{
    static readonly UTF8Encoding Utf8 = new(false);

    public readonly string Base;
    public readonly string RemoteDir;
    public readonly string RootDir;
    public readonly string Checkout;
    public readonly string Other;
    public readonly CollectingLog Log = new();
    public SgRoot Root = null!;
    public CheckoutConfig Co = null!;

    public GitFixture(bool checkoutInsideRoot = true, bool autocrlf = true)
    {
        Base = Path.Combine(Path.GetTempPath(), "sgg-" + Guid.NewGuid().ToString("N")[..8]);
        RemoteDir = Path.Combine(Base, "remote.git");
        RootDir = Path.Combine(Base, "root");
        Other = Path.Combine(Base, "other");
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

        Directory.CreateDirectory(Path.GetDirectoryName(Checkout)!);
        Git(Base, "clone", "-q", RemoteDir, Checkout);
        Identify(Checkout, "Tester", "tester@example.com");
        Git(Checkout, "config", "core.autocrlf", autocrlf ? "true" : "false");
        // The clone was checked out before the setting; write its files again the way it now says.
        if (autocrlf)
        {
            Git(Checkout, "rm", "-q", "-r", "--cached", ".");
            Git(Checkout, "reset", "-q", "--hard");
        }
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
        try
        {
            foreach (var f in Directory.EnumerateFiles(Base, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { }
            Directory.Delete(Base, true);
        }
        catch { /* leftovers in temp are acceptable */ }
    }
}
