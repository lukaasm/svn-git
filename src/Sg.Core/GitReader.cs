using System.Diagnostics;
using System.Text;

namespace Sg.Core;

/// <summary>
/// One git process that stays up for an operation and answers questions about objects on a pipe:
/// `git cat-file --batch-command`, asked `info &lt;rev&gt;` for what a name points at and
/// `contents &lt;rev&gt;` for an object's bytes. Resolving a ref was a git process each - a quarter of
/// all the processes a backup started - and each start costs 30 ms on Windows where an answer here
/// costs a fraction of one. git reads the refs and objects fresh for every question, so what another
/// command wrote a moment ago is seen.
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
    Stream? _in, _out;
    readonly byte[] _buffer = new byte[1 << 16];
    int _start, _end;
    readonly StringBuilder _stderr = new();
    bool _failed;

    public GitReader(string exe, string store, IReadOnlyDictionary<string, string> env, ILog log)
    {
        _exe = exe;
        _store = store;
        _env = env;
        _log = log;
    }

    /// <summary>What an object's header says: its id and its type.</summary>
    public readonly record struct Header(string Oid, string Type);

    /// <summary>
    /// What a revision names: its object id, or null when it names nothing. False when this reader
    /// could not ask, and the question has to go to a process of its own.
    /// </summary>
    public bool TryInfo(string rev, out string? oid)
    {
        oid = null;
        if (!Ask("info", rev, read: false, out var header, out _)) return false;
        oid = header?.Oid;
        return true;
    }

    /// <summary>An object's type and bytes, or null for both when the revision names nothing. False as for <see cref="TryInfo"/>.</summary>
    public bool TryContents(string rev, out Header? header, out byte[]? data) => Ask("contents", rev, read: true, out header, out data);

    bool Ask(string command, string rev, bool read, out Header? header, out byte[]? data)
    {
        header = null;
        data = null;
        if (rev.Length == 0 || rev.IndexOfAny(['\n', '\r']) >= 0) return false;
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!Started()) return false;
                string? line;
                try
                {
                    var ask = Utf8.GetBytes(command + " " + rev + "\n");
                    _in!.Write(ask);
                    _in.Flush();
                    line = ReadLine();
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
                // A lookup is a line in the log, as its process was; the trees a listing reads are its caller's one line.
                if (!read) _log.Cmd("[" + _store + "] git cat-file --batch-command < " + command + " " + rev);
                if (line.EndsWith(" missing", StringComparison.Ordinal)) return true;
                if (line.EndsWith(" ambiguous", StringComparison.Ordinal)) return false;
                var fields = line.Split(' ');
                if (fields.Length != 3 || !long.TryParse(fields[2], out var size)) { Stop(); return false; }
                header = new Header(fields[0], fields[1]);
                if (!read) return true;
                // The object's bytes, then the newline git puts after them.
                try
                {
                    data = new byte[size];
                    if (!ReadExact(data) || !ReadExact(new byte[1])) { Stop(); data = null; header = null; return false; }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or OutOfMemoryException)
                {
                    Stop(); data = null; header = null; return false;
                }
                return true;
            }
            _failed = true;
            return false;
        }
    }

    string? ReadLine()
    {
        var line = new List<byte>();
        while (true)
        {
            for (var i = _start; i < _end; i++)
            {
                if (_buffer[i] != (byte)'\n') continue;
                line.AddRange(new ArraySegment<byte>(_buffer, _start, i - _start));
                _start = i + 1;
                return Utf8.GetString(line.ToArray());
            }
            line.AddRange(new ArraySegment<byte>(_buffer, _start, _end - _start));
            if (!Fill()) return null;
        }
    }

    bool ReadExact(byte[] into)
    {
        var done = 0;
        while (done < into.Length)
        {
            if (_start == _end && !Fill()) return false;
            var n = Math.Min(into.Length - done, _end - _start);
            Array.Copy(_buffer, _start, into, done, n);
            _start += n;
            done += n;
        }
        return true;
    }

    bool Fill()
    {
        _start = 0;
        _end = _out!.Read(_buffer, 0, _buffer.Length);
        return _end > 0;
    }

    bool Started()
    {
        if (_process is { HasExited: false }) return true;
        Stop();
        if (_failed) return false;
        lock (Unsupported) if (Unsupported.Contains(_exe)) return false;
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _store,
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
            return false;
        }
        _process = p;
        _in = p.StandardInput.BaseStream;
        _out = p.StandardOutput.BaseStream;
        _start = _end = 0;
        return true;
    }

    void Stop()
    {
        var p = _process;
        _process = null;
        _in = _out = null;
        _start = _end = 0;
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
