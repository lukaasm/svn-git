using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Sg.App;

/// <summary>A virtualized list's reading position, identified by its first visible row rather than selection.</summary>
internal sealed class ListViewport
{
    readonly ListView _list;
    readonly Func<object, string> _key;
    ScrollViewer? _scroll;
    BrowseScroll.Anchor? _pending;
    int _version;

    public ListViewport(ListView list, Func<object, string> key)
    {
        _list = list; _key = key;
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Cancel()), true);
        list.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => Cancel()), true);
        list.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => Cancel()), true);
        list.Loaded += (_, _) => Queue();
        list.SizeChanged += (_, _) => { if (_pending != null) Queue(); };
    }
    void Cancel() { ++_version; _pending = null; }

    public BrowseScroll.Anchor? Capture()
    {
        if (_pending != null) return _pending;
        var scroll = Scroll();
        if (scroll == null || _list.Items.Count == 0) return null;
        var first = _list.ItemsPanelRoot is ItemsStackPanel panel ? Math.Max(0, panel.FirstVisibleIndex) : 0;
        for (var i = first; i < _list.Items.Count; i++)
        {
            if (_list.ContainerFromIndex(i) is not FrameworkElement row) break;
            var top = row.TransformToVisual(scroll).TransformPoint(new()).Y;
            if (top >= scroll.ViewportHeight) break;
            if (top + row.ActualHeight > 0) return new(_key(_list.Items[i]), top, scroll.VerticalOffset);
        }
        return new(null, 0, scroll.VerticalOffset);
    }

    public void Restore(BrowseScroll.Anchor? anchor)
    {
        Cancel(); _pending = anchor;
        Queue();
    }
    void Queue()
    {
        if (_pending is not { } anchor || !_list.IsLoaded || _list.ActualHeight <= 0) return;
        var version = ++_version;
        var source = _list.ItemsSource;
        bool Current() => version == _version && _list.IsLoaded && ReferenceEquals(source, _list.ItemsSource);
        _list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!Current() || Scroll() is not { } scroll) return;
            _list.UpdateLayout();
            var item = anchor.Key == null ? null : _list.Items.FirstOrDefault(i => _key(i) == anchor.Key);
            // Realize just the anchor row; retaining or measuring every container defeats virtualization.
            if (item != null) _list.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
            _list.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!Current()) return;
                _list.UpdateLayout();
                var offset = item != null && _list.ContainerFromItem(item) is FrameworkElement row
                    ? scroll.VerticalOffset + row.TransformToVisual(scroll).TransformPoint(new()).Y - anchor.Top
                    : anchor.Offset;
                scroll.ChangeView(null, Math.Clamp(offset, 0, scroll.ScrollableHeight), null, disableAnimation: true);
                _pending = null;
            });
        });
    }
    ScrollViewer? Scroll() => _scroll ??= Find(_list);
    static ScrollViewer? Find(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (Find(child) is { } nested) return nested;
        }
        return null;
    }
}
