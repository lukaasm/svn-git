using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>
/// A button that started something shows it: its icon becomes a spinning ring and it stays off until the
/// work is done. The log pane says what runs; this says where it was started from.
/// </summary>
public static class Busy
{
    /// <summary>
    /// Runs the work with the button marked busy. sender may be anything; only a Control changes.
    /// restoreEnabled false leaves the enabled state to the window's own logic, for buttons whose state
    /// a Sync method computes after the work.
    /// </summary>
    public static Task During(object? sender, Func<Task> work, bool restoreEnabled = true) =>
        Run(sender as Control, work, restoreEnabled);

    public static async Task<T> During<T>(object? sender, Func<Task<T>> work, bool restoreEnabled = true)
    {
        T result = default!;
        await Run(sender as Control, async () => { result = await work(); }, restoreEnabled);
        return result;
    }

    /// <summary>
    /// How long work has to run before the ring is worth drawing, and how long it stays once it is
    /// drawn. Under the first, a sync that answers from cache used to blink the icon out and back;
    /// over it, the second stops the ring appearing and vanishing inside one frame.
    /// </summary>
    const int ShowAfterMs = 180, KeepForMs = 350;

    static async Task Run(Control? control, Func<Task> work, bool restoreEnabled)
    {
        if (control == null) { await work(); return; }
        var wasEnabled = control.IsEnabled;
        control.IsEnabled = false;
        // ContentControl, not Button: the primary action of the two commit pages is a SplitButton now, and
        // both kinds hold their icon and their word in the same panel.
        var panel = (control as ContentControl)?.Content as Panel;
        var icon = panel?.Children.OfType<FontIcon>().FirstOrDefault();
        ProgressRing? ring = null;
        var shownAt = 0L;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        void ShowRing()
        {
            if (ring != null || panel == null || icon == null) return;
            ring = new ProgressRing
            {
                IsActive = true,
                Width = 16, Height = 16, MinWidth = 16, MinHeight = 16,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Insert(panel.Children.IndexOf(icon), ring);
            icon.Visibility = Visibility.Collapsed;
            shownAt = clock.ElapsedMilliseconds;
        }

        try
        {
            // The work starts first, and the ring only joins it if it is still running a moment later.
            var running = work();
            var late = await Task.WhenAny(running, Task.Delay(ShowAfterMs));
            if (!ReferenceEquals(late, running)) ShowRing();
            await running;
        }
        finally
        {
            if (ring != null)
            {
                var seen = clock.ElapsedMilliseconds - shownAt;
                if (seen < KeepForMs) await Task.Delay((int)(KeepForMs - seen));
                panel?.Children.Remove(ring);
            }
            if (icon != null) icon.Visibility = Visibility.Visible;
            if (restoreEnabled) control.IsEnabled = wasEnabled;
        }
    }
}
