using Sg.Core;

namespace Sg.App;

/// <summary>
/// The words a page uses for the server behind a checkout. An SVN checkout reads the way it always has;
/// a git clone says git, and names the branch it sends to where SVN would say SVN.
/// </summary>
static class ServerWords
{
    /// <summary>"SVN" or "git", for the middle of a sentence.</summary>
    public static string Name(CheckoutConfig? co) => co?.IsGit == true ? "git" : "SVN";

    /// <summary>Where a commit goes: SVN, or the remote branch a git clone tracks.</summary>
    public static string Target(CheckoutConfig co) => co.IsGit ? $"{co.Remote}/{co.Branch}" : "SVN";

    /// <summary>The label of the button that sends checkout changes to the server.</summary>
    public static string CommitButton(CheckoutConfig co, int picked = 0) =>
        co.IsGit
            ? picked == 0 ? "Commit and push" : $"Commit and push {picked}"
            : picked == 0 ? "Commit to SVN" : $"Commit {picked} to SVN";

    /// <summary>The page title of a branch push: where the branch goes.</summary>
    public static string PushTitle(CheckoutConfig? co) => co?.IsGit == true ? $"Push to {co.Remote}/{co.Branch}" : "Push to SVN";

    /// <summary>What the working copies inside a checkout are called: SVN externals, or git submodules.</summary>
    public static string Externals(CheckoutConfig? co) => co?.IsGit == true ? "submodules" : "externals";

    /// <summary>The same, as a heading.</summary>
    public static string ExternalsTitle(CheckoutConfig? co) => co?.IsGit == true ? "Submodules" : "Externals";

    /// <summary>The history of the server, as the checkout's log page calls it.</summary>
    public static string LogTitle(CheckoutConfig co) => co.IsGit ? "Server log" : "SVN log";
}
