using System.Diagnostics;
using System.Text;

namespace Sg.Core;

/// <summary>Where progress and command lines go. The CLI prints to stderr, the GUI to a log pane.</summary>
public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Cmd(string message);

    /// <summary>A long step moved on. total &lt;= 0 means unknown. unit is "B" for bytes, else a plain word like "files".</summary>
    void Progress(string label, long done, long total, string unit, string? detail) { }

    /// <summary>The long step is over. Prints one final line.</summary>
    void ProgressEnd(string label, string? summary) { }
}

public sealed class NullLog : ILog
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Cmd(string message) { }
}

/// <summary>Collects the lines. Status reads several worktrees side by side, so every method locks.</summary>
public sealed class CollectingLog : ILog
{
    readonly object _gate = new();
    readonly List<string> _lines = new();

    public List<string> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    public void Clear() { lock (_gate) _lines.Clear(); }

    void Add(string line) { lock (_gate) _lines.Add(line); }
    public void Info(string message) => Add("info: " + message);
    public void Warn(string message) => Add("warn: " + message);
    public void Cmd(string message) => Add("cmd: " + message);
    public override string ToString() => string.Join("\n", Lines);
}

/// <summary>An error with a message meant for the user. No stack trace needed.</summary>
public sealed class SgException : Exception
{
    public SgException(string message) : base(message) { }
}

public sealed class ProcResult
{
    public required string Exe { get; init; }
    public required IReadOnlyList<string> Args { get; init; }
    public string? Cwd { get; init; }
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool Ok => ExitCode == 0;

    public string CommandLine => Exe + " " + string.Join(" ", Args.Select(a => a.Contains(' ') || a.Length == 0 ? "\"" + a + "\"" : a));

    public ProcResult EnsureOk()
    {
        if (Ok) return this;
        var text = (StdErr.Trim() + "\n" + StdOut.Trim()).Trim();
        throw new SgException($"command failed (exit {ExitCode}): {CommandLine}\n{text}");
    }
}

public static class Proc
{
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Runs a process to completion. Captures stdout and stderr as UTF-8 text, or streams stdout to a file.</summary>
    public static ProcResult Run(string exe, IReadOnlyList<string> args, string? cwd, ILog log,
        byte[]? stdin = null, IReadOnlyDictionary<string, string>? env = null, string? stdoutToFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        log.Cmd((cwd != null ? "[" + cwd + "] " : "") + exe + " " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));

        var cancel = Cancellation.Current;
        cancel.ThrowIfCancellationRequested();

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { throw new SgException($"cannot start {exe}: {ex.Message}"); }

        // Cancelling ends the child. git and svn both leave the working copy usable when killed:
        // a snapshot writes nothing until update-ref, and svn recovers with cleanup.
        using var kill = cancel.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception) { /* already gone */ }
        });

        var errTask = Task.Run(() => p.StandardError.ReadToEnd());

        // A run with stdin has to write it before it drains stdout, or a child that fills its output
        // pipe deadlocks. Everything else reads stdout on this thread: sg spends a hundred short git
        // calls on one refresh, and a pooled task each was thread pool traffic for nothing.
        Task<string>? outTask = null;
        var stdout = "";
        if (stdin != null)
        {
            outTask = stdoutToFile != null
                ? Task.Run(() => { Drain(p, stdoutToFile); return ""; })
                : Task.Run(() => p.StandardOutput.ReadToEnd());
            try
            {
                p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
                p.StandardInput.BaseStream.Flush();
            }
            catch (IOException) { /* process may exit early */ }
            p.StandardInput.Close();
        }
        else
        {
            try
            {
                if (stdoutToFile != null) Drain(p, stdoutToFile);
                else stdout = p.StandardOutput.ReadToEnd();
            }
            catch (IOException) { /* the child was killed while the pipe was open */ }
        }

        p.WaitForExit();
        if (cancel.IsCancellationRequested)
        {
            // The child is dead; let the readers finish so nothing is left running, then take back
            // the half written file. A truncated prefix would otherwise read as the real content.
            try
            {
                Task[] pending = outTask != null ? [outTask, errTask] : [errTask];
                Task.WaitAll(pending, TimeSpan.FromSeconds(5));
            }
            catch (Exception) { /* the reads died with the child */ }
            if (stdoutToFile != null)
            {
                try { File.Delete(stdoutToFile); } catch (IOException) { }
            }
            throw new SgCancelledException();
        }
        return new ProcResult
        {
            Exe = exe, Args = args, Cwd = cwd, ExitCode = p.ExitCode,
            StdOut = outTask != null ? outTask.Result : stdout, StdErr = errTask.Result,
        };
    }

    static void Drain(Process p, string toFile)
    {
        using var f = File.Create(toFile);
        p.StandardOutput.BaseStream.CopyTo(f);
    }

    /// <summary>
    /// Like Run, but hands every output line to a callback as it arrives. Lines end at \n or \r, so git progress
    /// updates arrive one by one. Callbacks run on reader threads. stdout is kept only when keepStdout is set.
    /// </summary>
    public static ProcResult RunStreaming(string exe, IReadOnlyList<string> args, string? cwd, ILog log,
        Action<string>? onStdoutLine, Action<string>? onStderrLine, IReadOnlyDictionary<string, string>? env = null, bool keepStdout = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        log.Cmd((cwd != null ? "[" + cwd + "] " : "") + exe + " " + string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));

        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { throw new SgException($"cannot start {exe}: {ex.Message}"); }

        var outSb = keepStdout ? new StringBuilder() : null;
        var errSb = new StringBuilder();
        var outTask = Task.Run(() => Pump(p.StandardOutput, onStdoutLine, outSb));
        var errTask = Task.Run(() => Pump(p.StandardError, onStderrLine, errSb));
        p.WaitForExit();
        Task.WaitAll(outTask, errTask);
        return new ProcResult
        {
            Exe = exe, Args = args, Cwd = cwd, ExitCode = p.ExitCode,
            StdOut = outSb?.ToString() ?? "", StdErr = errSb.ToString(),
        };
    }

    static void Pump(StreamReader reader, Action<string>? onLine, StringBuilder? keep)
    {
        var buf = new char[8192];
        var line = new StringBuilder();
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
        {
            keep?.Append(buf, 0, n);
            if (onLine == null) continue;
            for (var i = 0; i < n; i++)
            {
                var c = buf[i];
                if (c is '\n' or '\r')
                {
                    if (line.Length > 0) { onLine(line.ToString()); line.Clear(); }
                }
                else line.Append(c);
            }
        }
        if (line.Length > 0) onLine?.Invoke(line.ToString());
    }
}
