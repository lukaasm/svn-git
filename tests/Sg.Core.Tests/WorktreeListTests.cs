namespace Sg.Core.Tests;

/// <summary>The worktree list read from the store's files against `git worktree list --porcelain`.</summary>
public sealed class WorktreeListTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sgw-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch { /* leftovers in temp are acceptable */ }
    }

    [Fact]
    public void The_list_read_from_files_is_git_s()
    {
        var root = Ops.Init(Path.Combine(_dir, "root"), new CollectingLog(), fsmonitor: false);
        var git = root.Git;
        void Same(string when)
        {
            var byGit = git.WorktreeListByGit();
            var here = git.WorktreesFromFiles();
            Assert.NotNull(here);
            Assert.True(byGit.SequenceEqual(here!), $"{when}\ngit:\n{string.Join("\n", byGit)}\nhere:\n{string.Join("\n", here!)}");
        }
        Same("no worktrees");
        foreach (var name in new[] { "gamma", "Beta", "alpha", "delta/x" })
            git.Ok(null, "worktree", "add", "-q", "-b", name, Path.Combine(_dir, "root", name.Replace('/', '-')), SgRoot.RootRef);
        Same("four on branches, names in mixed case");
        git.Ok(Path.Combine(_dir, "root", "gamma"), "checkout", "-q", "--detach");
        Same("one detached");
        git.Ok(null, "pack-refs", "--all", "--prune");
        Same("branches only in packed-refs");
        var gone = Path.Combine(_dir, "root", "alpha");
        foreach (var f in Directory.EnumerateFiles(gone, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(gone, true);
        Same("a folder deleted, not pruned yet");
        git.Ok(null, "worktree", "prune");
        Same("pruned");
        // worktree.useRelativePaths (git 2.48 and later) writes the folder relative to the entry.
        git.Ok(null, "-c", "worktree.useRelativePaths=true", "worktree", "add", "-q", "-b", "relative", Path.Combine(_dir, "root", "relative"), SgRoot.RootRef);
        Same("one written with a relative path");
    }
}
