using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Sg.Core;

namespace Sg.App;

public partial class App : Application
{
    public static string[] Args { get; private set; } = [];
    static MainWindow? _main;
    static TrayIcon? _tray;
    static Microsoft.UI.Dispatching.DispatcherQueue? _queue;
    static bool _exiting;

    const int MenuOpen = 1, MenuMonitor = 2, MenuCheck = 3, MenuSettings = 4, MenuExit = 9;

    public App()
    {
        InitializeComponent();
        // Before anything else can throw. Without it the first exception out of any async void handler
        // ends the process, and what is left to report is that the window went away.
        Crash.Install(this);
    }

    /// <summary>sg-ui.exe [action] [path] or --tray. The context menu passes the folder that was clicked.</summary>
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        _queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var (action, path, toast) = Parse(Args);
        Session.Open(path ?? Environment.CurrentDirectory);
        // Before any window is made, so the first list of files is drawn in the right colours rather
        // than in sg's and then repainted.
        Themes.ApplyToApp(Themes.Current);
        Notifications.Register(args => RunOnUi(() => HandleToast(args)));
        try { AppInstance.GetCurrent().Activated += (_, a) => RunOnUi(() => OnRedirected(a)); }
        catch (Exception) { /* single instance not available, every launch is its own process */ }

        var hidden = action == "tray";
        // Started by a toast, pressed after sg had closed: what its button asked for is what gets opened.
        var window = toast != null ? HandleToast(toast) : OpenFor(hidden ? "overview" : action, path, activate: !hidden);
        // A crash dialog needs a window to live in, and this is the first one there is.
        Crash.Owner(window);
        MonitorService.Start(window.DispatcherQueue);
        if (_main != null && (Session.Settings.Tray || hidden)) EnsureTray();
        WarnIfElevated(window, hidden);
    }

    /// <summary>
    /// Said once, at the start, and only when it is true. Elevation is not something anybody chooses
    /// for sg: it is inherited from the terminal that started it, usually one an installer asked to
    /// elevate, and the first thing it breaks is every Browse in the app. So it is said before the
    /// first Browse rather than after, and while there is still nothing to lose by restarting.
    ///
    /// Not while the window is hidden in the tray: a modal over a window nobody can see is a modal
    /// nobody can dismiss, and a tray start is one Windows did at login rather than one anybody did.
    /// </summary>
    static void WarnIfElevated(Window window, bool hidden)
    {
        if (hidden || !Elevation.ShouldWarn) return;
        // After the window has been laid out. A ContentDialog wants a XamlRoot, and the root of a
        // window that has only just been made is not there yet.
        window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            try { await Dialogs.Info(window, Elevation.Title, Elevation.Warning); }
            catch (Exception) { /* another dialog owns the window; the log still carries the line */ }
        });
        Session.Log.Warn(Elevation.Title + ". " + Elevation.Warning.Replace("\n", " ").Replace("  ", " ").Trim());
    }

    static void RunOnUi(Action a)
    {
        if (_queue == null || _queue.HasThreadAccess) a();
        else _queue.TryEnqueue(() => a());
    }

    /// <summary>Another sg-ui was started while this one runs. Do what it was asked to do, here.</summary>
    static void OnRedirected(AppActivationArguments a)
    {
        try
        {
            if (a.Kind == ExtendedActivationKind.Launch && a.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs la)
            {
                var argv = SplitCommandLine(la.Arguments);
                if (argv.Length > 0 && (argv[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || argv[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) argv = argv.Skip(1).ToArray();
                // The windows read flags like --select off Args, and this launch is the one that asked.
                Args = argv;
                var (action, path, _) = Parse(argv);
                if (path != null) Session.Open(path);
                OpenFor(action == "tray" ? "overview" : action, path, activate: action != "tray");
            }
            else if (a.Kind == ExtendedActivationKind.AppNotification && a.Data is AppNotificationActivatedEventArgs na)
                HandleToast(na.Arguments);
            else ShowMain();
        }
        catch (Exception) { ShowMain(); }
    }

    /// <summary>
    /// A toast was pressed, or a button on one. Every toast names an action and what it is about, and
    /// this is the one place that reads them, whether sg was running or Windows just started it for the
    /// press. What a window can only do once it has read the root goes through its start action, the
    /// way the Explorer menu's "sync" does, so the cold start and the warm one land in the same code.
    /// </summary>
    static Window HandleToast(IDictionary<string, string> args)
    {
        string? Get(string key) => args.TryGetValue(key, out var v) ? v : null;
        var action = Get("action") ?? "";
        var checkout = Get("checkout");
        var co = checkout == null ? null
            : Session.Root?.Config.Checkouts.FirstOrDefault(c => c.Name.Equals(checkout, StringComparison.OrdinalIgnoreCase));
        switch (action)
        {
            case "monitor":
                ShowMonitor(Get("id"));
                return _main!;
            case "log" when Get("path") is { } path:
                return OpenFor("log", path, activate: true);
            case "applog":
            {
                var main = EnsureMain(null, null);
                WindowHelper.Show(main);
                return OutputWindow.Show();
            }
            case "sync" or "backup" or "update" or "overview":
            {
                if (_main == null)
                {
                    var main = EnsureMain(action == "overview" ? null : action, co?.Path);
                    WindowHelper.Show(main);
                    return main;
                }
                _main.FromToast(action, co?.Name);
                WindowHelper.Show(_main);
                return _main;
            }
            default:
                ShowMain();
                return _main!;
        }
    }

    /// <summary>The project monitor is a page of the overview, so a toast brings the overview up and turns it to that page.</summary>
    static void ShowMonitor(string? id)
    {
        var main = EnsureMain(null, null);
        main.ShowMonitor(id);
        WindowHelper.Show(main);
    }

    /// <summary>
    /// The command line, or the arguments of the toast that started this process. Windows starts an
    /// app for a toast press with an argument that begins with four dashes and nothing readable after
    /// it; what the toast carried is asked for separately, and comes back as Toast.
    /// </summary>
    static (string Action, string? Path, IDictionary<string, string>? Toast) Parse(string[] argv)
    {
        var fromToast = argv.Length > 0 && argv[0].StartsWith("----");
        var action = argv.Length > 0 && !fromToast ? argv[0].ToLowerInvariant() : "overview";
        var path = argv.Length > 1 && !fromToast ? argv[1].TrimEnd('"') : null;
        if (action == "--tray") action = "tray";
        IDictionary<string, string>? toast = null;
        if (fromToast)
        {
            try
            {
                var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
                if (activated.Kind == ExtendedActivationKind.AppNotification && activated.Data is AppNotificationActivatedEventArgs na)
                    toast = na.Arguments;
            }
            catch (Exception) { /* plain launch */ }
        }
        return (action, path, toast);
    }

    /// <summary>
    /// Opens what an action asks for. The Explorer menu's actions get a window of their own around
    /// their page, the Tortoise way; the overview is made once and hidden into the tray instead of closed.
    /// </summary>
    static Window OpenFor(string action, string? path, bool activate)
    {
        // "git worktree list" and "git config --get" are two blocking processes, and only the actions that
        // are about a folder need them. This runs on the launch path and again on every redirect, where a
        // second sg-ui hands its command line to this one: opening the overview, the monitor or the
        // settings used to ask git twice for nothing, with the window coming up behind it.
        var aboutAPath = action is "push" or "commit" or "log" or "blame" or "shelf" or "merge" or "resolve" or "server-branch";
        var wt = aboutAPath && path != null && Session.Root != null ? Session.WorktreeAt(path) : null;
        var worktree = wt?.Path;
        var branch = wt?.Branch;
        var baseName = branch != null ? Session.Root?.Git.ConfigGet($"branch.{branch}.sgBase") : null;
        var checkout = aboutAPath && path != null && worktree == null ? Session.Root?.CheckoutContaining(path) : null;
        var select = Args.Contains("--select");
        Window window;
        switch (action)
        {
            case "push" when worktree != null:
                window = PageWindow.For(() => new PushPage(worktree) { Checkout = baseName, Branch = branch }, "push:" + worktree, 1500, 920);
                break;
            case "commit" when worktree != null:
                window = PageWindow.For(() => new CommitPage(worktree, autoSelect: select) { Checkout = baseName, Branch = branch }, "commit:" + worktree, 1500, 920);
                break;
            case "commit" when checkout != null:
                window = PageWindow.For(() => new SvnCommitPage(checkout, autoSelect: select), "changes:" + checkout.Name, 1500, 920);
                break;
            case "log" when worktree != null:
                window = PageWindow.For(() => new LogPage(worktree, autoSelect: select) { Checkout = baseName, Branch = branch }, "log:" + worktree, 1500, 920);
                break;
            case "log" when checkout != null:
                window = PageWindow.For(() => new SvnLogPage(checkout, autoSelect: select), "svnlog:" + checkout.Name, 1500, 920);
                break;
            // Blame names a file, not a folder, so the page gets its path inside whatever holds it.
            case "blame" when worktree != null && path != null:
                window = PageWindow.For(() => new BlamePage(PathUtil.Rel(PathUtil.RelativeTo(worktree, path)), worktree, null)
                    { Checkout = baseName, Branch = branch }, "blame:" + path, 1400, 900);
                break;
            case "blame" when checkout != null && path != null:
                window = PageWindow.For(() => new BlamePage(PathUtil.Rel(PathUtil.RelativeTo(checkout.Path, path)), null, checkout)
                    { Checkout = checkout.Name }, "blame:" + path, 1400, 900);
                break;
            case "shelf" when worktree != null:
                window = PageWindow.For(() => new ShelfPage(null, worktree, branch) { Checkout = baseName, Branch = branch }, "shelf:" + worktree, 1400, 900);
                break;
            case "shelf" when checkout != null:
                window = PageWindow.For(() => new ShelfPage(checkout) { Checkout = checkout.Name }, "shelf:" + checkout.Name, 1400, 900);
                break;
            case "merge" when checkout != null:
                window = PageWindow.For(() => new MergePage(checkout), "merge:" + checkout.Name, 1400, 900);
                break;
            case "resolve" when worktree != null:
                window = PageWindow.For(() => new ConflictPage(worktree) { Checkout = baseName, Branch = branch }, "resolve:" + worktree, 1500, 900);
                break;
            case "server-branch" when Session.Root != null:
            {
                var co = path != null ? Session.Root.CheckoutContaining(path) : null;
                window = PageWindow.For(() => new ServerBranchPage(co), "server-branch", 1100, 800);
                break;
            }
            // The file, not a folder: an export names the checkout it belongs to itself.
            case "import" when path != null:
                window = PageWindow.For(() => new ImportPage(path), "import:" + path, 1100, 860);
                break;
            case "settings":
                window = PageWindow.For(() => new SettingsPage(), "settings", 960, 820);
                break;
            case "monitor":
            {
                var main = EnsureMain(null, null);
                main.ShowMonitor(null);
                window = main;
                break;
            }
            default:
                window = EnsureMain(action == "overview" ? null : action, path);
                break;
        }
        if (activate) WindowHelper.Show(window);
        return window;
    }

    static MainWindow EnsureMain(string? startAction, string? path)
    {
        if (_main != null) return _main;
        var main = new MainWindow(startAction, path);
        main.AppWindow.Closing += (_, args) =>
        {
            if (_exiting || _tray == null) return;
            // Keep running in the tray. The monitor keeps checking.
            args.Cancel = true;
            main.AppWindow.Hide();
        };
        main.Closed += (_, _) =>
        {
            _main = null;
            _tray?.Dispose();
            _tray = null;
        };
        _main = main;
        return main;
    }

    static void ShowMain()
    {
        var main = EnsureMain(null, null);
        WindowHelper.Show(main);
    }

    public static void EnsureTray()
    {
        if (_tray != null || _main == null) return;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "sg.ico");
        _tray = new TrayIcon("sg", icon, ShowMain, new (int, string)[]
        {
            (MenuOpen, "Open sg"),
            (MenuMonitor, "Project monitor"),
            (MenuCheck, "Check repositories now"),
            (MenuSettings, "Settings"),
            (0, ""),
            (MenuExit, "Exit"),
        }, OnTrayCommand);
        MonitorService.Changed += UpdateTrayTip;
        UpdateTrayTip();
    }

    public static void RemoveTray()
    {
        MonitorService.Changed -= UpdateTrayTip;
        _tray?.Dispose();
        _tray = null;
    }

    static void UpdateTrayTip()
    {
        var unread = MonitorService.TotalUnread;
        _tray?.SetTip(unread > 0 ? $"sg: {unread} unread commit(s)" : "sg");
    }

    static void OnTrayCommand(int id)
    {
        switch (id)
        {
            case MenuOpen: ShowMain(); break;
            case MenuMonitor: ShowMonitor(null); break;
            case MenuCheck: _ = MonitorService.CheckAllAsync(); break;
            case MenuSettings:
            {
                var main = EnsureMain(null, null);
                main.ShowSettings();
                WindowHelper.Show(main);
                break;
            }
            case MenuExit: ExitApp(); break;
        }
    }

    /// <summary>
    /// Ends the app. The toast registration is deliberately left in place: it belongs to the install,
    /// not to this run, and taking it down on the way out is what left the app id half written, with
    /// Windows dropping every toast from it. A toast already on screen can still open sg afterwards.
    /// </summary>
    public static void ExitApp()
    {
        _exiting = true;
        RemoveTray();
        Current.Exit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string cmd, out int count);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    static string[] SplitCommandLine(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return [];
        var p = CommandLineToArgvW(cmd, out var n);
        if (p == IntPtr.Zero) return [];
        try
        {
            var res = new string[n];
            for (var i = 0; i < n; i++) res[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size)) ?? "";
            return res;
        }
        finally { LocalFree(p); }
    }
}
