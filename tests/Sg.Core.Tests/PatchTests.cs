using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// The block model under "stage this hunk" and "revert this hunk". A patch read here is written back out
/// for git apply, so the tests care about the exact bytes as much as about the counts.
/// </summary>
public sealed class PatchTests
{
    /// <summary>Raw literals carry the source file's line endings; a patch is LF, so say so once here.</summary>
    static string Lf(string s) => s.Replace("\r\n", "\n");

    const string TwoHunks = """
        diff --git a/f.txt b/f.txt
        index 1111111..2222222 100644
        --- a/f.txt
        +++ b/f.txt
        @@ -1,3 +1,4 @@
         a
        +NEW
         b
         c
        @@ -7,3 +8,3 @@ void f()
         g
        -h
        +H
         i

        """;

    static PatchFile OneFile(string patch)
    {
        var files = Patch.Parse(Lf(patch));
        return Assert.Single(files);
    }

    [Fact]
    public void Reads_the_file_and_its_blocks()
    {
        var f = OneFile(TwoHunks);
        Assert.Equal("f.txt", f.Path);
        Assert.Equal("f.txt", f.OldPath);
        Assert.False(f.Binary);
        Assert.Equal(2, f.Hunks.Count);
        Assert.Equal((1, 3, 1, 4), Range(f.Hunks[0]));
        Assert.Equal((7, 3, 8, 3), Range(f.Hunks[1]));
        Assert.Equal(" void f()", f.Hunks[1].Heading);
        Assert.Equal(0, f.Hunks[0].Index);
        Assert.Equal(1, f.Hunks[1].Index);
        Assert.Equal("f.txt", f.Hunks[1].Path);
    }

    static (int, int, int, int) Range(PatchHunk h) => (h.OldStart, h.OldCount, h.NewStart, h.NewCount);

    [Fact]
    public void Counts_the_lines_each_block_adds_and_removes()
    {
        var f = OneFile(TwoHunks);
        Assert.Equal((1, 0), (f.Hunks[0].Added, f.Hunks[0].Removed));
        Assert.Equal((1, 1), (f.Hunks[1].Added, f.Hunks[1].Removed));
    }

    [Fact]
    public void Gives_both_sides_of_a_block_without_the_markers()
    {
        var f = OneFile(TwoHunks);
        Assert.Equal(new[] { "a", "NEW", "b", "c" }, f.Hunks[0].NewLines);
        Assert.Equal(new[] { "a", "b", "c" }, f.Hunks[0].OldLines);
    }

    [Fact]
    public void The_trailing_newline_of_a_patch_is_not_a_context_line()
    {
        // " g", "-h", "+H", " i" and nothing after them.
        var f = OneFile(TwoHunks);
        Assert.Equal(4, f.Hunks[1].Lines.Count);
        Assert.Equal(" i", f.Hunks[1].Lines[^1]);
    }

    [Fact]
    public void Writing_every_block_back_gives_the_patch_it_came_from() =>
        Assert.Equal(Lf(TwoHunks), Patch.Render(OneFile(TwoHunks)));

    [Fact]
    public void One_block_alone_keeps_the_old_side_and_moves_the_new_side_back()
    {
        var f = OneFile(TwoHunks);
        var only = Patch.Render(f, new[] { f.Hunks[1] });
        Assert.Contains("@@ -7,3 +7,3 @@ void f()\n", only);
        Assert.DoesNotContain("+NEW", only);
        Assert.StartsWith("diff --git a/f.txt b/f.txt\n", only);
    }

    [Fact]
    public void The_first_block_alone_needs_no_shift()
    {
        var f = OneFile(TwoHunks);
        Assert.Contains("@@ -1,3 +1,4 @@\n", Patch.Render(f, new[] { f.Hunks[0] }));
    }

    [Fact]
    public void Nothing_picked_is_an_empty_patch() =>
        Assert.Equal("", Patch.Render(OneFile(TwoHunks), Array.Empty<PatchHunk>()));

    const string Modified = "a\nNEW\nb\nc\nd\ne\nf\ng\nH\ni\n";

    [Fact]
    public void Reversing_one_block_leaves_the_other_change_alone()
    {
        var f = OneFile(TwoHunks);
        Assert.Equal("a\nb\nc\nd\ne\nf\ng\nH\ni\n", Patch.Reverse(Modified, new[] { f.Hunks[0] }));
        Assert.Equal("a\nNEW\nb\nc\nd\ne\nf\ng\nh\ni\n", Patch.Reverse(Modified, new[] { f.Hunks[1] }));
    }

    [Fact]
    public void Reversing_every_block_gives_the_old_file_back() =>
        Assert.Equal("a\nb\nc\nd\ne\nf\ng\nh\ni\n", Patch.Reverse(Modified, OneFile(TwoHunks).Hunks));

    [Fact]
    public void Reversing_refuses_when_the_file_moved_under_it()
    {
        var f = OneFile(TwoHunks);
        var ex = Assert.Throws<SgException>(() => Patch.Reverse("a\nSOMETHING ELSE\nb\nc\n", new[] { f.Hunks[0] }));
        Assert.Contains("refresh", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reversing_refuses_a_block_past_the_end_of_the_file() =>
        Assert.Throws<SgException>(() => Patch.Reverse("a\n", OneFile(TwoHunks).Hunks));

    [Fact]
    public void A_block_with_nothing_on_the_new_side_puts_its_lines_back_after_the_line_it_names()
    {
        const string patch = """
            diff --git a/f.txt b/f.txt
            --- a/f.txt
            +++ b/f.txt
            @@ -2,2 +1,0 @@
            -b
            -c

            """;
        var f = OneFile(patch);
        Assert.Equal((2, 2, 1, 0), Range(f.Hunks[0]));
        Assert.Equal("a\nb\nc\n", Patch.Reverse("a\n", f.Hunks));
    }

    [Fact]
    public void A_crlf_file_stays_crlf()
    {
        const string patch = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,2 @@\n a\r\n-b\r\n+B\r\n";
        var f = OneFile(patch);
        Assert.Equal(new[] { "a", "B" }, f.Hunks[0].NewLines);
        Assert.Equal("a\r\nb\r\n", Patch.Reverse("a\r\nB\r\n", f.Hunks));
    }

    [Fact]
    public void An_svn_patch_names_its_file_on_the_Index_line()
    {
        const string patch = """
            Index: src/a.cpp
            ===================================================================
            --- src/a.cpp	(revision 12)
            +++ src/a.cpp	(working copy)
            @@ -1,2 +1,2 @@
             keep
            -old
            +new

            """;
        var f = OneFile(patch);
        Assert.Equal("src/a.cpp", f.Path);
        Assert.Single(f.Hunks);
        Assert.Equal("keep\nold\n", Patch.Reverse("keep\nnew\n", f.Hunks));
    }

    [Fact]
    public void A_binary_file_says_so_and_has_no_blocks()
    {
        var f = OneFile("diff --git a/img.png b/img.png\nBinary files a/img.png and b/img.png differ\n");
        Assert.True(f.Binary);
        Assert.Empty(f.Hunks);
    }

    [Fact]
    public void No_newline_at_the_end_belongs_to_the_block_it_follows()
    {
        // Written with the counts spelled out: a rendered patch always spells them, so this round trips.
        const string patch = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,1 +1,1 @@\n-a\n+b\n\\ No newline at end of file\n";
        var f = OneFile(patch);
        Assert.Equal(3, f.Hunks[0].Lines.Count);
        Assert.Equal(new[] { "b" }, f.Hunks[0].NewLines);
        Assert.Equal(patch, Patch.Render(f));
    }

    [Fact]
    public void A_header_with_no_counts_means_one_line_each_side()
    {
        var f = OneFile("diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -3 +3 @@\n-a\n+b\n");
        Assert.Equal((3, 1, 3, 1), Range(f.Hunks[0]));
    }

    [Fact]
    public void Several_files_come_out_in_the_order_the_patch_names_them()
    {
        var files = Patch.Parse(Lf(TwoHunks) + "diff --git a/g.txt b/g.txt\n--- a/g.txt\n+++ b/g.txt\n@@ -1 +1 @@\n-x\n+y\n");
        Assert.Equal(new[] { "f.txt", "g.txt" }, files.Select(f => f.Path));
        Assert.Equal(2, files[0].Hunks.Count);
        Assert.Single(files[1].Hunks);
    }

    [Fact]
    public void A_rename_names_both_sides()
    {
        var files = Patch.Parse("diff --git a/old.txt b/new.txt\nsimilarity index 90%\nrename from old.txt\nrename to new.txt\n--- a/old.txt\n+++ b/new.txt\n@@ -1 +1 @@\n-x\n+y\n");
        Assert.Equal("new.txt", files[0].Path);
        Assert.Equal("old.txt", files[0].OldPath);
    }

    [Fact]
    public void A_block_is_named_by_the_lines_it_changes_not_by_the_context_around_them()
    {
        var f = OneFile(TwoHunks);
        Assert.Equal("line 2", f.Hunks[0].Describe());     // covers 1-4, changes only line 2
        Assert.Equal("line 9", f.Hunks[1].Describe());     // covers 8-10, changes only line 9
    }

    [Fact]
    public void A_block_that_only_deletes_is_named_by_the_line_it_follows()
    {
        var f = OneFile("diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,4 +1,2 @@\n a\n-b\n-c\n d\n");
        Assert.Equal("2 deleted line(s) after line 1", f.Hunks[0].Describe());
    }

    [Fact]
    public void A_block_that_adds_several_lines_names_the_run()
    {
        var f = OneFile("diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,4 @@\n a\n+x\n+y\n b\n");
        Assert.Equal("lines 2-3", f.Hunks[0].Describe());
    }

    [Fact]
    public void The_block_under_a_line_is_the_one_that_covers_it()
    {
        var f = OneFile(TwoHunks);
        Assert.Same(f.Hunks[0], Patch.HunkAt(f, 2));
        Assert.Null(Patch.HunkAt(f, 6));
        Assert.Same(f.Hunks[1], Patch.HunkAt(f, 9));
    }

    [Fact]
    public void A_selection_picks_every_block_it_touches()
    {
        var f = OneFile(TwoHunks);
        Assert.Equal(2, Patch.HunksIn(f, 1, 10).Count);
        Assert.Single(Patch.HunksIn(f, 9, 9));
        Assert.Empty(Patch.HunksIn(f, 6, 6));
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "\r\n")]
    [InlineData("a\nb\n", "\n")]
    [InlineData("a\rb\r", "\r")]
    [InlineData("a\r\nb\nc\r\n", "\r\n")]   // mixed: the ending most lines use wins
    [InlineData("one line", null)]
    public void The_line_ending_a_rewritten_file_keeps_is_the_one_it_mostly_uses(string text, string? expected) =>
        Assert.Equal(expected, Patch.Eol(text));

    [Fact]
    public void A_file_is_found_by_a_path_that_ends_the_one_the_patch_names()
    {
        var files = Patch.Parse("diff --git a/src/a.cpp b/src/a.cpp\n--- a/src/a.cpp\n+++ b/src/a.cpp\n@@ -1 +1 @@\n-x\n+y\n");
        Assert.NotNull(Patch.FileFor(files, "src/a.cpp"));
        Assert.NotNull(Patch.FileFor(files, "a.cpp"));
        Assert.NotNull(Patch.FileFor(files, @"src\a.cpp"));
        Assert.Null(Patch.FileFor(files, "other.cpp"));
    }
}
