using Sg.Core;

namespace Sg.App;

/// <summary>Read-only form validation. Core operations still recheck the destination when executed.</summary>
internal sealed record BranchTarget(bool Taken, string? Error)
{
    public static BranchTarget Check(SgRoot root, string name)
    {
        try
        {
            root.Git.CheckBranchName(name);
            var taken = root.Git.RefSha("refs/heads/" + name) != null;
            var path = root.WorktreePathFor(name);
            if (!taken && (Directory.Exists(path) || File.Exists(path)))
                return new(false, "The destination folder already exists. Choose another branch name or move that folder first: " + path);
            return new(taken, null);
        }
        catch (SgException e)
        {
            return new(false, e.Message.StartsWith("bad branch name:", StringComparison.Ordinal)
                ? "Choose a valid Git branch name, such as feature/fix. Spaces, '..', and special ref characters are not allowed."
                : "Could not check this name: " + e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "Could not check the destination: " + e.Message);
        }
    }
}
