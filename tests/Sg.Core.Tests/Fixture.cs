using System.Text;

namespace Sg.Core.Tests;

/// <summary>
/// Three local SVN repositories shaped like the real monorepo:
/// mono/trunk has externals into engine/branches/fort/dev (as schmetterling) and game/branches/fort/{dev,builds,tools} (under fort/).
/// </summary>
public sealed class Fixture : IDisposable
{
    static readonly UTF8Encoding Utf8 = new(false);

    public readonly string Base;
    public readonly string ReposDir;
    public readonly string RootDir;
    public readonly string Checkout;
    public readonly string EngineUrl;
    public readonly string GameUrl;
    public readonly string MonoUrl;
    public readonly CollectingLog Log = new();
    public readonly Svn Svn;
    public SgRoot Root = null!;
    public CheckoutConfig Co = null!;

    public Fixture()
    {
        Base = Path.Combine(Path.GetTempPath(), "sgt-" + Guid.NewGuid().ToString("N")[..8]);
        ReposDir = Path.Combine(Base, "repos");
        RootDir = Path.Combine(Base, "fort");
        Directory.CreateDirectory(ReposDir);
        Directory.CreateDirectory(RootDir);
        Svn = new Svn("svn", Log);

        EngineUrl = CreateRepo("engine");
        GameUrl = CreateRepo("game");
        MonoUrl = CreateRepo("mono");

        Populate(EngineUrl, wc =>
        {
            Put(wc, "trunk/dev/engine.cpp", "int engine = 1;\n");
            Put(wc, "trunk/dev/sub/data.txt", "engine data\n");
            Put(wc, "trunk/docs/readme.txt", "docs\n");
        });
        Copy(EngineUrl + "/trunk", EngineUrl + "/branches/fort");

        // game/builds carries a nested external into a folder that the engine external already covers, like the real repo does
        Populate(GameUrl, wc =>
        {
            Put(wc, "trunk/dev/game.cpp", "int game = 1;\n");
            var bytes = new byte[100_000];
            new Random(1).NextBytes(bytes);
            File.WriteAllBytes(Ensure(wc, "trunk/builds/big.bin"), bytes);
            Put(wc, "trunk/tools/tool.py", "print(1)\n");
        }, wc => PropSet(wc, "trunk/builds", "svn:externals", EngineUrl + "/branches/fort/dev/sub data_engine\n"));
        Copy(GameUrl + "/trunk", GameUrl + "/branches/fort");

        Populate(MonoUrl, wc =>
        {
            Put(wc, "trunk/CMakeLists.txt", "project(fort)\n");
            Put(wc, "trunk/fort/.keep", "");
            Put(wc, "trunk/build/.keep", "");
            Put(wc, "trunk/src/.keep", "");
        }, wc =>
        {
            PropSet(wc, "trunk", "svn:externals", EngineUrl + "/branches/fort/dev schmetterling\n");
            PropSet(wc, "trunk/fort", "svn:externals",
                GameUrl + "/branches/fort/dev dev\n" + GameUrl + "/branches/fort/builds builds\n" + GameUrl + "/branches/fort/tools tools\n");
            PropSet(wc, "trunk/build", "svn:ignore", "win_vc17\n");
        });

        Checkout = Path.Combine(RootDir, "mono");
        Svn.Ok(null, "checkout", "--non-interactive", MonoUrl + "/trunk", Checkout);
    }

    /// <summary>Init the root and register the checkout the way the real one will be registered.</summary>
    public void Setup()
    {
        Root = Ops.Init(RootDir, Log, fsmonitor: false);
        Co = Ops.CheckoutAdd(Root, Checkout, skip: ["fort/builds"], junctions: ["fort/builds"], optional: ["fort/tools"]).Checkout;
    }

    string CreateRepo(string name)
    {
        var dir = Path.Combine(ReposDir, name);
        Proc.Run("svnadmin", ["create", dir], null, Log).EnsureOk();
        return "file:///" + dir.Replace('\\', '/');
    }

    void Populate(string url, Action<string> files, Action<string>? afterAdd = null)
    {
        var wc = Path.Combine(Base, "wc-" + Guid.NewGuid().ToString("N")[..6]);
        Svn.Ok(null, "checkout", "--non-interactive", url, wc);
        files(wc);
        foreach (var top in Directory.GetFileSystemEntries(wc).Where(p => Path.GetFileName(p) != ".svn"))
            Svn.Ok(wc, "add", "--non-interactive", Path.GetFileName(top));
        afterAdd?.Invoke(wc);
        Svn.Ok(wc, "commit", "--non-interactive", "-m", "initial content");
    }

    void Copy(string from, string to) =>
        Svn.Ok(null, "copy", "--parents", "--non-interactive", "-m", "branch", from, to);

    static string Ensure(string wc, string rel)
    {
        var p = Path.Combine(wc, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return p;
    }

    public static void Put(string wc, string rel, string content) => File.WriteAllText(Ensure(wc, rel), content, Utf8);

    void PropSet(string wc, string rel, string name, string value)
    {
        var f = Path.Combine(Base, "prop-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(f, value, Utf8);
        Svn.Ok(wc, "propset", name, "-F", f, rel.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(f);
    }

    public string Cat(string url) => Svn.Ok(null, "cat", "--non-interactive", url).StdOut;
    public string PropGet(string url, string prop) => Svn.Ok(null, "propget", prop, "--non-interactive", url).StdOut;
    public long Revision(string url) => Svn.Info(Base, url).Revision;
    public string Ls(string url) => Svn.Ok(null, "ls", "--non-interactive", url).StdOut;
    public string LogVerbose(string url) => Svn.Ok(null, "log", "-v", "--xml", "--non-interactive", url).StdOut;

    /// <summary>A second working copy, for commits made by "someone else".</summary>
    public string OtherWc(string url)
    {
        var wc = Path.Combine(Base, "other-" + Guid.NewGuid().ToString("N")[..6]);
        Svn.Ok(null, "checkout", "--non-interactive", url, wc);
        return wc;
    }

    public void SetPreCommitHook(string repo, bool reject)
    {
        var hook = Path.Combine(ReposDir, repo, "hooks", "pre-commit.bat");
        if (reject) File.WriteAllText(hook, "@echo off\r\necho rejected by the test hook 1>&2\r\nexit 1\r\n");
        else if (File.Exists(hook)) File.Delete(hook);
    }

    public IEnumerable<SvnStatusEntry> CheckoutChanges() =>
        Svn.Status(Checkout, noIgnore: false).Where(e => e.Path.Length > 0 && e.Item is not ("external" or "unversioned" or "ignored"));

    public void Dispose()
    {
        try { Directory.Delete(Base, true); }
        catch { /* leftovers in temp are acceptable */ }
    }
}
