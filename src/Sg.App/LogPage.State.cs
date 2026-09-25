using Sg.Core;

namespace Sg.App;

public sealed partial class LogPage
{
    sealed record ViewState(string Query, string[] Selected, string Current, bool Snapshots,
        BrowseScroll.Anchor? Scroll, Dictionary<string, ListFilter.ViewState> Files);
    readonly Dictionary<string, ListFilter.ViewState> _fileViews = new(StringComparer.Ordinal);
    readonly PageReads _listReads = new(), _commitReads = new(), _patchReads = new();
    readonly ListViewport _commitsViewport;
    ViewState? _returning;
    string? _filesSha;
    bool _hidden = true;

    void RememberFiles()
    {
        if (_filesSha == null) return;
        _fileViews.Remove(_filesSha);
        _fileViews[_filesSha] = _filter.CaptureView();
        while (_fileViews.Count > 32) _fileViews.Remove(_fileViews.Keys.First());
    }
    internal override object? CaptureViewState()
    {
        RememberFiles();
        return (_returning ?? new ViewState(CommitFilterBox.Text, Commits.SelectedItems.OfType<CommitRow>()
            .Where(r => !r.IsGroup).Select(r => r.Sha).ToArray(), _currentSha, _snapshotsOpen,
            _commitsViewport.Capture(), new(_fileViews))) with { Query = CommitFilterBox.Text };
    }
    internal override void RestoreViewState(object? state)
    {
        if (state is not ViewState view) return;
        _returning = view; CommitFilterBox.Text = view.Query; _snapshotsOpen = view.Snapshots;
        foreach (var (sha, files) in view.Files) _fileViews[sha] = files;
    }
    public override void OnShown(bool returning) { _hidden = false; _ = LoadAsync(); }
    public override void OnHidden()
    {
        _hidden = true; _listReads.Cancel(); _commitReads.Cancel(); _patchReads.Cancel();
    }
    async Task RestoreSelection(ViewState view)
    {
        var rows = Commits.Items.OfType<CommitRow>().Where(r => !r.IsGroup && view.Selected.Contains(r.Sha)).ToArray();
        // A rewritten or removed commit invalidates the entire requested set, including rewrite actions.
        if (rows.Length == view.Selected.Length)
        {
            _rebuilding = true;
            foreach (var row in rows) Commits.SelectedItems.Add(row);
            _rebuilding = false;
            SyncRewriteButtons();
            _commitsViewport.Restore(view.Scroll);
            if (rows.FirstOrDefault(r => r.Sha == view.Current) is { } current) await LoadCommitAsync(current);
        }
        else _commitsViewport.Restore(view.Scroll);
    }
}
