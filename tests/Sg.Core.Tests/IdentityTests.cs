using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Committing on a machine that has no git identity. Every "as the user" write hands git the job of
/// reading user.name and user.email, and git refuses outright when it finds neither. A fresh PC is
/// exactly that machine, and importing an exported branch is the thing you do on one, so the commit
/// has to be made rather than lost: sg's own identity stands in when there is nothing else.
/// </summary>
public sealed class IdentityTests : IDisposable
{
    readonly string _dir;
    readonly Git _git;
    readonly CollectingLog _log = new();

    public IdentityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sgid-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _git = new Git("git", _dir, _log);
        _git.Ok(_dir, "init", "-q", "-b", "main");
        _git.Config("core.autocrlf", "false");
    }

    public void Dispose()
    {
        try { DeleteTree(_dir); }
        catch (IOException) { /* the test still said what it had to say */ }
    }

    static void DeleteTree(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    void Write(string rel, string text) => File.WriteAllText(Path.Combine(_dir, rel), text);

    string Author() => _git.Out(_dir, "log", "-1", "--format=%an <%ae>");
    string Committer() => _git.Out(_dir, "log", "-1", "--format=%cn <%ce>");

    /// <summary>
    /// The machine the CI runner is, and the remote PC an archive lands on: user.name is set to nothing,
    /// so git has no ident to build and would stop with "Committer identity unknown".
    /// </summary>
    void NoIdentity()
    {
        _git.Config("user.name", "");
        _git.Config("user.email", "");
    }

    [Fact]
    public void WithNoIdentity_TheCommitIsStillMade_UnderSgsOwnName()
    {
        NoIdentity();
        Write("a.txt", "one\n");
        _git.AddPaths(_dir, new[] { "a.txt" });

        var sha = _git.CommitAsUser(_dir, "first\n");

        Assert.NotEqual("", sha);
        Assert.Equal("sg <sg@localhost>", Author());
        Assert.Equal("sg <sg@localhost>", Committer());
    }

    [Fact]
    public void WithAnIdentity_TheCommitIsTheUsers_NotSgs()
    {
        _git.Config("user.name", "Real Person");
        _git.Config("user.email", "real@localhost");
        Write("a.txt", "one\n");
        _git.AddPaths(_dir, new[] { "a.txt" });

        _git.CommitAsUser(_dir, "first\n");

        Assert.Equal("Real Person <real@localhost>", Author());
        Assert.Equal("Real Person <real@localhost>", Committer());
    }

    /// <summary>
    /// Setting the identity is what makes it appear, and it can be set after the first read. The answer
    /// is held to keep the config call off every commit, so setting it has to throw that answer away.
    /// </summary>
    [Fact]
    public void AnIdentitySetLater_IsTheOneThatIsUsed()
    {
        NoIdentity();
        Write("a.txt", "one\n");
        _git.AddPaths(_dir, new[] { "a.txt" });
        _git.CommitAsUser(_dir, "first\n");
        Assert.Equal("sg <sg@localhost>", Author());

        _git.Config("user.name", "Arrived Late");
        _git.Config("user.email", "late@localhost");
        Write("b.txt", "two\n");
        _git.AddPaths(_dir, new[] { "b.txt" });
        _git.CommitAsUser(_dir, "second\n");

        Assert.Equal("Arrived Late <late@localhost>", Author());
    }
}
