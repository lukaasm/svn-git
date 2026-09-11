using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Sg.App;

public static class WindowHelper
{
    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>True when this window is the one the user works in right now.</summary>
    public static bool IsForeground(Window window) =>
        GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(window);

    /// <summary>True when the window an element is on is the one the user works in. An element off screen is not.</summary>
    public static bool IsForeground(UIElement element) =>
        WindowOf(element) is { } w && IsForeground(w);

    /// <summary>The window an element is shown in, through the page host it sits in. Null off screen.</summary>
    public static Window? WindowOf(UIElement element) => NavHost.Of(element)?.Window;

    /// <summary>
    /// Opens a window in front of the one that opened it. Activate on its own is not enough: it shows
    /// the window and gives it the keyboard focus inside XAML, but leaves the foreground where it was,
    /// so a second window came up behind the overview. This process already owns the foreground, so
    /// Windows lets it hand the foreground over.
    /// </summary>
    public static void Show(Window window)
    {
        var app = window.AppWindow;
        app.Show();   // the overview lives on while it is hidden in the tray
        window.Activate();
        app.MoveInZOrderAtTop();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
    }

    /// <summary>
    /// The window a picker belongs to, as a handle. A dialog with no owner can be lost behind the
    /// window that asked for it, and the window it belongs to has to be the one it blocks.
    /// </summary>
    static IntPtr HandleOf(object owner)
    {
        var window = owner as Window ?? (owner is UIElement e ? WindowOf(e) : null);
        return window == null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    /// <summary>Asks for a folder, owned by this window.</summary>
    public static Task<string?> PickFolder(object owner, string? startIn = null)
    {
        var hwnd = HandleOf(owner);
        return Task.FromResult(hwnd == IntPtr.Zero ? null : NativePicker.Folder(hwnd, startIn));
    }

    /// <summary>Asks where to write a file, owned by this window. The shell asks about overwriting.</summary>
    public static Task<string?> PickSaveFile(object owner, string suggestedName, string typeLabel, string extension)
    {
        var hwnd = HandleOf(owner);
        return Task.FromResult(hwnd == IntPtr.Zero ? null : NativePicker.SaveFile(hwnd, suggestedName, extension));
    }

    /// <summary>Asks for one file of one kind, owned by this window.</summary>
    public static Task<string?> PickOpenFile(object owner, params string[] extensions)
    {
        var hwnd = HandleOf(owner);
        return Task.FromResult(hwnd == IntPtr.Zero ? null : NativePicker.OpenFile(hwnd, extensions));
    }

    /// <summary>Sizes a window in logical pixels, so it looks the same at every screen scaling.</summary>
    public static void Resize(Window window, int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        window.AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    /// <summary>
    /// The Windows 11 look: content under the title bar, Mica behind everything, caption buttons that
    /// follow the theme, and the Log button on the right of the title bar. The log window itself is
    /// the one window without it.
    /// </summary>
    public static void Chrome(Window window, TitleBar titleBar, int width, int height, bool log = true)
    {
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        if (log) titleBar.RightHeader = LogButton();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "sg.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
        Resize(window, width, height);
    }

    /// <summary>
    /// The way to the lines: every line every operation wrote, in the one window that holds them. In
    /// the title bar it is in the same place on every window, where the foot of every page used to
    /// carry a copy of it.
    /// </summary>
    static Button LogButton()
    {
        var b = new IconButton
        {
            Glyph = "\uE9D9",
            Text = "Log",
            Style = (Style)Application.Current.Resources["QuietButton"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ToolTipService.SetToolTip(b, "Open the log: every line every sg operation wrote, in one window.");
        b.Click += (_, _) => OutputWindow.Show();
        return b;
    }
}
