using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>Restore a browsing offset after newly loaded rows have been measured.</summary>
internal static class BrowseScroll
{
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
