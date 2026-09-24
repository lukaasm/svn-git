using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

public sealed partial class CodeReviewPage
{
    readonly DispatcherTimer _sourceTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly InfoBar _sourceNotice = new() { IsClosable = false, Severity = InfoBarSeverity.Warning };
    readonly IconButton _reloadCode = new() { Text = "Reload code", Glyph = "\uE72C" };
    ReviewSource? _source;
    int _sourceRequest;
    bool _sourcePending, _probingSource;

    void InitializeSourceUpdates()
    {
        AutomationProperties.SetAutomationId(_sourceNotice, "CodeReviewSourceNotice");
        AutomationProperties.SetAutomationId(_reloadCode, "CodeReviewReloadCode");
        _sourceNotice.ActionButton = _reloadCode;
        _reloadCode.Click += async (_, _) =>
        {
            if (!_writing && !_reading && _file != null) await LoadFile(preserve: true);
        };
        _sourceTimer.Tick += async (_, _) => { _sourceTimer.Stop(); await ProbeSource(); };
    }
    void StopSourceUpdates()
    {
        ++_sourceRequest; _sourceTimer.Stop();
        _source?.Dispose(); _source = null; _sourcePending = false;
        _sourceNotice.IsOpen = false;
    }
    async Task FollowDisplayedSource(ReviewFile displayed)
    {
        StopSourceUpdates();
        var request = _sourceRequest; var root = Session.Require();
        try
        {
            var source = await Task.Run(() => CodeReview.FollowSource(root, _path, displayed.File));
            if (_hidden || request != _sourceRequest || _file != displayed) { source.Dispose(); return; }
            _source = source;
            source.Changed += () => DispatcherQueue.TryEnqueue(() => { if (_source == source) QueueSource(); });
            // Catches a write between the snapshot read and starting observation.
            QueueSource();
        }
        catch (Exception e)
        {
            if (!_hidden && request == _sourceRequest && _file == displayed) SourceProblem(e.Message);
        }
    }
    void QueueSource()
    {
        if (_hidden || _source == null || _file == null) return;
        _sourcePending = true;
        if (!_probingSource && !_sourceTimer.IsEnabled) _sourceTimer.Start();
    }
    async Task ProbeSource()
    {
        if (_hidden || !_sourcePending || _source == null || _file == null || _probingSource) return;
        var source = _source; var displayed = _file;
        _sourcePending = false; _probingSource = true;
        try
        {
            var version = await Task.Run(() => { source.Reconnect(); return source.ReadVersion(); });
            if (_hidden || _source != source || _file != displayed) return;
            if (version != displayed.Version)
            {
                _sourceNotice.Title = version == "missing" ? "File removed" : "Code changed";
                _sourceNotice.Message = "You are reading the earlier version. Reload to review the current code; your position and drafts are kept.";
                _sourceNotice.Severity = InfoBarSeverity.Warning; _sourceNotice.IsOpen = true;
            }
            else if (source.WatchError != null) SourceProblem(source.WatchError);
            else _sourceNotice.IsOpen = false;
        }
        catch (Exception e)
        {
            if (!_hidden && _source == source && _file == displayed) SourceProblem(e.Message);
        }
        finally
        {
            _probingSource = false;
            if (_sourcePending) QueueSource();
        }
    }
    void SourceProblem(string message)
    {
        _sourceNotice.Title = "Current code unavailable";
        _sourceNotice.Message = "The displayed version and drafts are kept. Reload to retry. " + message;
        _sourceNotice.Severity = InfoBarSeverity.Warning; _sourceNotice.IsOpen = true;
    }
    void UpdateSourceAction()
    {
        _reloadCode.IsEnabled = !_writing && !_reading && _file != null;
        _reloadCode.Text = _reading ? "Loading…" : "Reload code";
    }
}
