using Sg.Core;

namespace Sg.App;

/// <summary>Owns disposable reads for one page. Replacement and navigation cancel child processes too.</summary>
internal sealed class PageReads
{
    Request? _current;
    public Request Begin()
    {
        Cancel();
        return _current = new(this);
    }
    public void Cancel()
    {
        var previous = _current;
        _current = null;
        previous?.Cancel();
    }
    internal sealed class Request(PageReads owner) : IDisposable
    {
        readonly CancellationTokenSource _cancel = new();
        public bool Current => ReferenceEquals(owner._current, this) && !_cancel.IsCancellationRequested;
        internal void Cancel() => _cancel.Cancel();
        public async Task<T?> Run<T>(StatusStrip pane, Func<T> work) where T : class
        {
            try
            {
                using (Cancellation.Use(_cancel.Token))
                {
                    var result = await Task.Run(work, _cancel.Token);
                    return Current ? result : null;
                }
            }
            catch (Exception) when (!Current) { return null; }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex) { Runner.ReadError(pane, ex); return null; }
        }
        public void Dispose()
        {
            if (ReferenceEquals(owner._current, this)) owner._current = null;
            _cancel.Dispose();
        }
    }
}
