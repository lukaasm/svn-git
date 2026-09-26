using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Picking a side for a file one side deleted. git's own "checkout --theirs" refuses a file the other
/// side does not have, so Accept incoming on "deleted by them" failed with a git error instead of
/// deleting the file, which is what that side says to do. A real git, a real rebase, both shapes.
/// </summary>
public sealed class TakeSideTests : IDisposable
{
    readonly string _dir;
    readonly Git _git;
    readonly CollectingLog _log = new();

    public TakeSideTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sgts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _git = new Git("git", _dir, _log);
        _git.Ok(_dir, "init", "-q", "-b", "main");
        _git.Config("user.name", "Test");
        _git.Config("user.email", "test@localhost");
        _git.Config("core.autocrlf", "false");
        Write("keep.txt", "unrelated\n");
        Write("f.txt", "one\n");
        _git.Ok(_dir, "add", "-A");
        _git.Ok(_dir, "commit", "-q", "-m", "first");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { /* the test still said what it had to say */ }
    }

    void Write(string rel, string text) => File.WriteAllText(Path.Combine(_dir, rel), text);
    bool Exists(string rel) => File.Exists(Path.Combine(_dir, rel));

    /// <summary>
    /// A rebase that stops on f.txt: main changed it, the replayed commit deleted it, or the other way
    /// round. During a rebase "ours" is the base being replayed onto and "theirs" the commit replayed.
    /// </summary>
    void StopOnDeletion(bool incomingDeletes)
    {
        _git.Ok(_dir, "checkout", "-q", "-b", "topic");
        if (incomingDeletes) _git.Ok(_dir, "rm", "-q", "f.txt");
        else Write("f.txt", "two, from the topic\n");
        _git.Ok(_dir, "commit", "-q", "-am", "topic change");
        _git.Ok(_dir, "checkout", "-q", "main");
        if (incomingDeletes) Write("f.txt", "two, from main\n");
        else _git.Ok(_dir, "rm", "-q", "f.txt");
        _git.Ok(_dir, "commit", "-q", "-am", "main change");
        _git.Ok(_dir, "checkout", "-q", "topic");
        Assert.False(_git.Run(_dir, "rebase", "main").Ok);
        Assert.Equal(["f.txt"], _git.ConflictedFiles(_dir));
    }

    [Fact]
    public void Accepting_the_incoming_deletion_deletes_the_file()
    {
        StopOnDeletion(incomingDeletes: true);

        _git.TakeSide(_dir, ["f.txt"], ours: false);

        Assert.Empty(_git.ConflictedFiles(_dir));
        Assert.False(Exists("f.txt"));
        Assert.Contains("D  f.txt", _git.Out(_dir, "status", "--porcelain"));
    }

    [Fact]
    public void Accepting_the_current_side_over_an_incoming_deletion_keeps_the_file()
    {
        StopOnDeletion(incomingDeletes: true);

        _git.TakeSide(_dir, ["f.txt"], ours: true);

        Assert.Empty(_git.ConflictedFiles(_dir));
        Assert.Equal("two, from main\n", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }

    [Fact]
    public void Accepting_the_current_deletion_deletes_the_file()
    {
        StopOnDeletion(incomingDeletes: false);

        _git.TakeSide(_dir, ["f.txt"], ours: true);

        Assert.Empty(_git.ConflictedFiles(_dir));
        Assert.False(Exists("f.txt"));
    }

    [Fact]
    public void A_conflict_is_named_in_git_s_own_words()
    {
        StopOnDeletion(incomingDeletes: true);
        Assert.Equal("UD", Conflicts.StatusCode(_git, _dir, "f.txt"));
        Assert.Equal("deleted by them", Conflicts.Describe("UD"));
        Assert.Equal("both modified", Conflicts.Describe("UU"));
    }
}
