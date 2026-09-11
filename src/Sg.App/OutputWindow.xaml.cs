using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Sg.App;

/// <summary>
/// The lines every operation wrote, on their own. One window for the whole app, like the monitor: the
/// log is not a property of the window that happened to start the work, and a second copy of it would
/// only ever hold half the story.
/// </summary>
public sealed partial class OutputWindow : Window
{
    static OutputWindow? _open;

    public static OutputWindow Show(string? status = null)
    {
        if (_open == null)
        {
            _open = new OutputWindow();
            _open.Closed += (_, _) => _open = null;
        }
        if (status != null) _open.StatusText.Text = status;
        WindowHelper.Show(_open);
        return _open;
    }

    /// <summary>True while a window is showing the log, so a strip can say "showing" instead of "show".</summary>
    public static bool IsOpen => _open != null;

    public OutputWindow()
    {
        InitializeComponent();
        WindowHelper.Chrome(this, AppTitleBar, 1100, 760, log: false);
        Shortcuts.CloseOnEscape(this);
        LogStore.Changed += OnChanged;
        Closed += (_, _) => LogStore.Changed -= OnChanged;
        Fill();
    }

    void OnChanged()
    {
        if (DispatcherQueue.HasThreadAccess) Fill();
        else DispatcherQueue.TryEnqueue(Fill);
    }

    void Fill()
    {
        var text = LogStore.Text;
        if (LogText.Text == text) return;
        LogText.Text = text;
        // Reading back through what already happened must not be undone by the next line arriving.
        if (FollowToggle.IsChecked == true) LogText.SelectionStart = text.Length;
    }

    /// <summary>What the operation that is running says about itself, mirrored from the strip that owns it.</summary>
    public static void SetStatus(string text)
    {
        var w = _open;
        if (w == null) return;
        if (w.DispatcherQueue.HasThreadAccess) w.StatusText.Text = text;
        else w.DispatcherQueue.TryEnqueue(() => w.StatusText.Text = text);
    }

    void Clear_Click(object sender, RoutedEventArgs e) => LogStore.Clear();
}
