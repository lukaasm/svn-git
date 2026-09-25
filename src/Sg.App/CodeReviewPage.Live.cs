using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

public sealed partial class CodeReviewPage
{
    readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly StatusChip _liveState = new() { Text = "Live updates", Glyph = "\uE72C" };
    ReviewFeed? _feed;
    Task<ReviewFeed>? _feedStart;
    Window? _observedWindow;
    string? _feedbackRevision, _liveError, _resolvedLocally;
    IReadOnlyList<string> _knownFiles = [];
    bool _hidden = true, _reading, _refreshing, _feedbackPending, _draftsPending, _keepResolved;

    void StartLiveUpdates()
    {
        _hidden = false;
        if (_observedWindow != Window)
        {
            if (_observedWindow != null) _observedWindow.Activated -= Activated;
            _observedWindow = Window;
            if (_observedWindow != null) _observedWindow.Activated += Activated;
        }
    }
    void StopLiveUpdates()
    {
        _hidden = true; _liveTimer.Stop();
        StopSourceUpdates();
        _feed?.Dispose(); _feed = null; _feedStart = null;
        if (_observedWindow != null) _observedWindow.Activated -= Activated;
        _observedWindow = null;
    }
    void Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        QueueFeedback(refreshDrafts: true);
        QueueSource();
    }
    async Task<ReviewFeed?> EnsureFeed()
    {
        var root = Session.Require();
        // An explicit reload also rechecks identity: a restore can replace the worktree at this path.
        _feed?.Dispose(); _feed = null;
        var pending = _feedStart = Task.Run(() => CodeReview.Follow(root, _path));
        try
        {
            var feed = await pending;
            if (_hidden || !ReferenceEquals(_feedStart, pending)) { feed.Dispose(); return null; }
            if (_feed != feed)
            {
                _feed = feed;
                feed.Changed += () => DispatcherQueue.TryEnqueue(() => { if (_feed == feed) QueueFeedback(); });
            }
            return feed;
        }
        catch (Exception e)
        {
            if (!_hidden && ReferenceEquals(_feedStart, pending)) { _feedStart = null; Error(e.Message); }
            return null;
        }
    }
    void QueueFeedback(bool refreshDrafts = false)
    {
        if (_hidden) return;
        _feedbackPending = true; _draftsPending |= refreshDrafts;
        SetLiveStatus(); ScheduleFeedback();
    }
    void ScheduleFeedback()
    {
        if (_hidden || !_feedbackPending || _writing || _reading || _refreshing || _filter.IsDropDownOpen) return;
        // The first event schedules the read. Further events share that read instead of postponing it forever.
        if (!_liveTimer.IsEnabled) _liveTimer.Start();
    }
    void SetLiveStatus()
    {
        var problem = _liveError ?? _feed?.WatchError;
        _liveState.Text = problem != null ? "Live updates paused · Refresh to retry"
            : _writing && _feedbackPending ? "New feedback waiting" : "Live updates";
        _liveState.Severity = problem != null ? ChipSeverity.Caution : ChipSeverity.Neutral;
        ToolTipService.SetToolTip(_liveState, problem ?? "Replies update here automatically. Open drafts and the code you are reading stay in place.");
    }
    bool UpdateFiles()
    {
        var before = _selectedFile;
        var counts = _data.Threads.GroupBy(t => t.Anchor.File).ToDictionary(g => g.Key, g => (Open: g.Count(t => t.State == "open"), Total: g.Count()));
        var paths = _knownFiles.Concat(counts.Keys).Concat(_savedDrafts.Values.Select(d => d.File))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!_filePaths.SequenceEqual(paths) || _fileList.Count == 0)
        {
            var firstLoad = _filePaths.Length == 0;
            _loadingFiles = true;
            _filePaths = paths;
            _fileList.SetItems(paths.Select(f => new FileRow { Path = f }), "Files", preserveView: true);
            _selectedFile = paths.Contains(before) ? before : paths.FirstOrDefault();
            if (_returning is { } view)
            {
                _fileList.RestoreView(view.Files); _returning = null;
            }
            else if (_selectedFile != null && (firstLoad || before != _selectedFile)) _fileList.Select(row => row.TreePath == _selectedFile, reveal: before == null);
            _loadingFiles = false;
        }
        var badges = new Dictionary<string, TreeBadge>();
        foreach (var path in paths)
        {
            var (open, total) = counts.GetValueOrDefault(path);
            var draft = _savedDrafts.Values.Any(d => d.File == path);
            if (total == 0 && !draft) continue;
            var text = open > 0 ? $"{open} open" : total > 0 ? $"{total} resolved" : "";
            if (draft) text += text.Length > 0 ? " · draft" : "Draft";
            badges[path] = new(text, draft ? "\uE70F" : open > 0 ? "\uE90A" : "\uE73E", open > 0 ? ChipSeverity.Attention : total > 0 ? ChipSeverity.Success : ChipSeverity.Neutral);
        }
        _fileList.SetBadges(badges);
        _comment.Text = _savedDrafts.ContainsKey("comment:" + _selectedFile) ? "Resume draft" : "Comment";
        return before != _selectedFile;
    }
    sealed record Feedback(ReviewSnapshot Snapshot, Dictionary<string, ReviewLocation> Locations, IReadOnlyDictionary<string, ReviewDraft>? Drafts);
    async Task RefreshFeedback()
    {
        if (_hidden || _feed == null || !_feedbackPending || _writing || _reading || _refreshing || _filter.IsDropDownOpen) return;
        var feed = _feed; var displayed = _file; var drafts = _draftsPending;
        _feedbackPending = _draftsPending = false; _refreshing = true;
        try
        {
            var result = await Task.Run(() =>
            {
                feed.Reconnect();
                var snapshot = feed.Read();
                var locations = LocateThreads(snapshot.Data, displayed);
                return new Feedback(snapshot, locations, drafts ? _drafts.List(feed.Identity) : null);
            });
            if (_hidden || _feed != feed) return;
            if (_writing || _reading || _file != displayed || _filter.IsDropDownOpen)
            { _feedbackPending = true; _draftsPending |= drafts; return; }
            if (_notice.Message == _liveError) _notice.IsOpen = false;
            _liveError = null;
            var changed = result.Snapshot.Revision != _feedbackRevision;
            if (!changed && result.Drafts == null) return;
            var selected = result.Snapshot.Data.Threads.FirstOrDefault(t => t.Id == _currentThread);
            if (selected?.State == "open") _keepResolved = false;
            else if (selected?.State == "resolved" && _data.Threads.FirstOrDefault(t => t.Id == _currentThread)?.State == "open")
                _keepResolved = _currentThread != _resolvedLocally;
            _resolvedLocally = null;
            _data = result.Snapshot.Data; _feedbackRevision = result.Snapshot.Revision;
            _locations = result.Locations;
            if (result.Drafts != null) _savedDrafts = result.Drafts;
            if (UpdateFiles()) { ++_navigationRequest; await LoadFile(); }
            else RenderThreads(preserve: true);
        }
        catch (Exception e)
        {
            if (!_hidden && _feed == feed) { _liveError = "Feedback could not be refreshed. " + e.Message; Error(_liveError); }
        }
        finally { _refreshing = false; if (!_hidden) { SetLiveStatus(); ScheduleFeedback(); } }
    }
}
