using System.Diagnostics;
using Sg.Core;

namespace Sg.App;

/// <summary>ILog that forwards to whichever StatusStrip is running an operation. Progress is throttled to ten updates a second.</summary>
public sealed class UiLog : ILog
{
    readonly AsyncLocal<OperationTask?> _task = new();
    public OperationTask? Task { get => _task.Value; set => _task.Value = value; }
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

    public void Info(string message) { Task?.Append(message); Sink?.Append(message); }
    public void Warn(string message) => Info("warning: " + message);

    public void Cmd(string message)
    {
        if (Session.Settings.Verbose) Info("$ " + message);
    }

    public void Progress(string label, long done, long total, string unit, string? detail)
    {
        var target = _target.Value;
        if (target == null) return;
        var now = _clock.ElapsedMilliseconds;
        var final = total > 0 && done >= total;
        if (now - Interlocked.Read(ref target.LastMs) < 100 && !final) return;
        Interlocked.Exchange(ref target.LastMs, now);
        var counts = unit == "B" ? DiskUsage.Human(done) + (total > 0 ? " / " + DiskUsage.Human(total) : "") : $"{done}" + (total > 0 ? $"/{total}" : "") + " " + unit;
        Task?.Progress($"{label} · {counts} {detail}", total > 0 ? Math.Clamp(done * 100.0 / total, 0, 100) : null);
        Sink?.SetProgress(label, done, total, unit, detail);
    }

    public void ProgressEnd(string label, string? summary)
    {
        if (_target.Value is { } target) Interlocked.Exchange(ref target.LastMs, -1000);
        Task?.Progress(label + ": " + (summary ?? "done"));
        Task?.Append(label + ": " + (summary ?? "done"));
        Sink?.EndProgress(label + ": " + (summary ?? "done"));
    }
}
