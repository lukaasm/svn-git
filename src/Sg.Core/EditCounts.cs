namespace Sg.Core;

/// <summary>
/// Each checkout's local edit count, counted again only after something under the checkout changed. svn
/// status reads every file of a checkout - most of a second on one this size - and every refresh of the
/// overview asked it again for each checkout, when mostly nothing had changed. Before a kept count is
/// handed back, a file is written into the checkout's .svn or .git folder and the watcher waits for its
/// own report of it: whatever changed before has been reported by then, so a count asked for right after
/// an edit is never the one from before it. A watcher that lost track counts again and starts over; a
/// checkout with no .svn or .git folder to write into, or on a network drive, is counted every time.
/// </summary>
public sealed class EditCounts : IDisposable
{
    readonly object _gate = new();
    readonly Dictionary<string, Watched?> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public int Count(string checkout, Func<int> count)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkout));
        Watched? w;
        lock (_gate)
        {
            if (!_byPath.TryGetValue(path, out w) || w is { Broken: true })
            {
                w?.Dispose();
                _byPath[path] = w = Watched.Start(path);
            }
        }
        return w == null ? count() : w.Count(count);
    }

    /// <summary>Counts every checkout again on its next look: F5 reads everything again.</summary>
    public void Forget()
    {
        lock (_gate) foreach (var w in _byPath.Values) w?.Forget();
    }

    /// <summary>Stops watching every checkout not named: the root closed, or a checkout left it.</summary>
    public void Keep(IEnumerable<string> checkouts)
    {
        var keep = checkouts.Select(c => Path.TrimEndingDirectorySeparator(Path.GetFullPath(c))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var path in _byPath.Keys.Where(p => !keep.Contains(p)).ToList())
            {
                _byPath[path]?.Dispose();
                _byPath.Remove(path);
            }
    }

    public void Dispose() => Keep([]);

    sealed class Watched : IDisposable
    {
        const string SettlePrefix = "sg-settle-";
        readonly FileSystemWatcher _watcher;
        readonly string _settleIn;
        readonly object _gate = new();
        readonly ManualResetEventSlim _arrived = new();
        volatile bool _changed = true;
        volatile string? _awaited;
        int? _count;

        public volatile bool Broken;

        Watched(FileSystemWatcher watcher, string settleIn)
        {
            _watcher = watcher;
            _settleIn = settleIn;
        }

        public static Watched? Start(string path)
        {
            try
            {
                if (path.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) return null;
                // svn keeps its own temporary files in .svn\tmp, and status never lists what is inside .svn or .git.
                var svnTmp = Path.Combine(path, ".svn", "tmp");
                var settleIn = Directory.Exists(svnTmp) ? svnTmp : Directory.Exists(Path.Combine(path, ".git")) ? Path.Combine(path, ".git") : null;
                if (settleIn == null) return null;
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                                   | NotifyFilters.Attributes | NotifyFilters.CreationTime,
                };
                var w = new Watched(watcher, settleIn);
                watcher.Changed += (_, e) => w.Saw(e.FullPath);
                watcher.Created += (_, e) => w.Saw(e.FullPath);
                watcher.Deleted += (_, e) => w.Saw(e.FullPath);
                watcher.Renamed += (_, e) => { w.Saw(e.OldFullPath); w.Saw(e.FullPath); };
                watcher.Error += (_, _) => { w._changed = true; w.Broken = true; };
                watcher.EnableRaisingEvents = true;
                return w;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        void Saw(string path)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(SettlePrefix, StringComparison.Ordinal))
            {
                if (string.Equals(path, _awaited, StringComparison.OrdinalIgnoreCase)) _arrived.Set();
                return;
            }
            // The settle folder's own time moves with every settle file; what else lands in it has events of its own.
            if (string.Equals(path, _settleIn, StringComparison.OrdinalIgnoreCase)) return;
            // git's fsmonitor daemon writes a file here for every status it answers.
            if (path.Contains(@"\.git\fsmonitor--daemon", StringComparison.OrdinalIgnoreCase)) return;
            _changed = true;
        }

        public int Count(Func<int> count)
        {
            lock (_gate)
            {
                if (_count is { } known && Settled() && !_changed && !Broken) return known;
                // Cleared before counting: what changes while the count runs is seen on the next look.
                _changed = false;
                try { return (_count = count()).Value; }
                catch
                {
                    _changed = true;
                    _count = null;
                    throw;
                }
            }
        }

        public void Forget() => _changed = true;

        /// <summary>Writes a file in and waits for the watcher to report it: every change before it has been reported then.</summary>
        bool Settled()
        {
            var file = Path.Combine(_settleIn, SettlePrefix + Guid.NewGuid().ToString("N"));
            _arrived.Reset();
            _awaited = file;
            try
            {
                File.WriteAllBytes(file, []);
                return _arrived.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                _awaited = null;
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* svn cleanup empties .svn\tmp */ }
            }
        }

        /// <summary>
        /// The watcher only: a count running on another thread may still be waiting on its settle file, and
        /// times out rather than finding its event gone.
        /// </summary>
        public void Dispose()
        {
            Broken = true;
            _watcher.Dispose();
        }
    }
}
