using System.Runtime.InteropServices;

namespace Sg.App;

/// <summary>A Win32 notification area icon with a click action and a right-click menu. Lives on the UI thread.</summary>
public sealed class TrayIcon : IDisposable
{
    const uint WM_APP_TRAY = 0x8001;
    const uint WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002, TPM_BOTTOMALIGN = 0x0020;
    const uint MF_STRING = 0, MF_SEPARATOR = 0x800;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEX cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);

    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly WndProc _proc;
    readonly IntPtr _hwnd;
    readonly IntPtr _icon;
    readonly uint _taskbarCreated;
    readonly Action _onClick;
    readonly Action<int> _onCommand;
    readonly IReadOnlyList<(int Id, string Text)> _menu;
    NOTIFYICONDATA _data;
    bool _disposed;

    /// <param name="menu">Menu items. Id 0 is a separator.</param>
    public TrayIcon(string tip, string iconPath, Action onClick, IReadOnlyList<(int Id, string Text)> menu, Action<int> onCommand)
    {
        _onClick = onClick;
        _onCommand = onCommand;
        _menu = menu;
        _proc = WndProcImpl;
        var inst = GetModuleHandleW(null);
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = inst,
            lpszClassName = "SgTrayWindow" + Environment.ProcessId,
        };
        RegisterClassExW(ref cls);
        _hwnd = CreateWindowExW(0, cls.lpszClassName, "sg tray", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, inst, IntPtr.Zero);
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = File.Exists(iconPath) ? LoadImageW(IntPtr.Zero, iconPath, 1, 16, 16, 0x10) : IntPtr.Zero;
        _data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = Fit(tip),
            szInfo = "",
            szInfoTitle = "",
        };
        Shell_NotifyIconW(NIM_ADD, ref _data);
    }

    static string Fit(string tip) => tip.Length > 120 ? tip[..120] : tip;

    public void SetTip(string tip)
    {
        if (_disposed) return;
        _data.szTip = Fit(tip);
        Shell_NotifyIconW(NIM_MODIFY, ref _data);
    }

    IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_APP_TRAY)
        {
            var ev = (uint)(l.ToInt64() & 0xFFFF);
            if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) _onClick();
            else if (ev == WM_RBUTTONUP) ShowMenu();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && !_disposed)
        {
            // Explorer restarted, the icon is gone. Put it back.
            Shell_NotifyIconW(NIM_ADD, ref _data);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, w, l);
    }

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        foreach (var (id, text) in _menu)
        {
            if (id == 0) AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            else AppendMenuW(menu, MF_STRING, (UIntPtr)id, text);
        }
        SetForegroundWindow(_hwnd);
        GetCursorPos(out var p);
        var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd > 0) _onCommand(cmd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIconW(NIM_DELETE, ref _data);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        DestroyWindow(_hwnd);
    }
}
