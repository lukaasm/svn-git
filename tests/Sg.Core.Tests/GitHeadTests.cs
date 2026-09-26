using System.Text;

namespace Sg.Core.Tests;

/// <summary>A clone's HEAD read from its files, against git's own, and what a commit declares read once.</summary>
public sealed class GitHeadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sgh-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch { /* leftovers in temp are acceptable */ }
    }

    static string Git(string cwd, params string[] args) =>
        Proc.Run("git", ["-C", cwd, "-c", "user.name=T", "-c", "user.email=t@x", "-c", "protocol.file.allow=always", .. args], null, new CollectingLog())
            .EnsureOk().StdOut.Trim();

    [Fact]
    public void Head_read_from_the_files_is_git_s()
    {
        var repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Git(repo, "add", "a.txt");
        Git(repo, "commit", "-q", "-m", "one");
        var clone = new GitRepo("git", repo, new CollectingLog());
        string ByGit(string cwd) => Git(cwd, "rev-parse", "HEAD");

        Assert.Equal(ByGit(repo), clone.HeadCommit());
        // A branch only in packed-refs.
        Git(repo, "pack-refs", "--all", "--prune");
        Assert.False(File.Exists(Path.Combine(repo, ".git", "refs", "heads", "main")));
        Assert.Equal(ByGit(repo), clone.HeadCommit());
        // Detached.
        Git(repo, "checkout", "-q", "--detach");
        Assert.Equal(ByGit(repo), clone.HeadCommit());
        Git(repo, "checkout", "-q", "main");

        // A submodule: its .git is a file naming a folder in the parent's .git.
        var parent = Path.Combine(_dir, "parent");
        Directory.CreateDirectory(parent);
        Git(parent, "init", "-q", "-b", "main");
        Git(parent, "submodule", "add", "-q", repo, "libs/sub");
        var sub = Path.Combine(parent, "libs", "sub");
        Assert.True(File.Exists(Path.Combine(sub, ".git")));
        Assert.Equal(ByGit(sub), new GitRepo("git", sub, new CollectingLog()).HeadCommit());

        // A linked worktree: its branch lives in the main repository's refs, which commondir names.
        var linked = Path.Combine(_dir, "linked");
        Git(repo, "worktree", "add", "-q", "-b", "side", linked);
        Assert.Equal(ByGit(linked), new GitRepo("git", linked, new CollectingLog()).HeadCommit());

        // Not a repository at all: git decides.
        Assert.Equal("HEAD", new GitRepo("git", _dir, new CollectingLog()).HeadCommit());
    }

    [Fact]
    public void Tracking_and_remotes_read_from_config_are_git_s()
    {
        var server = Path.Combine(_dir, "server");
        Directory.CreateDirectory(server);
        Git(server, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(server, "a.txt"), "a");
        Git(server, "add", "a.txt");
        Git(server, "commit", "-q", "-m", "one");
        var clone = Path.Combine(_dir, "clone");
        Git(_dir, "clone", "-q", server, clone);
        var repo = new GitRepo("git", clone, new CollectingLog());
        (string?, string?, string?) ByGit()
        {
            var head = Proc.Run("git", ["-C", clone, "symbolic-ref", "--short", "-q", "HEAD"], null, new CollectingLog());
            if (!head.Ok) return (null, null, null);
            var local = head.StdOut.Trim();
            string? Get(string key) { var r = Proc.Run("git", ["-C", clone, "config", "--get", key], null, new CollectingLog()); return r.Ok && r.StdOut.Trim().Length > 0 ? r.StdOut.Trim() : null; }
            var remote = Get($"branch.{local}.remote");
            var merge = Get($"branch.{local}.merge");
            if (remote == null || remote == "." || merge == null) return (local, null, null);
            return (local, remote, merge.StartsWith("refs/heads/", StringComparison.Ordinal) ? merge[11..] : merge);
        }
        List<string> RemotesByGit() => Git(clone, "remote").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();

        Assert.Equal(("main", "origin", "main"), repo.Tracking());
        Assert.Equal(ByGit(), repo.Tracking());
        Git(clone, "remote", "add", "zeta", server);
        Git(clone, "remote", "add", "al.pha", server);
        Assert.Equal(RemotesByGit(), repo.Remotes());
        Git(clone, "checkout", "-q", "-b", "Local.Only");
        Assert.Equal(ByGit(), repo.Tracking());
        Git(clone, "checkout", "-q", "--detach");
        Assert.Equal(ByGit(), repo.Tracking());
    }

    [Fact]
    public void Git_folders_shallowness_and_remote_urls_read_from_the_files_are_git_s()
    {
        var server = Path.Combine(_dir, "server");
        Directory.CreateDirectory(server);
        Git(server, "init", "-q", "-b", "main");
        for (var i = 0; i < 3; i++)
        {
            File.WriteAllText(Path.Combine(server, "a.txt"), "a" + i);
            Git(server, "add", "a.txt");
            Git(server, "commit", "-q", "-m", "c" + i);
        }
        var clone = Path.Combine(_dir, "clone");
        Git(_dir, "clone", "-q", "--depth", "1", "file://" + server.Replace('\\', '/'), clone);
        var parent = Path.Combine(_dir, "parent");
        Directory.CreateDirectory(parent);
        Git(parent, "init", "-q", "-b", "main");
        Git(parent, "submodule", "add", "-q", server, "libs/sub");
        var linked = Path.Combine(_dir, "linked");
        Git(clone, "worktree", "add", "-q", "-b", "side", linked);
        var repos = new[] { clone, Path.Combine(parent, "libs", "sub"), linked, parent };
        string? UrlByGit(string cwd, string remote)
        {
            var r = Proc.Run("git", ["-C", cwd, "remote", "get-url", remote], null, new CollectingLog());
            return r.Ok ? r.StdOut.Trim() : null;
        }
        void Same()
        {
            foreach (var path in repos)
            {
                var repo = new GitRepo("git", path, new CollectingLog());
                Assert.Equal(Path.GetFullPath(Git(path, "rev-parse", "--absolute-git-dir")), repo.GitDir, StringComparer.OrdinalIgnoreCase);
                Assert.Equal(Git(path, "rev-parse", "--is-shallow-repository") == "true", repo.IsShallow());
                foreach (var remote in new[] { "origin", "second", "empty", "reset", "nope" })
                    Assert.Equal(UrlByGit(path, remote), repo.RemoteUrl(remote));
            }
        }

        Assert.True(new GitRepo("git", clone, new CollectingLog()).IsShallow());
        Same();
        // Several URLs, the first counts; an empty one starts the list again; a remote with none.
        Git(clone, "remote", "add", "second", "https://example.com/a/b.git");
        Git(clone, "config", "--add", "remote.second.url", "https://example.com/other.git");
        Git(clone, "config", "--add", "remote.reset.url", "https://example.com/gone.git");
        Git(clone, "config", "--add", "remote.reset.url", "");
        Git(clone, "config", "--add", "remote.reset.url", "https://example.com/kept.git");
        Git(clone, "config", "remote.empty.fetch", "+refs/heads/*:refs/remotes/empty/*");
        Same();
        // insteadOf: the longest prefix wins (second's a/b.git), and of two as long the one whose base git
        // met first, though the other prefix came first (reset's kept.git).
        Git(clone, "config", "--add", "url.https://mirror.example/.insteadOf", "https://example.com/");
        Git(clone, "config", "--add", "url.ssh://git@example.com/a/.insteadOf", "https://example.com/a/");
        Git(clone, "config", "--add", "url.FIRST/.insteadOf", "https://example.com/o");
        Git(clone, "config", "--add", "url.second/.insteadOf", "https://example.com/x");
        Git(clone, "config", "--add", "url.second/.insteadOf", "https://example.com/k");
        Git(clone, "config", "--add", "url.FIRST/.insteadOf", "https://example.com/k");
        Same();
        Assert.Equal("FIRST/ept.git", new GitRepo("git", clone, new CollectingLog()).RemoteUrl("reset"));
        Assert.Equal("ssh://git@example.com/a/b.git", new GitRepo("git", clone, new CollectingLog()).RemoteUrl("second"));
        // Whole again.
        Git(clone, "fetch", "-q", "--unshallow");
        Same();
        Assert.False(new GitRepo("git", clone, new CollectingLog()).IsShallow());
    }

    [Fact]
    public void What_a_commit_declares_is_read_once()
    {
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        Git(sub, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(sub, "s.txt"), "s");
        Git(sub, "add", "s.txt");
        Git(sub, "commit", "-q", "-m", "sub");
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(app);
        Git(app, "init", "-q", "-b", "main");
        Git(app, "submodule", "add", "-q", sub, "libs/core");
        Git(app, "commit", "-q", "-m", "with a submodule");
        var head = Git(app, "rev-parse", "HEAD");
        var asked = 0;
        ProcResult Run(string[] args) { asked++; return Proc.Run("git", ["-C", app, .. args], null, new CollectingLog()); }

        var first = GitSubmodules.Declared(Run, head);
        var second = GitSubmodules.Declared(Run, head);
        Assert.Equal(2, asked);
        Assert.Equal(first, second);
        Assert.Equal("libs/core", Assert.Single(first).Path);
        Assert.Equal(Git(app, "rev-parse", "HEAD:libs/core"), first[0].Pin);
        // A name git resolves is asked every time.
        GitSubmodules.Declared(Run, "HEAD");
        Assert.Equal(4, asked);
    }
}
