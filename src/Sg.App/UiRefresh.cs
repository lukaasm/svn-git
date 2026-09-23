using Microsoft.UI.Dispatching;

namespace Sg.App;

/// <summary>Coalesces background notifications; the UI reads current state once when it catches up.</summary>
internal sealed class UiRefresh(DispatcherQueue dispatcher, Action refresh, TimeSpan? interval = null)
{
    int _queued;
    DispatcherQueueTimer? _timer;

    public void Request()
    {
        if (interval == null && dispatcher.HasThreadAccess) { refresh(); return; }
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!dispatcher.TryEnqueue(interval == null ? DispatcherQueuePriority.Normal : DispatcherQueuePriority.Low, () =>
        {
            if (interval is { } delay)
            {
                // Progress is a stream: sample the latest state instead of rendering every log line.
                if (_timer == null)
                {
                    _timer = dispatcher.CreateTimer();
                    _timer.Interval = delay;
                    _timer.IsRepeating = false;
                    _timer.Tick += (_, _) => { Interlocked.Exchange(ref _queued, 0); refresh(); };
                }
                _timer.Start();
                return;
            }
            Interlocked.Exchange(ref _queued, 0);
            refresh();
        })) Interlocked.Exchange(ref _queued, 0);
    }
}
