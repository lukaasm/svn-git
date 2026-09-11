using System.Security.Principal;
using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Noticing that sg is running as administrator. Nobody chooses this for sg: it is inherited from the
/// terminal that started it, and the terminal was elevated because an installer asked. The first thing
/// it breaks is every folder picker in the app, so the app has to say it before the first one is
/// opened - which means this has to be right, and cheap enough to ask on the way to the first window.
/// </summary>
public sealed class ElevationTests : IDisposable
{
    /// <summary>The answer is cached, and these tests set it. Put it back so nothing else inherits one.</summary>
    public void Dispose() => Elevation.Override(null);

    /// <summary>
    /// The real answer, against the same question asked independently. Not a tautology: it checks the
    /// plumbing - that the token is read, that nothing throws, and that the cache holds one answer.
    /// </summary>
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TheAnswerMatchesTheTokenThisProcessIsActuallyRunningWith()
    {
        if (!OperatingSystem.IsWindows()) return;
        Elevation.Override(null);

        using var identity = WindowsIdentity.GetCurrent();
        var expected = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        Assert.Equal(expected, Elevation.IsElevated);
    }

    /// <summary>A token does not change under a running process, so neither may the answer.</summary>
    [Fact]
    public void TheAnswerIsTheSameEveryTimeItIsAsked()
    {
        Elevation.Override(null);

        var first = Elevation.IsElevated;

        Assert.Equal(first, Elevation.IsElevated);
        Assert.Equal(first, Elevation.IsElevated);
    }

    [Fact]
    public void WhenElevated_ThereIsSomethingToSay()
    {
        Elevation.Override(true);

        Assert.True(Elevation.IsElevated);
        Assert.True(Elevation.ShouldWarn);
    }

    [Fact]
    public void WhenNotElevated_NothingIsSaid()
    {
        Elevation.Override(false);

        Assert.False(Elevation.IsElevated);
        Assert.False(Elevation.ShouldWarn);
    }

    /// <summary>
    /// The words themselves, because they are the whole feature: a warning that does not say what is
    /// wrong, what it costs and what to do instead is a warning that gets dismissed. They live in the
    /// core rather than in the app so a test can read them.
    /// </summary>
    [Fact]
    public void TheWarningSaysWhatItCostsAndWhatToDo()
    {
        Assert.Contains("does not need administrator", Elevation.Warning);
        // The consequence the reader will otherwise meet as an unexplained failure.
        Assert.Contains("access denied", Elevation.Warning);
        // How it happened, since it is never a decision.
        Assert.Contains("elevated terminal", Elevation.Warning);
        // And the way out.
        Assert.Contains("ordinary terminal", Elevation.Warning);
        Assert.Contains("administrator", Elevation.Title);
    }

    /// <summary>A dialog is one screen. A warning that does not fit on one is one nobody finishes.</summary>
    [Fact]
    public void TheWarningIsShortEnoughToRead()
    {
        Assert.InRange(Elevation.Warning.Length, 100, 900);
        Assert.InRange(Elevation.Title.Length, 10, 60);
    }
}
