namespace Sg.Core.Tests;

/// <summary>
/// Alone, after the rest: a watcher reports on a pool thread, and with every other test holding one the
/// report of a settle file came after its wait gave up, and a kept count was counted again.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EditCountsCollection { public const string Name = "Edit counts"; }

/// <summary>A checkout's edit count is counted again after a change under it, and only then.</summary>
[Collection(EditCountsCollection.Name)]
public sealed class EditCountsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sge-" + Guid.NewGuid().ToString("N")[..8]);
    readonly EditCounts _counts = new();

    public void Dispose()
    {
        _counts.Dispose();
        try { Directory.Delete(_dir, true); }
        catch { /* leftovers in temp are acceptable */ }
    }

    string Checkout(string name, string marker)
    {
        var co = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(co, marker));
        File.WriteAllText(Path.Combine(co, "a.txt"), "a");
        return co;
    }

    [Fact]
    public void A_count_is_kept_until_something_under_the_checkout_changes()
    {
        var co = Checkout("svn", Path.Combine(".svn", "tmp"));
        var asked = 0;
        int Count() => _counts.Count(co, () => ++asked);
        Assert.Equal(1, Count());
        Assert.Equal(1, Count());
        Assert.Equal(1, Count());
        // Each asked for right after the change, with no wait: the settle file has the watcher report it first.
        File.WriteAllText(Path.Combine(co, "a.txt"), "b");
        Assert.Equal(2, Count());
        Assert.Equal(2, Count());
        Directory.CreateDirectory(Path.Combine(co, "deep", "er"));
        Assert.Equal(3, Count());
        File.WriteAllText(Path.Combine(co, "deep", "er", "n.txt"), "n");
        Assert.Equal(4, Count());
        File.Move(Path.Combine(co, "deep", "er", "n.txt"), Path.Combine(co, "deep", "m.txt"));
        Assert.Equal(5, Count());
        File.Delete(Path.Combine(co, "deep", "m.txt"));
        Assert.Equal(6, Count());
        // What svn keeps in .svn counts too: a commit changes it and leaves the files as they were.
        File.WriteAllText(Path.Combine(co, ".svn", "wc.db"), "db");
        Assert.Equal(7, Count());
        File.WriteAllText(Path.Combine(co, ".svn", "tmp", "svn-temp"), "t");
        Assert.Equal(8, Count());
        Assert.Equal(8, Count());
        _counts.Forget();
        Assert.Equal(9, Count());
        Assert.Equal(9, Count());
        Assert.Equal(["svn-temp"], Directory.EnumerateFiles(Path.Combine(co, ".svn", "tmp")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_git_checkout_settles_in_its_git_folder_and_fsmonitor_s_files_change_nothing()
    {
        var co = Checkout("git", ".git");
        var asked = 0;
        int Count() => _counts.Count(co, () => ++asked);
        Assert.Equal(1, Count());
        Directory.CreateDirectory(Path.Combine(co, ".git", "fsmonitor--daemon", "cookies"));
        File.WriteAllText(Path.Combine(co, ".git", "fsmonitor--daemon", "cookies", "1234-0"), "");
        Assert.Equal(1, Count());
        File.WriteAllText(Path.Combine(co, ".git", "index"), "i");
        Assert.Equal(2, Count());
        Assert.Equal(2, Count());
    }

    [Fact]
    public void A_folder_with_no_svn_or_git_folder_is_counted_every_time()
    {
        var co = Checkout("plain", "sub");
        var asked = 0;
        _counts.Count(co, () => ++asked);
        _counts.Count(co, () => ++asked);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void A_count_that_failed_is_asked_again_and_a_checkout_let_go_starts_over()
    {
        var co = Checkout("svn", Path.Combine(".svn", "tmp"));
        Assert.Throws<SgException>(() => _counts.Count(co, () => throw new SgException("svn is busy")));
        var asked = 0;
        Assert.Equal(1, _counts.Count(co, () => ++asked));
        Assert.Equal(1, _counts.Count(co, () => ++asked));
        _counts.Keep([]);
        Assert.Equal(2, _counts.Count(co, () => ++asked));
        Assert.Equal(2, _counts.Count(co, () => ++asked));
    }
}
