using System.Diagnostics;
using Sg.Core;

namespace Sg.App;

/// <summary>ILog that forwards to whichever StatusStrip is running an operation. Progress is throttled to ten updates a second.</summary>
public sealed class UiLog : ILog
{
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastMs = -1000;
    public volatile StatusStrip? Sink;

    public void Info(string message) => Sink?.Append(message);
    public void Warn(string message) => Sink?.Append("warning: " + message);

    public void Cmd(string message)
    {
        if (Session.Settings.Verbose) Sink?.Append("$ " + message);
    }

    public void Progress(string label, long done, long total, string unit, string? detail)
    {
        var now = _clock.ElapsedMilliseconds;
        var final = total > 0 && done >= total;
        if (now - _lastMs < 100 && !final) return;
        _lastMs = now;
        Sink?.SetProgress(label, done, total, unit, detail);
    }

    public void ProgressEnd(string label, string? summary)
    {
        _lastMs = -1000;
        Sink?.EndProgress(label + ": " + (summary ?? "done"));
    }
}
