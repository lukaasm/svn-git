using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>
/// The foot of a window that runs operations: what is happening, how far along, and how to stop it.
/// The lines go to the app's one log window, the Log button in the title bar away. Every method may
/// be called from any thread.
/// </summary>
public sealed partial class StatusStrip : UserControl
{
    public StatusStrip()
    {
        InitializeComponent();
    }

    void Ui(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    /// <summary>An operation is running: the bar stays up between the progress reports it makes.</summary>
    bool _running;

    void Say(string text) => Ui(() =>
    {
        StatusText.Text = text;
        OutputWindow.SetStatus(text);
    });

    /// <summary>
    /// One line for the log. Lines are held by the app, not by this control, so a window that closes
    /// mid-operation does not take the record of it away.
    /// </summary>
    public void Append(string line) => LogStore.Append(line);

    public void SetProgress(string label, long done, long total, string unit, string? detail) => Ui(() =>
    {
        Bar.Visibility = Visibility.Visible;
        if (total > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Maximum = 100;
            Bar.Value = Math.Clamp(done * 100.0 / total, 0, 100);
        }
        else Bar.IsIndeterminate = true;
        var nums = unit == "B"
            ? (total > 0 ? $"{Human(done)} / {Human(total)}" : Human(done))
            : (total > 0 ? $"{done}/{total} {unit}" : $"{done} {unit}");
        Say($"{label}  {nums}" + (detail != null ? "  " + detail : ""));
    });

    /// <summary>One counted step finished. The operation may go on, so the bar runs rather than going away.</summary>
    public void EndProgress(string text) => Ui(() =>
    {
        Bar.IsIndeterminate = _running;
        Bar.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        Say(text);
        Append(text);
    });

    CancellationTokenSource? _cancel;

    /// <summary>Runner hands the strip the token source, so Cancel has something to press.</summary>
    public void Arm(CancellationTokenSource? source) => Ui(() =>
    {
        _cancel = source;
        CancelButton.Visibility = source != null ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = source != null;
    });

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "cancelling...";
        try { _cancel?.Cancel(); }
        catch (ObjectDisposedException) { /* it already finished */ }
    }

    /// <summary>An operation starts: the bar runs from the first moment, not once something reports progress.</summary>
    public void Begin(string title) => Ui(() =>
    {
        _running = true;
        Bar.IsIndeterminate = true;
        Bar.Visibility = Visibility.Visible;
        Say(title + "...");
        Append("== " + title);
    });

    public void End(string? text = null) => Ui(() =>
    {
        _running = false;
        Bar.Visibility = Visibility.Collapsed;
        Bar.IsIndeterminate = false;
        if (text != null) Say(text);
    });

    public void Error(string message) => Ui(() =>
    {
        _running = false;
        Bar.Visibility = Visibility.Collapsed;
        Bar.IsIndeterminate = false;
        var first = message.Split('\n')[0];
        Say("error: " + first);
        Append("error: " + message);
        // The detail is the whole point of a failure, and it is one window away rather than on screen.
        // Opening it is the one case that earns the interruption.
        OutputWindow.Show("error: " + first);
    });

    public void Clear() => LogStore.Clear();

    static string Human(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB"
        : b >= 1 << 20 ? $"{b / (double)(1 << 20):0} MB"
        : $"{b / 1024.0:0} KB";
}
