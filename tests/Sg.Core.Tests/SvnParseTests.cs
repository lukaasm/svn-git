using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// ParseStatus reads the answer as it arrives instead of building a document, because a snapshot asks
/// for the whole tree and gets tens of megabytes back. These pin what it takes out of svn's shapes.
/// </summary>
public sealed class SvnParseTests
{
    [Fact]
    public void RootTargetKeepsPathsAsSvnGivesThem()
    {
        var entries = Svn.ParseStatus("""
            <?xml version="1.0" encoding="UTF-8"?>
            <status>
            <target path=".">
            <entry path="CMakeLists.txt"><wc-status props="none" item="modified" revision="12"/></entry>
            <entry path="junk.txt"><wc-status props="none" item="unversioned"/></entry>
            <entry path="fort/dev"><wc-status props="none" item="external"/></entry>
            </target>
            </status>
            """);

        Assert.Equal(3, entries.Count);
        Assert.Equal(("CMakeLists.txt", "modified", "none"), (entries[0].Path, entries[0].Item, entries[0].Props));
        Assert.Equal(("junk.txt", "unversioned"), (entries[1].Path, entries[1].Item));
        Assert.Equal(("fort/dev", "external"), (entries[2].Path, entries[2].Item));
    }

    /// <summary>An external is reported under its own target, and its entry paths are relative to it.</summary>
    [Fact]
    public void ExternalTargetPrefixesItsOwnEntries()
    {
        var entries = Svn.ParseStatus("""
            <status>
            <target path="."><entry path="a.txt"><wc-status item="modified" props="none"/></entry></target>
            <target path="fort/dev">
            <entry path="game.cpp"><wc-status item="modified" props="none"/></entry>
            <entry path="fort/dev/already.cpp"><wc-status item="added" props="none"/></entry>
            <entry path="fort/dev"><wc-status item="modified" props="none"/></entry>
            </target>
            </status>
            """);

        Assert.Equal(["a.txt", "fort/dev/game.cpp", "fort/dev/already.cpp", "fort/dev"], entries.Select(e => e.Path));
    }

    /// <summary>Backslashes and a leading "./" come back normalised, the way every other path does.</summary>
    [Fact]
    public void PathsAreNormalised()
    {
        var entries = Svn.ParseStatus("""
            <status><target path=".\">
            <entry path=".\fort\dev\game.cpp"><wc-status item="modified" props="none"/></entry>
            </target></status>
            """);

        Assert.Equal("fort/dev/game.cpp", Assert.Single(entries).Path);
    }

    /// <summary>svn wraps extra detail around wc-status; only the two attributes sg reads are taken.</summary>
    [Fact]
    public void NestedDetailUnderAnEntryIsIgnored()
    {
        var entries = Svn.ParseStatus("""
            <status><target path=".">
            <entry path="a.txt">
            <wc-status props="normal" item="conflicted" revision="9">
            <commit revision="9"><author>someone</author><date>2026-01-01T00:00:00.000000Z</date></commit>
            </wc-status>
            </entry>
            </target></status>
            """);

        var e = Assert.Single(entries);
        Assert.Equal(("a.txt", "conflicted", "normal"), (e.Path, e.Item, e.Props));
    }

    /// <summary>An entry with no wc-status still counts, with empty item and props, as it always did.</summary>
    [Fact]
    public void EntryWithoutWcStatusStillCounts()
    {
        var entries = Svn.ParseStatus("""
            <status><target path=".">
            <entry path="a.txt"/>
            <entry path="b.txt"><wc-status item="modified" props="none"/></entry>
            </target></status>
            """);

        Assert.Equal(["a.txt", "b.txt"], entries.Select(e => e.Path));
        Assert.Equal(["", "modified"], entries.Select(e => e.Item));
    }

    /// <summary>A changelist sits beside the targets, and its entries were never part of the answer.</summary>
    [Fact]
    public void EntriesOutsideATargetAreLeftOut()
    {
        var entries = Svn.ParseStatus("""
            <status>
            <target path="."><entry path="a.txt"><wc-status item="modified" props="none"/></entry></target>
            <changelist name="work"><entry path="b.txt"><wc-status item="modified" props="none"/></entry></changelist>
            </status>
            """);

        Assert.Equal("a.txt", Assert.Single(entries).Path);
    }

    [Fact]
    public void EmptyStatusGivesNothing()
    {
        Assert.Empty(Svn.ParseStatus("<status><target path=\".\"></target></status>"));
        Assert.Empty(Svn.ParseStatus("<status><target path=\".\"/></status>"));
    }
}
