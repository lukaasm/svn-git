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
}
