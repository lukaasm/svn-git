namespace Sg.Core.Tests;

/// <summary>Alone: another test's clone push tells every store to forget, and these count what is asked.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitMemoryCollection { public const string Name = "Git memory"; }

/// <summary>
/// What one operation reads of refs, branch config, worktrees and a remote is read once, and asked again
/// after anything that could have changed it. A stale answer here is a wrong push or a lost backup.
/// </summary>
[Collection(GitMemoryCollection.Name)]
public sealed class GitMemoryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sgm-" + Guid.NewGuid().ToString("N")[..8]);
    readonly CollectingLog _log = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* leftovers in temp are acceptable */ }
    }

    [Theory]
    [InlineData(false, "rev-parse", "--verify", "HEAD")]
    [InlineData(false, "worktree", "list", "--porcelain")]
    [InlineData(false, "config", "--get-regexp", "^branch")]
    [InlineData(false, "-c", "core.quotePath=false", "ls-tree", "HEAD")]
    [InlineData(false, "commit-tree", "abc", "-F", "-")]
    [InlineData(true, "update-ref", "refs/heads/x", "abc")]
    [InlineData(true, "worktree", "add", "x")]
    [InlineData(true, "config", "branch.x.sgBase", "mono")]
    [InlineData(true, "push", "remote", "x")]
    [InlineData(true, "some-future-command")]
    public void Anything_not_known_to_be_harmless_counts_as_a_write(bool changes, params string[] args) =>
        Assert.Equal(changes, Git.Changes(args, out _));

    [Fact]
    public void A_remembered_ref_is_read_again_after_a_write()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        var head = root.Git.RefSha(SgRoot.RootRef)!;
        int Asked() => _log.Lines.Count(l => l.StartsWith("cmd: ") && l.Contains("rev-parse --verify --quiet refs/heads/memo"));
        using (root.Lock())
        {
            Assert.Null(root.Git.RefSha("refs/heads/memo"));
            Assert.Null(root.Git.RefSha("refs/heads/memo"));
            Assert.Equal(1, Asked());
            root.Git.UpdateRef("refs/heads/memo", head);
            Assert.Equal(head, root.Git.RefSha("refs/heads/memo"));
            Assert.Equal(2, Asked());

            // A write that does not go through the store's own git - another process, a file - has to be told.
            Proc.Run("git", ["-C", root.StorePath, "update-ref", "-d", "refs/heads/memo"], null, _log).EnsureOk();
            root.Git.Changed();
            Assert.Null(root.Git.RefSha("refs/heads/memo"));
        }
        // The operation is over: the next one asks again.
        Assert.Null(root.Git.RefSha("refs/heads/memo"));
        Assert.Equal(4, Asked());
    }

    [Fact]
    public void Nothing_is_remembered_outside_an_operation()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        var head = root.Git.RefSha(SgRoot.RootRef)!;
        Assert.Null(root.Git.RefSha("refs/heads/outside"));
        Proc.Run("git", ["-C", root.StorePath, "update-ref", "refs/heads/outside", head], null, _log).EnsureOk();
        Assert.Equal(head, root.Git.RefSha("refs/heads/outside"));
    }

    [Fact]
    public void Store_settings_go_in_one_write_and_no_key_is_there_twice()
    {
        var root = Ops.Init(_dir, _log, fsmonitor: false);
        Assert.Single(_log.Lines, l => l.StartsWith("cmd: ") && l.Contains(" config --local --list"));
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("cmd: ") && l.Contains(" config core."));
        var keys = root.Git.Out(null, "config", "--local", "--name-only", "--list").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("zdiff3", root.Git.Out(null, "config", "merge.conflictStyle"));
        // Every key is there once, so git can still set one the ordinary way.
        root.Git.Config("core.filemode", "true");
        Assert.Equal("true", root.Git.Out(null, "config", "core.filemode"));
    }
}
