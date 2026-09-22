using Sg.Core;

namespace Sg.App;

/// <summary>Read-only form validation. Core operations still recheck the destination when executed.</summary>
internal sealed record BranchTarget(bool Taken, string? Error, TaskFollowUp? Existing = null)
{
    public static BranchTarget Check(SgRoot root, string name)
    {
        try
        {
            root.Git.CheckBranchName(name);
            var taken = root.Git.RefSha("refs/heads/" + name) != null;
            var path = root.WorktreePathFor(name);
            if (!taken && (Directory.Exists(path) || File.Exists(path)))
                return new(false, "The destination folder already exists. Choose another branch name or move that folder first: " + path,
                    new(TaskTargetKind.Folder, Directory.Exists(path) ? path : Path.GetDirectoryName(path)!));
            if (!taken) return new(false, null);
            var tree = root.Git.WorktreeList().FirstOrDefault(w => !w.Bare && (w.Branch == name
                || w.Branch == null && Directory.Exists(w.Path) && root.Git.RebaseHeadName(w.Path) == name));
            var operation = Operations.List(root).FirstOrDefault(o => !o.Terminal && o.Branch == name);
            var existing = tree?.Path ?? operation?.Path;
            if (existing != null && Directory.Exists(existing))
                return new(true, null, new(Conflicts.HasPending(root.Git, existing) ? TaskTargetKind.Replay
                    : operation != null ? TaskTargetKind.Update : TaskTargetKind.Folder, existing));
            return new(true, null, operation == null ? null : new(TaskTargetKind.Activity));
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

/// <summary>
/// One form's destination checks. New input cancels the debounce; obsolete Git results never reach
/// the form. Call on the UI thread, and invalidate before loading a source or presenting a result.
/// </summary>
internal sealed class BranchTargetValidation
{
    CancellationTokenSource? _pending;

    public void Invalidate()
    {
        _pending?.Cancel();
        _pending = null;
    }

    /// <returns>The latest check, or null when superseded or the active root changed.</returns>
    public async Task<BranchTarget?> CheckAsync(SgRoot? root, string name)
    {
        Invalidate();
        if (root == null || name.Length == 0) return new(false, null);
        using var pending = new CancellationTokenSource();
        _pending = pending;
        try
        {
            await Task.Delay(200, pending.Token);
            if (root != Session.Root) return null;
            var result = await Task.Run(() => BranchTarget.Check(root, name), pending.Token);
            return !pending.IsCancellationRequested && root == Session.Root ? result : null;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { return null; }
        finally
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }
    }
}
