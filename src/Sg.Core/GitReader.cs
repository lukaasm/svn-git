using System.Diagnostics;
using System.Text;

namespace Sg.Core;

/// <summary>
/// One git process that stays up for an operation and answers "what does this name point at" on a
/// pipe: `git cat-file --batch-command`, asked `info &lt;rev&gt;` once per question. Resolving a ref
/// was a git process each - a quarter of all the processes a backup started - and each start costs
/// 30 ms on Windows where an answer here costs a fraction of one. git reads the refs and objects
/// fresh for every question, so what another command wrote a moment ago is seen.
/// When it cannot answer - a git older than 2.36, a process that died, a name git calls ambiguous -
/// the caller asks with a process of its own, as before.
/// </summary>
sealed class GitReader : IDisposable
{
    static readonly UTF8Encoding Utf8 = new(false);
    /// <summary>The git.exe files that refused --batch-command: they are not asked again.</summary>
    static readonly HashSet<string> Unsupported = new(StringComparer.OrdinalIgnoreCase);

    readonly string _exe;
    readonly string _store;
    readonly IReadOnlyDictionary<string, string> _env;
    readonly ILog _log;
    readonly object _gate = new();
    Process? _process;
    readonly StringBuilder _stderr = new();
    bool _failed;

    public GitReader(string exe, string store, IReadOnlyDictionary<string, string> env, ILog log)
    {
        _exe = exe;
        _store = store;
        _env = env;
        _log = log;
    }

    /// <summary>
    /// What a revision names: its object id, or null when it names nothing. False when this reader
    /// could not ask, and the question has to go to a process of its own.
    /// </summary>
    public bool TryInfo(string rev, out string? oid)
    {
        oid = null;
        if (rev.Length == 0 || rev.IndexOfAny(['\n', '\r']) >= 0) return false;
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var p = Started();
                if (p == null) return false;
                string? line;
                try
                {
                    p.StandardInput.Write("info " + rev + "\n");
                    p.StandardInput.Flush();
                    line = p.StandardOutput.ReadLine();
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    line = null;
                }
                if (line == null)
                {
                    // The process is gone. A git that does not know --batch-command says so on its way
                    // out and is not asked again; any other loss is started again once.
                    string said;
                    lock (_stderr) said = _stderr.ToString();
                    Stop();
                    if (said.Contains("batch-command", StringComparison.Ordinal) || said.Contains("usage:", StringComparison.Ordinal))
                    {
                        lock (Unsupported) Unsupported.Add(_exe);
                        break;
                    }
                    continue;
                }
                _log.Cmd("[" + _store + "] git cat-file --batch-command < info " + rev);
                if (line.EndsWith(" missing", StringComparison.Ordinal)) return true;
                if (line.EndsWith(" ambiguous", StringComparison.Ordinal)) return false;
                var space = line.IndexOf(' ');
                if (space <= 0) { Stop(); return false; }
                oid = line[..space];
                return true;
            }
            _failed = true;
            return false;
        }
    }

    Process? Started()
    {
        if (_process is { HasExited: false }) return _process;
        _process?.Dispose();
        _process = null;
        if (_failed) return null;
        lock (Unsupported) if (Unsupported.Contains(_exe)) return null;
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
        foreach (var a in (string[])["-C", _store, "cat-file", "--batch-command"]) psi.ArgumentList.Add(a);
        foreach (var kv in _env) psi.Environment[kv.Key] = kv.Value;
        _log.Cmd("[" + _store + "] " + _exe + " -C " + _store + " cat-file --batch-command");
        var p = new Process { StartInfo = psi };
        lock (_stderr) _stderr.Clear();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
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
                // Its input closed, git finishes the last answer and exits; one that does not is ended.
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
