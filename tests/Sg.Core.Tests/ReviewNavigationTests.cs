namespace Sg.Core.Tests;

public sealed class ReviewNavigationTests
{
    static CodeThread Thread(string id, string file, int line, bool resolved = false) => new()
    {
        Id = id, Anchor = new(file, "modified", line, line, "", "", ""),
        Events = [new(id, [], resolved ? "resolve" : "comment", "Feedback", "Reviewer", DateTimeOffset.UtcNow, "")],
    };

    [Fact]
    public void Navigation_wraps_across_files_skipping_resolved_and_including_file_comments()
    {
        var threads = new[] { Thread("c", "c.cs", 9), Thread("b", "b.cs", 3, true), Thread("a", "a.cs", 0) };
        Assert.Equal("c", ReviewNavigation.Next(threads, "a", "a.cs", true)?.Id);
        Assert.Equal("a", ReviewNavigation.Next(threads, "c", "c.cs", true)?.Id);
        Assert.Equal("c", ReviewNavigation.Next(threads, "a", "a.cs", false)?.Id);
        Assert.Equal("a", ReviewNavigation.Next(threads, "c", "c.cs", false)?.Id);
    }

    [Fact]
    public void Resolving_current_thread_retains_its_position()
    {
        var threads = new[] { Thread("first", "a.cs", 1), Thread("current", "a.cs", 2, true), Thread("next", "a.cs", 3) };
        Assert.Equal("next", ReviewNavigation.Next(threads, "current", "a.cs", true)?.Id);
        Assert.Equal("first", ReviewNavigation.Next(threads, "current", "a.cs", false)?.Id);
    }

    [Fact]
    public void Starting_from_file_without_feedback_chooses_adjacent_file_and_no_open_means_no_target()
    {
        var threads = new[] { Thread("c", "c.cs", 9), Thread("a", "a.cs", 0) };
        Assert.Equal("c", ReviewNavigation.Next(threads, null, "b.cs", true)?.Id);
        Assert.Equal("a", ReviewNavigation.Next(threads, null, "b.cs", false)?.Id);
        Assert.Null(ReviewNavigation.Next([Thread("x", "a.cs", 0, true)], null, null, true));
        Assert.Null(ReviewNavigation.Next([], null, null, false));
    }
}
