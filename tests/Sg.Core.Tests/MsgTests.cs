using Xunit;

namespace Sg.Core.Tests;

/// <summary>The prefix the commit page starts a message with, read off the branch's last one.</summary>
public sealed class MsgTests
{
    [Theory]
    [InlineData("gui: the stack layout skips its mask group", "gui:")]
    [InlineData("gui_editor: the Item strip compares a whole name", "gui_editor:")]
    [InlineData("log and push: a filter over the commits", "log and push:")]
    [InlineData("Fixes: 12", "Fixes:")]
    [InlineData("gui:", "gui:")]
    [InlineData("gui: first line\n\nbody: with a colon of its own", "gui:")]
    [InlineData("gui: first line\r\nsecond", "gui:")]
    public void Prefix_IsTheWordsBeforeTheFirstColon(string message, string expected) =>
        Assert.Equal(expected, Msg.Prefix(message));

    [Theory]
    [InlineData("Fort r266")]                                   // a snapshot: no colon at all
    [InlineData("Fix 15 code review findings across the grid")]
    [InlineData("see http://svn.exor/svn/x")]                   // the colon is inside a URL
    [InlineData("12:30 standup notes")]                         // a time, and digits alone are not a name
    [InlineData("one two three four: too many words")]
    [InlineData("a prefix that is far too long to be one: x")]
    [InlineData("what?: punctuation is not a name")]
    [InlineData(": nothing before it")]
    [InlineData("")]
    [InlineData(null)]
    public void Prefix_IsNullWhenTheSubjectHasNone(string? message) =>
        Assert.Null(Msg.Prefix(message));
}
