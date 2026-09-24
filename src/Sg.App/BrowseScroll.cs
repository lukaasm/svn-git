using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>Restore a browsing offset after newly loaded rows have been measured.</summary>
internal static class BrowseScroll
{
    public sealed record Anchor(string? Key, double Top, double Offset);
    public static Anchor Capture(ScrollViewer scroll, IReadOnlyDictionary<string, FrameworkElement> rows)
    {
        foreach (var (key, row) in rows)
        {
            var top = row.TransformToVisual(scroll).TransformPoint(new()).Y;
            if (top + row.ActualHeight > 0 && top < scroll.ViewportHeight) return new(key, top, scroll.VerticalOffset);
        }
        return new(null, 0, scroll.VerticalOffset);
    }
    public static void Restore(ScrollViewer scroll, Anchor anchor, IReadOnlyDictionary<string, FrameworkElement> rows, Func<bool> current) =>
        scroll.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!scroll.IsLoaded || !current()) return;
            scroll.UpdateLayout();
            var offset = anchor.Key != null && rows.TryGetValue(anchor.Key, out var row)
                ? scroll.VerticalOffset + row.TransformToVisual(scroll).TransformPoint(new()).Y - anchor.Top : anchor.Offset;
            scroll.ChangeView(null, Math.Clamp(offset, 0, scroll.ScrollableHeight), null, disableAnimation: true);
        });

    public static void Reveal(FrameworkElement element) => element.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (!element.IsLoaded || element.Visibility != Visibility.Visible) return;
        element.UpdateLayout();
        element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
    });

    public static void Restore(ScrollViewer scroll, double offset) => scroll.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
    {
        if (!scroll.IsLoaded) return;
        scroll.UpdateLayout();
        scroll.ChangeView(null, Math.Clamp(offset, 0, scroll.ScrollableHeight), null, disableAnimation: true);
    });
}
