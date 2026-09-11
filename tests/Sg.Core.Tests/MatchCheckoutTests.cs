using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Which checkout an export belongs to. The name it had elsewhere is never the answer - the same
/// repository is often registered under another name, and two repositories under the same one - so the
/// URL is the key. But a URL is how one machine reaches a server, and the far side reaches it its own
/// way: http here and https there, a host name here and its address there. Matching the strings refused
/// a branch its own checkout; matching too loosely would rebuild it on a tree it has nothing to do with.
/// </summary>
public sealed class MatchCheckoutTests : IDisposable
{
    readonly string _dir;
    readonly SgRoot _root;

    public MatchCheckoutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sgmc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _root = Ops.Init(_dir, new CollectingLog(), fsmonitor: false);
    }

    /// <summary>git writes its objects read-only, and a plain recursive delete refuses those.</summary>
    public void Dispose()
    {
        try { SgRoot.SweepTempDir(_dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the test still said what it had to say */ }
    }

    /// <summary>A checkout registered here. The folder is never visited: only its URL is being matched.</summary>
    void Registered(string name, string url) =>
        _root.Config.Checkouts.Add(new CheckoutConfig { Name = name, Path = Path.Combine(_dir, name), Url = url });

    static ExportMeta From(string url) => new()
    {
        Branch = "gui",
        Checkout = "Fort",
        Bases = [new ExportWc { Rel = "", Url = url, Revision = 266 }],
    };

    [Fact]
    public void TheSameUrl_Matches()
    {
        Registered("fort", "https://svn.example.com/svn/mono/branches/fort");

        var co = Export.MatchCheckout(_root, From("https://svn.example.com/svn/mono/branches/fort"));

        Assert.Equal("fort", co?.Name);
    }

    /// <summary>
    /// The case this was written for: one machine set up for http and the other for https, to one server.
    /// Every other line of the export agreed; the scheme was the whole disagreement.
    /// </summary>
    [Fact]
    public void HttpHereAndHttpsThere_IsStillTheSameRepository()
    {
        Registered("fort", "https://svn.example.com/svn/mono/branches/fort");

        var co = Export.MatchCheckout(_root, From("http://svn.example.com/svn/mono/branches/fort"));

        Assert.Equal("fort", co?.Name);
    }

    [Fact]
    public void ATrailingSlashOrADefaultPortOrAUserName_ChangesNothing()
    {
        Registered("fort", "https://svn.example.com/svn/mono/branches/fort");

        Assert.Equal("fort", Export.MatchCheckout(_root, From("https://svn.example.com/svn/mono/branches/fort/"))?.Name);
        Assert.Equal("fort", Export.MatchCheckout(_root, From("http://svn.example.com:80/svn/mono/branches/fort"))?.Name);
        Assert.Equal("fort", Export.MatchCheckout(_root, From("https://bob@SVN.Example.com/svn/mono/branches/fort"))?.Name);
    }

    /// <summary>
    /// One server answering to a name and to an address. The export of this tool carried both spellings in
    /// the same file, so this is not a hypothetical. The path has to match whole, and only one checkout
    /// may be at it, or nothing is answered.
    /// </summary>
    [Fact]
    public void OneServerReachedByNameHereAndByAddressThere_IsMatchedByThePath()
    {
        Registered("fort", "https://svn.example.com/svn/mono/branches/fort");

        var co = Export.MatchCheckout(_root, From("http://192.168.0.253/svn/mono/branches/fort"));

        Assert.Equal("fort", co?.Name);
    }

    [Fact]
    public void ADifferentPathOnTheSameServer_IsNotAMatch()
    {
        Registered("fort", "https://svn.example.com/svn/mono/branches/fort");

        Assert.Null(Export.MatchCheckout(_root, From("https://svn.example.com/svn/mono/trunk")));
        Assert.Null(Export.MatchCheckout(_root, From("https://svn.example.com/svn/other/branches/fort")));
        // Case is not a difference here, though SVN itself would call it one. See Export.NormalizeUrl.
        Assert.Equal("fort", Export.MatchCheckout(_root, From("https://svn.example.com/svn/mono/branches/FORT"))?.Name);
    }

    /// <summary>
    /// Two checkouts at that path on two hosts. Which one is meant is not something a rule can know, and
    /// picking either would rebuild the branch somewhere it may not belong. --into is the answer.
    /// </summary>
    [Fact]
    public void TwoCandidatesAtThePath_AnswerNothingRatherThanGuess()
    {
        Registered("mirror-a", "https://a.example.com/svn/mono/branches/fort");
        Registered("mirror-b", "https://b.example.com/svn/mono/branches/fort");

        Assert.Null(Export.MatchCheckout(_root, From("http://192.168.0.253/svn/mono/branches/fort")));
    }

    /// <summary>The exact URL still wins outright, even when another checkout is at the same path elsewhere.</summary>
    [Fact]
    public void AnExactMatchIsTakenOverANearOne()
    {
        Registered("elsewhere", "https://other.example.com/svn/mono/branches/fort");
        Registered("fort", "https://svn.example.com/svn/mono/branches/fort");

        var co = Export.MatchCheckout(_root, From("https://svn.example.com/svn/mono/branches/fort"));

        Assert.Equal("fort", co?.Name);
    }

    /// <summary>
    /// When nothing matches, the reader is the only one who can say whether two spellings are one server -
    /// and they cannot say it from a URL they were never shown. So the message shows all of them, and
    /// names the near one.
    /// </summary>
    [Fact]
    public void WhenNothingMatches_TheMessageNamesWhatIsRegistered()
    {
        Registered("fort", "https://svn.example.com/svn/mono/branches/fort");
        Registered("other", "https://svn.example.com/svn/other/trunk");

        var why = Export.NoMatch(_root, From("http://192.168.0.253/svn/mono/branches/fort"));

        Assert.Contains("fort", why);
        Assert.Contains("https://svn.example.com/svn/mono/branches/fort", why);
        Assert.Contains("other", why);
        Assert.Contains("--into", why);
    }

    [Fact]
    public void WithNoCheckoutsAtAll_TheMessageSaysThat()
    {
        var why = Export.NoMatch(_root, From("https://svn.example.com/svn/mono/branches/fort"));

        Assert.Contains("no checkouts", why);
        Assert.DoesNotContain("Registered here:", why);
    }
}
