using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Sg.App;

/// <summary>
/// The menu of a list's rows, for the mouse and the keyboard alike. A right click opens it at the pointer
/// on the row under it; Shift+F10 and the Menu key open it on the focused row, or on the selected one
/// when the list itself has focus. build gets the element the gesture came from and answers the menu
/// for the row that element belongs to, or null for none.
/// </summary>
public static class RowMenu
{
    public static void Attach(ListViewBase list, Func<DependencyObject, MenuFlyout?> build)
    {
        list.RightTapped += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source || build(source) is not { } menu) return;
            menu.ShowAt(list, new FlyoutShowOptions { Position = e.GetPosition(list) });
            e.Handled = true;
        };
        // A pointer raises this too, after RightTapped has shown the menu; only the keyboard comes
        // without a position.
        list.ContextRequested += (_, e) =>
        {
            if (e.TryGetPosition(list, out Windows.Foundation.Point _)) return;
            var target = e.OriginalSource as FrameworkElement;
            if (target == null || ReferenceEquals(target, list))
                target = list.SelectedItem != null ? list.ContainerFromItem(list.SelectedItem) as FrameworkElement : null;
            if (target == null || build(target) is not { } menu) return;
            menu.ShowAt(target);
            e.Handled = true;
        };
    }
}
