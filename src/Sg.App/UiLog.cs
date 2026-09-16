using System.Diagnostics;
using Sg.Core;

namespace Sg.App;

/// <summary>ILog that forwards to whichever StatusStrip is running an operation. Progress is throttled to ten updates a second.</summary>
public sealed class UiLog : ILog
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    sealed class Target(StatusStrip pane)
    {
        public StatusStrip Pane { get; } = pane;
        public long LastMs = -1000;
    }
    readonly AsyncLocal<Target?> _target = new();
    public StatusStrip? Sink
    {
        get => _target.Value?.Pane;
        set => _target.Value = value == null ? null : new Target(value);
    }

    public void Info(string message) => Sink?.Append(message);
    public void Warn(string message) => Sink?.Append("warning: " + message);

    public void Cmd(string message)
    {
        if (Session.Settings.Verbose) Sink?.Append("$ " + message);
    }

    public void Progress(string label, long done, long total, string unit, string? detail)
    {
        var target = _target.Value;
        if (target == null) return;
        var now = _clock.ElapsedMilliseconds;
        var final = total > 0 && done >= total;
        if (now - Interlocked.Read(ref target.LastMs) < 100 && !final) return;
        Interlocked.Exchange(ref target.LastMs, now);
        Sink?.SetProgress(label, done, total, unit, detail);
    }

    public void ProgressEnd(string label, string? summary)
    {
        if (_target.Value is { } target) Interlocked.Exchange(ref target.LastMs, -1000);
        Sink?.EndProgress(label + ": " + (summary ?? "done"));
    }
}
