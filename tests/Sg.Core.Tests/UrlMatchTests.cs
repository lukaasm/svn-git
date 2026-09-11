using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Telling one repository from another across two machines. A URL is not the repository: it is how one
/// machine reaches it. The same server answers to http here and https there, to a name here and to the
/// address it resolves to there - an export of this tool carried both spellings of one server in the
/// same file - and an importer that compared the strings refused to put a branch back on the checkout
/// it came from.
/// </summary>
public sealed class UrlMatchTests
{
    [Theory]
    // The scheme is how you reach it, not what it is.
    [InlineData("http://svn.example.com/svn/repo/trunk", "https://svn.example.com/svn/repo/trunk")]
    // A trailing slash is nothing.
    [InlineData("https://svn.example.com/svn/repo/trunk/", "https://svn.example.com/svn/repo/trunk")]
    // DNS does not care about the case of a host.
    [InlineData("https://SVN.Example.COM/svn/repo/trunk", "https://svn.example.com/svn/repo/trunk")]
    // A default port written out is the same port.
    [InlineData("http://svn.example.com:80/svn/repo/trunk", "https://svn.example.com/svn/repo/trunk")]
    [InlineData("https://svn.example.com:443/svn/repo/trunk", "http://svn.example.com/svn/repo/trunk")]
    [InlineData("svn://svn.example.com:3690/repo/trunk", "svn://svn.example.com/repo/trunk")]
    // A user name in front of the host says who is asking, not what is being asked for.
    [InlineData("https://alice@svn.example.com/svn/repo/trunk", "https://svn.example.com/svn/repo/trunk")]
    // Case, all of it. Looser than SVN itself, which is case-sensitive in the path: sg has compared
    // checkout URLs this way throughout, and answering the wrong branch is a thing the reader sees at
    // once, where refusing the right one leaves them with a file and no way to put it back.
    [InlineData("https://svn.example.com/svn/repo/TRUNK", "https://svn.example.com/svn/repo/trunk")]
    public void TwoWaysOfWritingOneServer_ComeOutTheSame(string a, string b) =>
        Assert.Equal(Export.NormalizeUrl(a), Export.NormalizeUrl(b));

    [Theory]
    // A different host is a different place until something says otherwise. The importer has a later step
    // for that, and it is a step, not this one.
    [InlineData("https://svn.example.com/svn/repo/trunk", "https://192.168.0.1/svn/repo/trunk")]
    // A non-default port is part of reaching it.
    [InlineData("https://svn.example.com:8443/svn/repo/trunk", "https://svn.example.com/svn/repo/trunk")]
    // A different path is a different thing.
    [InlineData("https://svn.example.com/svn/repo/branches/x", "https://svn.example.com/svn/repo/trunk")]
    public void ThingsThatAreActuallyDifferent_StayDifferent(string a, string b) =>
        Assert.NotEqual(Export.NormalizeUrl(a), Export.NormalizeUrl(b));

    /// <summary>A path with no scheme at all is left alone rather than half parsed.</summary>
    [Fact]
    public void SomethingThatIsNotAUrl_IsLeftAsItIs()
    {
        Assert.Equal("not a url", Export.NormalizeUrl("not a url"));
        Assert.Equal("", Export.NormalizeUrl(""));
        Assert.Equal("", Export.NormalizeUrl("   "));
    }

    /// <summary>An IPv6 host is bracketed, and the colons inside the brackets are not a port.</summary>
    [Fact]
    public void AnIpv6HostKeepsItsColons()
    {
        Assert.Equal("[2001:db8::1]/svn/repo", Export.NormalizeUrl("https://[2001:db8::1]/svn/repo"));
        Assert.Equal(Export.NormalizeUrl("https://[2001:db8::1]:443/svn/repo"),
                     Export.NormalizeUrl("http://[2001:db8::1]/svn/repo"));
    }
}
