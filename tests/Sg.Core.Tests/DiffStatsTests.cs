using Xunit;

namespace Sg.Core.Tests;

public sealed class DiffStatsTests
{
    [Fact]
    public void Git_patch_counts_per_file_and_names_the_new_side_of_a_rename()
    {
        const string patch = """
            diff --git a/src/a.cpp b/src/a.cpp
            index 1..2 100644
            --- a/src/a.cpp
            +++ b/src/a.cpp
            @@ -1,3 +1,4 @@
             keep
            -old
            +new
            +more
            diff --git a/old.txt b/new.txt
            similarity index 90%
            rename from old.txt
            rename to new.txt
            --- a/old.txt
            +++ b/new.txt
            @@ -1 +1 @@
            -x
            +y
            diff --git a/img.png b/img.png
            Binary files differ
            """;
        var s = DiffStats.Parse(patch);
        Assert.Equal(new DiffStats.Count(2, 1), s["src/a.cpp"]);
        Assert.Equal(new DiffStats.Count(1, 1), s["new.txt"]);
        Assert.Equal(new DiffStats.Count(0, 0), s["img.png"]);
        Assert.False(s.ContainsKey("old.txt"));
    }

    [Fact]
    public void Svn_patch_counts_per_index_and_matches_a_row_by_suffix()
    {
        const string patch = """
            Index: dev/src/x.cpp
            ===================================================================
            --- dev/src/x.cpp	(revision 9)
            +++ dev/src/x.cpp	(working copy)
            @@ -1,2 +1,2 @@
            -a
            -b
            +c
            """;
        var s = DiffStats.Parse(patch);
        Assert.Equal(new DiffStats.Count(1, 2), s["dev/src/x.cpp"]);
        Assert.Equal(new DiffStats.Count(1, 2), DiffStats.For(s, "/branches/fort/dev/src/x.cpp"));
        Assert.Null(DiffStats.For(s, "/branches/fort/dev/src/y.cpp"));
    }
}
