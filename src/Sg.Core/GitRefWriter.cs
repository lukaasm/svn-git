using System.Diagnostics;
using System.Text;

namespace Sg.Core;

/// <summary>
/// One `git update-ref --stdin` that stays up for an operation and takes each ref write as a
/// transaction of its own - start, update or delete, commit - which is what `git update-ref` does in a
/// process of its own. A backup or a push moves dozens of refs, each a 30 ms process on Windows. git
/// takes and lets go of the ref's lock inside each transaction, so nothing is held between them.
/// When a transaction fails - a lock held elsewhere, a ref git refuses - the process ends, and the
/// caller runs the same write as a process of its own, which fails, or succeeds, in git's own words.
/// </summary>
sealed class GitRefWriter : IDisposable
{
    static readonly UTF8Encoding Utf8 = new(false);

    readonly string _exe;
    readonly string _store;
    readonly IReadOnlyDictionary<string, string> _env;
    readonly ILog _log;
    readonly object _gate = new();
    Process? _process;
    bool _failed;
    int _refused;

    public GitRefWriter(string exe, string store, IReadOnlyDictionary<string, string> env, ILog log)
    {
        _exe = exe;
        _store = store;
        _env = env;
        _log = log;
    }

    /// <summary>Sets the ref to the sha. False when it could not be done here.</summary>
    public bool TryUpdate(string refName, string sha) => Transact("update " + refName + " " + sha, refName, sha);

    /// <summary>Deletes the ref. False when it could not be done here.</summary>
    public bool TryDelete(string refName) => Transact("delete " + refName, refName);

    bool Transact(string command, params string[] words)
    {
        if (words.Any(w => w.Length == 0 || w.IndexOfAny([' ', '\n', '\r', '\0']) >= 0)) return false;
        lock (_gate)
        {
            if (_failed) return false;
            var p = Started();
            if (p == null) return false;
            try
            {
                p.StandardInput.Write("start\n" + command + "\ncommit\n");
                p.StandardInput.Flush();
                if (p.StandardOutput.ReadLine() == "start: ok" && p.StandardOutput.ReadLine() == "commit: ok")
                {
                    _refused = 0;
                    _log.Cmd("[" + _store + "] git update-ref --stdin < " + command);
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { }
            // A transaction git refused ends the process; the caller asks git on its own. Two in a row
            // is a git that does not know transactions (older than 2.27), which is not asked again.
            Stop();
            if (++_refused >= 2) _failed = true;
            return false;
        }
    }

    Process? Started()
    {
        if (_process is { HasExited: false }) return _process;
        Stop();
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _store,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var a in (string[])["-C", _store, "update-ref", "--stdin"]) psi.ArgumentList.Add(a);
        foreach (var kv in _env) psi.Environment[kv.Key] = kv.Value;
        _log.Cmd("[" + _store + "] " + _exe + " -C " + _store + " update-ref --stdin");
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, _) => { };
        try
        {
            p.Start();
            p.BeginErrorReadLine();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            p.Dispose();
            _failed = true;
            return null;
        }
        return _process = p;
    }

    void Stop()
    {
        var p = _process;
        _process = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited)
            {
                try { p.StandardInput.Close(); } catch (IOException) { }
                if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { p.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate) Stop();
    }
}
