using Sg.Core;

namespace Sg.App;

/// <summary>Owns disposable reads for one page. Replacement and navigation cancel child processes too.</summary>
internal sealed class PageReads(Action<bool>? reading = null)
{
    Request? _current;
    public Request Begin()
    {
        Cancel();
        reading?.Invoke(true);
        return _current = new(this);
    }
    public void Cancel()
    {
        var previous = _current;
        _current = null;
        previous?.Cancel();
        if (previous != null) reading?.Invoke(false);
    }
    internal sealed class Request(PageReads owner) : IDisposable
    {
        readonly CancellationTokenSource _cancel = new();
        Task? _cancellation;
        public bool Current => ReferenceEquals(owner._current, this) && !_cancel.IsCancellationRequested;
        // Child-process termination can take seconds. Invalidate synchronously, but never execute
        // cancellation callbacks on the UI thread when the user edits or leaves a page.
        internal void Cancel() => _cancellation ??= _cancel.CancelAsync();
        public async Task<T?> Run<T>(StatusStrip pane, Func<T> work, Action<string>? failed = null) where T : class
        {
            using var feedback = pane.Reading();
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
            catch (Exception ex)
            {
                if (failed == null) Runner.ReadError(pane, ex);
                else
                {
                    // A page with inline recovery owns the feedback; keep diagnostics without opening another window.
                    pane.Append("error: " + ex);
                    failed(ex.Message);
                }
                return null;
            }
        }
        public void Dispose()
        {
            if (ReferenceEquals(owner._current, this))
            {
                owner._current = null;
                owner.EndRead();
            }
            _ = ReleaseCancellation();
        }
        async Task ReleaseCancellation()
        {
            try { if (_cancellation != null) await _cancellation.ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Read cancellation cleanup failed: " + ex); }
            finally { _cancel.Dispose(); }
        }
    }
    void EndRead() => reading?.Invoke(false);
}
