using System.Runtime.Versioning;
using System.Security.Principal;

namespace Sg.Core;

/// <summary>
/// Whether this process is running as administrator, and why that is worth saying out loud.
///
/// sg has no use for admin. It reads and writes a checkout, a git store and the user's own profile,
/// and every one of those belongs to the user already. Running it elevated only costs: files it
/// writes are owned by the elevated token, so the next ordinary run cannot replace them, and that
/// shows up as an access denied which reads like a reason to elevate again.
///
/// The way it happens is never a decision. An installer asks for admin, and everything started from
/// that terminal afterwards is elevated too - which is how the pickers came to be run elevated and
/// throw. So this is checked once, said once, and left to the reader.
/// </summary>
public static class Elevation
{
    static bool? _elevated;

    /// <summary>
    /// True when this process holds the administrators role. Answered once: a token does not change
    /// under a running process, and the answer is read on a path that must not do work.
    /// </summary>
    public static bool IsElevated => _elevated ??= Ask();

    static bool Ask()
    {
        // The guard is the platform check the analyser wants, and it has to be here rather than on
        // the method: this is called from a property that any platform may read.
        if (!OperatingSystem.IsWindows()) return false;
        return AskWindows();
    }

    [SupportedOSPlatform("windows")]
    static bool AskWindows()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A token that cannot be read is not a reason to refuse to start. Say no and carry on:
            // the cost of missing this is a warning nobody sees, and the cost of throwing is the app.
            return false;
        }
    }

    /// <summary>The title of the one thing sg says about it.</summary>
    public const string Title = "sg is running as administrator";

    /// <summary>
    /// What it costs, in the order the reader meets it. Written here rather than in the app so the
    /// words are covered by a test and are the same wherever they are shown.
    /// </summary>
    public static string Warning =>
        "sg does not need administrator, and running as one causes trouble it cannot undo:\n\n"
        + "•  Files it writes - the git store, a worktree, its own settings - end up owned by the "
        + "elevated token. Ordinary runs then cannot replace them, which appears as \"access denied\".\n\n"
        + "•  Anything started from an elevated terminal is elevated too. That is almost always how "
        + "this happens: an installer asked for admin, and sg was started from the same window.\n\n"
        + "Close sg and start it from an ordinary terminal, or from the Start menu.";

    /// <summary>
    /// Whether that warning is worth showing at all. Only true where it is both true and actionable:
    /// nothing is gained by telling someone on a machine sg cannot ask about.
    /// </summary>
    public static bool ShouldWarn => OperatingSystem.IsWindows() && IsElevated;

    /// <summary>For tests, and for a caller that has already worked the answer out another way.</summary>
    public static void Override(bool? elevated) => _elevated = elevated;
}
