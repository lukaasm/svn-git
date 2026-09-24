namespace Sg.Core;

/// <summary>File notifications are hints. Readers must recheck content and reconnect after an error.
/// An optional boundary also observes ancestor replacement without subscribing to an entire tree.</summary>
internal sealed class FileObservation : IDisposable
{
    readonly string _file;
    readonly string? _boundary;
    readonly object _gate = new();
    readonly List<FileSystemWatcher> _watchers = [];
    volatile bool _disposed, _reconnect;
    public string? Error { get; private set; }
    public event Action? Changed;

    public FileObservation(string file, string? boundary = null)
    { _file = Path.GetFullPath(file); _boundary = boundary == null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary)); Reconnect(); }

    public void Reconnect()
    {
        lock (_gate)
        {
            if (_disposed || _watchers.Count > 0 && Error == null && !_reconnect) return;
            Clear(); _reconnect = false;
            try
            {
                var target = _file;
                while (Path.GetDirectoryName(target) is { } directory)
                {
                    if (Directory.Exists(directory))
                    {
                        var ancestor = target != _file;
                        var watcher = new FileSystemWatcher(directory, Path.GetFileName(target))
                        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                        _watchers.Add(watcher);
                        void Notify(object sender, FileSystemEventArgs args)
                        {
                            if (ancestor && args.ChangeType == WatcherChangeTypes.Changed) return;
                            if (ancestor) _reconnect = true;
                            Signal();
                        }
                        watcher.Changed += Notify; watcher.Created += Notify; watcher.Deleted += Notify; watcher.Renamed += Notify;
                        watcher.Error += (_, e) =>
                        {
                            lock (_gate)
                            {
                                if (_disposed || !_watchers.Contains(watcher)) return;
                                Error = e.GetException().Message;
                            }
                            Signal();
                        };
                        watcher.EnableRaisingEvents = true;
                    }
                    if (_boundary == null || string.Equals(target, _boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
                    target = directory;
                }
                Error = _watchers.Count == 0 ? "The file's folder is unavailable." : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { Clear(); Error = e.Message; }
        }
    }
    void Signal() { if (!_disposed) Changed?.Invoke(); }
    void Clear() { foreach (var watcher in _watchers) watcher.Dispose(); _watchers.Clear(); }
    public void Dispose() { lock (_gate) { _disposed = true; Clear(); Changed = null; } }
}
