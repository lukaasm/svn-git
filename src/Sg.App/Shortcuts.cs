using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Sg.App;

/// <summary>Keyboard for the paths you repeat. Accelerators live on the window root, so any focus reaches them.</summary>
public static class Shortcuts
{
    public static void Add(Window window, VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        if (window.Content is FrameworkElement root) Add(root, key, modifiers, action);
    }

    /// <summary>
    /// The same, on an element: a page registers its keys on itself, so they reach it while it is on
    /// screen and go away with it. On the window root they would have stacked up, page after page.
    /// </summary>
    public static void Add(FrameworkElement root, VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        root.KeyboardAccelerators.Add(accelerator);
        // These sit on the window root so any focus reaches them, and WinUI answers that by naming the
        // key in a tooltip on whatever the accelerator is attached to. That is the whole window here,
        // so "Esc" followed the pointer over the content. Settings lists every shortcut instead.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    public static void Add(Window window, VirtualKey key, Action action) =>
        Add(window, key, VirtualKeyModifiers.None, action);

    public static void Add(FrameworkElement root, VirtualKey key, Action action) =>
        Add(root, key, VirtualKeyModifiers.None, action);

    /// <summary>
    /// F7 and Shift+F7 walk the changes of a diff. Every window that shows one wants the same pair,
    /// and each of the seven of them used to bind it itself.
    /// </summary>
    public static void DiffNavigation(FrameworkElement root, DiffView diff)
    {
        Add(root, VirtualKey.F7, () => diff.GoToDiff(next: true));
        Add(root, VirtualKey.F7, VirtualKeyModifiers.Shift, () => diff.GoToDiff(next: false));
    }

    /// <summary>
    /// Escape closes the window, unless you are typing. A half-written commit message is worth
    /// more than the shortcut.
    /// </summary>
    public static void CloseOnEscape(Window window)
    {
        Add(window, VirtualKey.Escape, () =>
        {
            if (window.Content is FrameworkElement root
                && FocusManager.GetFocusedElement(root.XamlRoot) is TextBox) return;
            window.Close();
        });
    }
}
