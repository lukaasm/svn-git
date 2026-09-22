using Microsoft.UI.Dispatching;

namespace Sg.App;

/// <summary>Coalesces background notifications; the UI reads current state once when it catches up.</summary>
internal sealed class UiRefresh(DispatcherQueue dispatcher, Action refresh)
{
    int _queued;

    public void Request()
    {
        if (dispatcher.HasThreadAccess) { refresh(); return; }
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (!dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            refresh();
        })) Interlocked.Exchange(ref _queued, 0);
    }
}
