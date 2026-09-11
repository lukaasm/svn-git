using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Rel, IsUnder and HasReservedName run against every path git and svn report, so they are written
/// to allocate nothing. These pin the answers they used to give.
/// </summary>
public sealed class PathUtilTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("./", "")]
    [InlineData("./.", "")]
    [InlineData(".//", "")]
    [InlineData("a", "a")]
    [InlineData("a/", "a")]
    [InlineData("a//", "a")]
    [InlineData("./a", "a")]
    [InlineData("././a", "a")]
    [InlineData("a/b", "a/b")]
    [InlineData("a\\b", "a/b")]
    [InlineData(".\\a\\b\\", "a/b")]
    [InlineData("a/.", "a/.")]
    [InlineData("./a/b/", "a/b")]
    public void Rel_NormalisesSlashesAndEnds(string input, string expected) =>
        Assert.Equal(expected, PathUtil.Rel(input));

    [Theory]
    [InlineData("a/b", "", true)]
    [InlineData("a/b", "a", true)]
    [InlineData("a/b", "A", true)]
    [InlineData("a/b", "a/b", true)]
    [InlineData("a/b", "a/b/c", false)]
    [InlineData("ab/c", "a", false)]
    [InlineData("a", "a/b", false)]
    [InlineData("abc", "abd", false)]
    public void IsUnder_MatchesTheWholeSegment(string rel, string prefix, bool expected) =>
        Assert.Equal(expected, PathUtil.IsUnder(rel, prefix));

    [Theory]
    [InlineData("CON", true)]
    [InlineData("con", true)]
    [InlineData("NUL.txt", true)]
    [InlineData("CON.", true)]
    [InlineData("CON.a.b", true)]
    [InlineData("COM1", true)]
    [InlineData("lpt9.log", true)]
    [InlineData("a/b/AUX/c", true)]
    [InlineData("a/PRN", true)]
    [InlineData("CONS", false)]
    [InlineData("COM0", false)]
    [InlineData("COM10", false)]
    [InlineData("LPT", false)]
    [InlineData("ACON", false)]
    [InlineData("connect.c", false)]
    [InlineData("", false)]
    [InlineData("a/b/c.txt", false)]
    public void HasReservedName_FindsTheNamesWindowsRefuses(string rel, bool expected) =>
        Assert.Equal(expected, PathUtil.HasReservedName(rel));
}
