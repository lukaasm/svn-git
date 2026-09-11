using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// One block of a file staged, unstaged and committed on its own, against a real git. This is the path
/// behind "stage this hunk" in the commit window, so it runs git rather than standing in for it.
/// </summary>
public sealed class StagingTests : IDisposable
{
    readonly string _dir;
    readonly Git _git;
    readonly CollectingLog _log = new();

    // Twenty lines, edited at line 2 and at line 18. Three lines of context on each side of the two
    // edits do not meet, so git writes two blocks, which is what a test about one block needs.
    const string Twenty = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\nm\nn\no\np\nq\nr\ns\nt\n";
    const string Edited = "a\nB\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\nm\nn\no\np\nq\nR\ns\nt\n";
    const string OnlyTheFirstBlock = "a\nB\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\nm\nn\no\np\nq\nr\ns\nt\n";
    const string OnlyTheSecondBlock = "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\nk\nl\nm\nn\no\np\nq\nR\ns\nt\n";

    public StagingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sgst-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _git = new Git("git", _dir, _log);
        _git.Ok(_dir, "init", "-q", "-b", "main");
        _git.Config("user.name", "Test");
        _git.Config("user.email", "test@localhost");
        _git.Config("core.autocrlf", "false");
        Write("f.txt", Twenty);
        Write("other.txt", "x\n");
        _git.Ok(_dir, "add", "-A");
        _git.Ok(_dir, "commit", "-q", "-m", "first");
    }

    public void Dispose()
    {
        try { DeleteTree(_dir); }
        catch (IOException) { /* the test still said what it had to say */ }
    }

    static void DeleteTree(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    void Write(string rel, string text) => File.WriteAllText(Path.Combine(_dir, rel), text);
    string Read(string rel) => File.ReadAllText(Path.Combine(_dir, rel));
    string Committed(string rel) => _git.ShowText("HEAD", rel);

    PatchHunk[] Unstaged(string rel)
    {
        var file = Patch.FileFor(Patch.Parse(_git.DiffUnstaged(_dir, rel)), rel);
        return file?.Hunks.ToArray() ?? Array.Empty<PatchHunk>();
    }

    /// <summary>Stages exactly one block of a file, the way the diff view's Stage button does.</summary>
    void StageBlock(string rel, int index)
    {
        var file = Patch.FileFor(Patch.Parse(_git.DiffUnstaged(_dir, rel)), rel)!;
        _git.ApplyPatch(_dir, Patch.Render(file, new[] { file.Hunks[index] }), cached: true, reverse: false);
    }

    [Fact]
    public void Two_edits_far_apart_are_two_blocks()
    {
        Write("f.txt", Edited);
        Assert.Equal(2, Unstaged("f.txt").Length);
    }

    [Fact]
    public void Staging_one_block_leaves_the_other_unstaged()
    {
        Write("f.txt", Edited);
        StageBlock("f.txt", 0);

        var staged = Patch.FileFor(Patch.Parse(_git.DiffStaged(_dir, "f.txt")), "f.txt")!;
        Assert.Single(staged.Hunks);
        Assert.Contains("+B", staged.Hunks[0].Lines);

        var left = Unstaged("f.txt");
        Assert.Single(left);
        Assert.Contains("+R", left[0].Lines);

        // Staging touches the index, never the file on disk.
        Assert.Equal(Edited, Read("f.txt"));
    }

    [Fact]
    public void The_status_of_a_part_staged_file_says_so()
    {
        Write("f.txt", Edited);
        StageBlock("f.txt", 0);
        var e = Assert.Single(_git.StatusEntries(_dir), s => s.Path == "f.txt");
        Assert.Equal("M", e.X);
        Assert.Equal("M", e.Y);
    }

    [Fact]
    public void Unstaging_a_block_puts_it_back_where_it_was()
    {
        Write("f.txt", Edited);
        StageBlock("f.txt", 0);
        var staged = Patch.FileFor(Patch.Parse(_git.DiffStaged(_dir, "f.txt")), "f.txt")!;
        _git.ApplyPatch(_dir, Patch.Render(staged, staged.Hunks), cached: true, reverse: true);

        Assert.Equal("", _git.DiffStaged(_dir, "f.txt").Trim());
        Assert.Equal(2, Unstaged("f.txt").Length);
        Assert.Equal(Edited, Read("f.txt"));
    }

    [Fact]
    public void A_commit_of_a_part_staged_file_holds_only_the_staged_block()
    {
        Write("f.txt", Edited);
        StageBlock("f.txt", 0);
        _git.CommitPathsAsUser(_dir, new[] { "f.txt" }, new[] { "f.txt" }, "half of it");

        Assert.Equal(OnlyTheFirstBlock, Committed("f.txt"));
        // The block that was not staged is still an edit in the working tree, and nothing is staged now.
        Assert.Equal(Edited, Read("f.txt"));
        Assert.Equal("", _git.DiffStaged(_dir).Trim());
        Assert.Single(Unstaged("f.txt"));
    }

    [Fact]
    public void A_commit_takes_the_whole_of_a_file_with_nothing_staged()
    {
        Write("f.txt", Edited);
        _git.CommitPathsAsUser(_dir, new[] { "f.txt" }, Array.Empty<string>(), "all of it");
        Assert.Equal(Edited, Committed("f.txt"));
        Assert.Empty(_git.StatusEntries(_dir));
    }

    [Fact]
    public void What_is_staged_for_a_file_nobody_picked_stays_out_of_the_commit_and_stays_staged()
    {
        Write("f.txt", Edited);
        Write("other.txt", "y\n");
        _git.Ok(_dir, "add", "other.txt");

        _git.CommitPathsAsUser(_dir, new[] { "f.txt" }, Array.Empty<string>(), "only f");

        Assert.Equal("x\n", Committed("other.txt"));
        var e = Assert.Single(_git.StatusEntries(_dir), s => s.Path == "other.txt");
        Assert.Equal("M", e.X);
    }

    [Fact]
    public void An_untracked_file_that_was_picked_goes_in_whole()
    {
        Write("new.txt", "brand new\n");
        _git.CommitPathsAsUser(_dir, new[] { "new.txt" }, Array.Empty<string>(), "adds one");
        Assert.Equal("brand new\n", Committed("new.txt"));
    }

    [Fact]
    public void Amending_replaces_the_commit_instead_of_adding_one()
    {
        var before = _git.Out(_dir, "rev-list", "--count", "HEAD");
        Write("f.txt", Edited);
        _git.CommitPathsAsUser(_dir, new[] { "f.txt" }, Array.Empty<string>(), "first, with more in it", amend: true);

        Assert.Equal(before, _git.Out(_dir, "rev-list", "--count", "HEAD"));
        Assert.Equal("first, with more in it", _git.Subject(_git.HeadSha(_dir)));
        Assert.Equal(Edited, Committed("f.txt"));
        // The other file of the commit being replaced is still in it.
        Assert.Equal("x\n", Committed("other.txt"));
    }

    [Fact]
    public void Reverting_a_block_in_the_working_tree_leaves_the_other_change_alone()
    {
        Write("f.txt", Edited);
        var file = Patch.FileFor(Patch.Parse(_git.DiffUnstaged(_dir, "f.txt")), "f.txt")!;
        Write("f.txt", Patch.Reverse(Read("f.txt"), new[] { file.Hunks[0] }));
        Assert.Equal(OnlyTheSecondBlock, Read("f.txt"));
    }

    [Fact]
    public void Unstage_puts_a_whole_file_back()
    {
        Write("f.txt", Edited);
        _git.AddPaths(_dir, new[] { "f.txt" });
        Assert.NotEqual("", _git.DiffStaged(_dir, "f.txt").Trim());
        _git.Unstage(_dir, new[] { "f.txt" });
        Assert.Equal("", _git.DiffStaged(_dir, "f.txt").Trim());
        Assert.Equal(Edited, Read("f.txt"));
    }

    [Fact]
    public void Remove_deletes_the_file_and_stages_the_deletion()
    {
        _git.RemovePaths(_dir, new[] { "other.txt" });
        Assert.False(File.Exists(Path.Combine(_dir, "other.txt")));
        var e = Assert.Single(_git.StatusEntries(_dir), s => s.Path == "other.txt");
        Assert.Equal("D", e.X);
    }

    [Fact]
    public void The_status_brings_the_branch_name_back_with_it()
    {
        var s = _git.Status(_dir);
        Assert.Equal("main", s.Branch);
        Assert.Empty(s.Entries);
    }

    [Fact]
    public void A_detached_head_has_no_branch_name()
    {
        _git.SetHeadDetached(_dir, _git.HeadSha(_dir));
        Assert.Null(_git.Status(_dir).Branch);
    }

    [Fact]
    public void The_head_summary_gives_the_message_and_whether_there_is_a_parent()
    {
        var first = _git.HeadSummary(_dir);
        Assert.Equal("first", first.Message);
        Assert.False(first.HasParent);

        Write("f.txt", Edited);
        _git.CommitPathsAsUser(_dir, new[] { "f.txt" }, Array.Empty<string>(), "second");
        var second = _git.HeadSummary(_dir);
        Assert.Equal("second", second.Message);
        Assert.True(second.HasParent);
    }

    [Fact]
    public void Counts_per_file_come_back_without_the_patch()
    {
        Write("f.txt", Edited);
        Write("other.txt", "y\nz\n");
        var counts = _git.NumStat(_dir, "HEAD");
        Assert.Equal(new DiffStats.Count(2, 2), counts["f.txt"]);
        Assert.Equal(new DiffStats.Count(2, 1), counts["other.txt"]);
    }

    [Fact]
    public void A_renamed_file_is_counted_under_the_name_it_has_now()
    {
        File.Move(Path.Combine(_dir, "other.txt"), Path.Combine(_dir, "renamed.txt"));
        _git.Ok(_dir, "add", "-A");
        var counts = _git.NumStat(_dir, "HEAD");
        Assert.True(counts.ContainsKey("renamed.txt"), "expected the new name, got: " + string.Join(", ", counts.Keys));
        Assert.False(counts.ContainsKey("other.txt"));
    }

    [Fact]
    public void A_binary_file_counts_as_nothing_rather_than_breaking_the_read()
    {
        File.WriteAllBytes(Path.Combine(_dir, "blob.bin"), new byte[] { 1, 2, 0, 3, 4 });
        _git.Ok(_dir, "add", "-A");
        Assert.Equal(new DiffStats.Count(0, 0), _git.NumStat(_dir, "HEAD")["blob.bin"]);
    }

    [Fact]
    public void A_commit_of_a_staged_deletion_takes_the_file_out()
    {
        _git.RemovePaths(_dir, new[] { "other.txt" });
        _git.CommitPathsAsUser(_dir, new[] { "other.txt" }, new[] { "other.txt" }, "drops it");
        Assert.Equal("", Committed("other.txt"));
        Assert.Empty(_git.StatusEntries(_dir));
    }
}
