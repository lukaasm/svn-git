namespace Sg.Core;

public sealed record ReviewSnapshot(CodeReviewData Data, string Revision);

/// <summary>Follow one review document, including atomic replacement. Notifications are hints; Read is authoritative.
/// No repository scans or polling occur here. Callbacks run on watcher threads; dispose when the view leaves.</summary>
public sealed class ReviewFeed : IDisposable
{
    readonly string _file;
    readonly object _gate = new();
    FileSystemWatcher? _watcher;
    volatile bool _disposed;
    public string Identity => Path.GetFileNameWithoutExtension(_file);
    public string? WatchError { get; private set; }
    public event Action? Changed;

    internal ReviewFeed(string file) { _file = file; Reconnect(); }

    /// <summary>Retry an unavailable watcher on activation or explicit refresh. Reads remain usable without it.</summary>
    public void Reconnect()
    {
        lock (_gate) ReconnectCore();
    }
    void ReconnectCore()
    {
        if (_disposed || _watcher != null && WatchError == null) return;
        _watcher?.Dispose(); _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(_file)!, Path.GetFileName(_file))
            { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher = watcher;
            watcher.Changed += Notify; watcher.Created += Notify; watcher.Deleted += Notify; watcher.Renamed += Notify;
            watcher.Error += (_, e) => { if (!_disposed && ReferenceEquals(_watcher, watcher)) { WatchError = e.GetException().Message; Signal(); } };
            watcher.EnableRaisingEvents = true; WatchError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { _watcher?.Dispose(); _watcher = null; WatchError = e.Message; }
    }
    void Notify(object sender, FileSystemEventArgs args) => Signal();
    void Signal() { if (!_disposed) Changed?.Invoke(); }

    public ReviewSnapshot Read()
    {
        string text;
        try
        {
            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > CodeReview.MaxDataBytes) throw new SgException("Review data exceeds the 32 MB limit.");
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return new(new(), "missing"); }
        // Decode before publication: corrupt or incomplete external writes must not replace the last good UI.
        return new(CodeReview.Decode(text), WorkspaceVersion.Hash(text));
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _watcher?.Dispose(); _watcher = null; Changed = null; }
    }
}
