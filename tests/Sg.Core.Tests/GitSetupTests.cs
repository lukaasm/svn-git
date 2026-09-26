namespace Sg.Core.Tests;

/// <summary>Which git.exe sg starts, and the format of the repositories it makes.</summary>
public sealed class GitSetupTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sgs-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* leftovers in temp are acceptable */ }
    }

    [Fact]
    public void Git_for_windows_wrapper_is_passed_over_for_the_git_it_starts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var top = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git");
        var wrapper = Path.Combine(top, "cmd", "git.exe");
        if (!File.Exists(wrapper) || !File.Exists(Path.Combine(top, "mingw64", "bin", "git.exe"))) return;

        var git = GitExecutable.Resolve(wrapper);
        Assert.Equal(Path.Combine(top, "mingw64", "bin", "git.exe"), git.Exe, ignoreCase: true);
        // What the wrapper would have put first on PATH, so ssh and sh are still found.
        Assert.StartsWith(Path.Combine(top, "mingw64", "bin") + ";" + Path.Combine(top, "usr", "bin") + ";", git.Env["PATH"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_git_that_is_not_git_for_windows_is_started_as_configured()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        var other = Path.Combine(_dir, "bin", "git.exe");
        File.WriteAllText(other, "");
        var git = GitExecutable.Resolve(other);
        Assert.Equal(other, git.Exe);
        Assert.Empty(git.Env);
    }

    [Fact]
    public void The_branch_read_from_head_is_the_one_git_names()
    {
        var log = new CollectingLog();
        var root = Ops.Init(Path.Combine(_dir, "root"), log, fsmonitor: false);
        var wt = Path.Combine(_dir, "root", "feature");
        root.Git.Ok(null, "worktree", "add", "-q", "-b", "feat/x.y", wt, SgRoot.RootRef);
        string? Git(string cwd) { var r = root.Git.Run(cwd, "symbolic-ref", "--short", "-q", "HEAD"); return r.Ok ? r.StdOut.Trim() : null; }

        Assert.Equal("feat/x.y", Git(wt));
        Assert.Equal(Git(wt), root.Git.HeadBranch(wt));
        Directory.CreateDirectory(Path.Combine(wt, "sub"));
        Assert.Equal(Git(Path.Combine(wt, "sub")), root.Git.HeadBranch(Path.Combine(wt, "sub")));
        root.Git.Ok(wt, "checkout", "-q", "--detach");
        Assert.Null(Git(wt));
        Assert.Null(root.Git.HeadBranch(wt));
        Assert.Throws<SgException>(() => root.Git.CurrentBranch(wt));
    }

    [Fact]
    public void A_new_store_is_sha1_with_files_for_refs_whatever_git_would_default_to()
    {
        var root = Ops.Init(_dir, new CollectingLog(), fsmonitor: false);
        Assert.Equal("sha1", root.Git.Out(null, "rev-parse", "--show-object-format"));
        Assert.Equal("files", root.Git.Out(null, "rev-parse", "--show-ref-format"));
    }
}
