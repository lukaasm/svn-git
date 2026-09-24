namespace Sg.Core;

/// <summary>Observe the selected review file. Version probes read just that bounded text file,
/// without Git processes or repository scans. A changed version never replaces the displayed snapshot.</summary>
public sealed class ReviewSource : IDisposable
{
    readonly FileObservation _observation;
    readonly string _worktree, _file;
    internal ReviewSource(string worktree, string file)
    {
        _worktree = worktree; _file = file;
        _observation = new(CodeReview.DiskPath(worktree, file), worktree);
    }
    public string? WatchError => _observation.Error;
    public event Action? Changed { add => _observation.Changed += value; remove => _observation.Changed -= value; }
    public void Reconnect()
    {
        // A replaced folder must still pass the same link checks as a source read.
        _ = CodeReview.DiskPath(_worktree, _file);
        _observation.Reconnect();
    }
    public string ReadVersion() => CodeReview.CurrentVersion(_worktree, _file);
    public void Dispose() => _observation.Dispose();
}
