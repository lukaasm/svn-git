namespace Sg.Core.Tests;

public sealed class ReviewLocationTests
{
    const string Saved = "header\nbefore\nfirst\nlast\nafter\nfooter";
    static CodeAnchor Anchor(int first = 3, int last = 4) => new("file.cs", "modified", first, last, "", "", "");

    [Fact]
    public void Exact_and_relocated_ranges_keep_both_endpoints()
    {
        Assert.Equal(new("current", 3, 4), CodeReview.Locate(Anchor(), Saved, Saved));
        Assert.Equal(new("relocated", 4, 5), CodeReview.Locate(Anchor(), Saved, "inserted\n" + Saved));
        Assert.Equal(new("relocated", 3, 4), CodeReview.Locate(Anchor(), Saved, Saved.Replace("\n", "\r\n")));
    }

    [Theory]
    [InlineData(null, "missing")]
    [InlineData("header\nbefore\nchanged\nlast\nafter\nfooter", "changed")]
    [InlineData(Saved + "\n" + Saved, "ambiguous")]
    public void Uncertain_anchors_never_report_a_line(string? displayed, string expected)
    {
        Assert.Equal(new(expected, null, null), CodeReview.Locate(Anchor(), Saved, displayed));
    }

    [Fact]
    public void File_comments_stay_file_comments_and_sides_are_mapped_independently()
    {
        Assert.Equal(new("current", 0, 0), CodeReview.Locate(Anchor(0, 0), Saved, Saved));
        Assert.Equal(new("changed", null, null), CodeReview.Locate(Anchor(0, 0), Saved, "inserted\n" + Saved));
        var original = Anchor() with { Side = "original" };
        Assert.Equal(new("current", 3, 4), CodeReview.Locate(original, Saved, Saved));
        Assert.Equal(new("changed", null, null), CodeReview.Locate(original, Saved, "different modified code"));
    }

    [Fact]
    public void First_and_last_lines_relocate_without_out_of_range_context()
    {
        Assert.Equal(new("relocated", 2, 2), CodeReview.Locate(Anchor(1, 1), Saved, "inserted\n" + Saved));
        Assert.Equal(new("relocated", 7, 7), CodeReview.Locate(Anchor(6, 6), Saved, "inserted\n" + Saved));
    }
}
