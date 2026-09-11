using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// The overview: checkouts in the left pane, the page for the selected one on the right, and behind
/// it every page a checkout or a worktree leads to, with the back and forward stack over them. This
/// window owns the state of the root: the status, what the server said, and the pane that shows both.
/// </summary>
public sealed partial class MainWindow : Window
{
    readonly string? _startAction;
    readonly string? _startPath;
    StatusResult? _status;
    CheckoutRow? _current;
    bool _restoringSelection;
    bool _syncingPane;
    int _generation;
    bool _checking;
    /// <summary>
    /// A check was asked for while one was already running. The one running belongs to a state that has
    /// since been thrown away, so it abandons itself on the generation guard: without this the badges
    /// were never written at all, and the card sat under a ring that never stopped.
    /// </summary>
    bool _recheckWanted;

    /// <summary>What the server last said about each checkout. Sync and Refresh forget one to ask again.</summary>
    internal readonly Dictionary<string, RemoteCheckResult> Remote = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Why the last check of a checkout failed, kept beside the successes. Without it a refused server
    /// left no trace at all, and every later look at the card read "no answer yet": the ring came back
    /// and spun for the life of the window over the words "checking the server...".
    /// </summary>
    internal readonly Dictionary<string, string> RemoteErrors = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>How many files are edited directly in each checkout.</summary>
    internal readonly Dictionary<string, int> LocalEdits = new(StringComparer.OrdinalIgnoreCase);

    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _monitor;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _updates;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _backup;
    /// <summary>One backup a few seconds after the last of a run of changes, rather than one per change.</summary>
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _backupSoon;
    bool _backingUp;
    UpdateCheck? _update;
    bool _updating;

    /// <summary>The one overview page. The pane points it at a checkout; it lives as long as the window.</summary>
    internal readonly CheckoutPage Overview;

    StatusStrip Pane => Overview.Strip;

    public MainWindow(string? startAction = null, string? startPath = null)
    {
        InitializeComponent();
        _startAction = startAction;
        _startPath = startPath;
        WindowHelper.Chrome(this, AppTitleBar, 1280, 820);
        Title = "sg";
        Overview = new CheckoutPage(this);
        Session.Log.Sink = Pane;
        Host.Window = this;
        Host.Changed += SyncHeader;
        Host.AttachShortcuts((FrameworkElement)Content, escapeCloses: false);
        _monitor = DispatcherQueue.CreateTimer();
        _monitor.Tick += (_, _) => _ = MonitorTickAsync();
        ArmMonitor();
        _updates = DispatcherQueue.CreateTimer();
        _updates.Tick += (_, _) => _ = CheckUpdateAsync();
        ArmUpdates();
        _backup = DispatcherQueue.CreateTimer();
        _backup.Tick += (_, _) => _ = BackupTickAsync();
        ArmBackup();
        _backupSoon = DispatcherQueue.CreateTimer();
        _backupSoon.Interval = TimeSpan.FromSeconds(8);
        _backupSoon.IsRepeating = false;
        _backupSoon.Tick += (_, _) => _ = BackupAsync(quiet: true);
        Nav.Loaded += (_, _) => DispatcherQueue.TryEnqueue(EnglishChrome);
        Shortcuts.Add(this, VirtualKey.F5, () => _ = RefreshAllAsync());
        MonitorService.Changed += UpdateMonitorBadge;
        // Opening or closing the pane swaps which of the two unread markers is on show.
        Nav.PaneOpened += (_, _) => UpdateMonitorBadge();
        Nav.PaneClosed += (_, _) => UpdateMonitorBadge();
        Closed += (_, _) => { _monitor.Stop(); _updates.Stop(); _backup.Stop(); _backupSoon.Stop(); MonitorService.Changed -= UpdateMonitorBadge; };
        UpdateMonitorBadge();
        Updates.Sweep();
        ShowOverview((CheckoutRow?)null);
        _ = RefreshAsync(runStartAction: true);
        _ = CheckUpdateAsync();
    }

    /// <summary>
    /// WinUI supplies the settings entry and the pane toggle itself and labels them in the Windows
    /// display language. Every other string in this app is English, so relabel those two.
    /// </summary>
    void EnglishChrome()
    {
        if (Nav.SettingsItem is NavigationViewItem settings)
        {
            settings.Content = "Settings";
            AutomationProperties.SetName(settings, "Settings");
            ToolTipService.SetToolTip(settings, "Settings");
        }
        foreach (var child in Descendants(Nav))
        {
            if (child is not Button b || b.Name != "TogglePaneButton") continue;
            AutomationProperties.SetName(b, "Navigation");
            ToolTipService.SetToolTip(b, "Navigation");
        }
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    // ---- the header over the page: path, subtitle, back and forward ----

    /// <summary>
    /// The path over the page and the pane's selection both follow the host, so back and forward
    /// move them the same way a click does. Written from the page's checkout and branch, not by the
    /// page: a page does not know whether it is behind the pane or in a window of its own.
    /// </summary>
    void SyncHeader()
    {
        var page = Host.Current;
        var crumbs = new List<Crumb>();
        if (page is CheckoutPage) crumbs.Add(new Crumb(page.Title));
        else
        {
            if (page?.Checkout is { } co)
            {
                crumbs.Add(new Crumb(co, () => ShowOverview(co)));
                if (page.Branch is { } br) crumbs.Add(new Crumb(br, () => { ShowOverview(co); Overview.Expand(br); }));
            }
            crumbs.Add(new Crumb(page?.Title ?? "sg"));
        }
        Crumbs.ItemsSource = crumbs;
        SubtitleText.Text = page?.Subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(page?.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        Nav.IsBackEnabled = Host.CanGoBack;
        ForwardButton.Visibility = Host.CanGoForward ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = Session.Root != null ? Visibility.Visible : Visibility.Collapsed;

        // The pane highlights the checkout the page is about, the monitor, or the settings.
        var key = Host.CurrentKey ?? "";
        object? select = key switch
        {
            "settings" => Nav.SettingsItem,
            "monitor" => MonitorItem,
            _ when page?.Checkout != null => Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as CheckoutRow)?.Name == page.Checkout),
            _ => null,
        };
        if (select == null || ReferenceEquals(Nav.SelectedItem, select)) return;
        _syncingPane = true;
        try { Nav.SelectedItem = select; }
        finally { _syncingPane = false; }
    }

    void Crumbs_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args) => (args.Item as Crumb)?.Go?.Invoke();

    void Nav_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => Host.Back();

    void Forward_Click(object sender, RoutedEventArgs e) => Host.Forward();

    /// <summary>
    /// The overview page for one checkout, by name. Going to the checkout that is already behind
    /// this page steps back to it, so the stack does not grow a copy for every crumb click.
    /// </summary>
    void ShowOverview(string? name)
    {
        var row = name == null ? null : Nav.MenuItems.OfType<NavigationViewItem>().Select(i => i.Tag as CheckoutRow).FirstOrDefault(r => r?.Name == name);
        ShowOverview(row);
    }

    void ShowOverview(CheckoutRow? row)
    {
        var key = row == null ? "overview" : "checkout:" + row.Name;
        if (Host.CurrentKey != key && Host.BackTo(key)) { Overview.Show(row, _status); return; }
        Host.Go(() => { Overview.Show(row, _status); return Overview; }, key);
    }

    // ---- updates ----

    /// <summary>The periodic GitHub build check. Same shape as the server check below.</summary>
    void ArmUpdates()
    {
        var minutes = Session.Settings.UpdateCheckMinutes;
        _updates.Stop();
        if (minutes <= 0) return;
        _updates.Interval = TimeSpan.FromMinutes(minutes);
        _updates.IsRepeating = true;
        _updates.Start();
    }

    async Task CheckUpdateAsync()
    {
        ArmUpdates();
        if (_updating || Session.Settings.UpdateCheckMinutes <= 0) return;
        var check = await Updates.CheckAsync();
        if (check == null || !check.Newer) return;

        var first = _update == null || _update.Remote.RunId != check.Remote.RunId;
        _update = check;
        ToolTipService.SetToolTip(UpdateItem,
            $"A newer sg build is on GitHub.\n\nInstalled: {check.Local?.ToString() ?? "unknown build"}\n"
            + $"Newest: {check.Remote}\n\n"
            + $"Pressing this closes sg, writes the build over {Updates.InstallDir()}, then opens sg again.");
        UpdateItem.Visibility = Visibility.Visible;
        if (!first) return;
        Pane.Append($"{DateTime.Now:HH:mm}  update: {check.Remote} is ready to install.");
        Notifications.Show("A newer sg build is ready", check.Remote + ". Open sg and press 'Update and restart'.");
    }

    // ---- backups ----

    /// <summary>The periodic backup. Same shape as the update check: re-armed from the settings each time it fires.</summary>
    void ArmBackup()
    {
        var minutes = Session.Settings.BackupMinutes;
        _backup.Stop();
        if (minutes <= 0) return;
        _backup.Interval = TimeSpan.FromMinutes(minutes);
        _backup.IsRepeating = true;
        _backup.Start();
    }

    async Task BackupTickAsync()
    {
        ArmBackup();
        if (Session.Settings.BackupMinutes <= 0) return;
        await BackupAsync(quiet: true);
    }

    /// <summary>
    /// A backup a few seconds from now, for the moments a branch just changed: coming back from a commit
    /// page, a shelve, a rebase. Several in a row become one. Nothing when the timer is off: that is the
    /// setting for "only when I press it".
    /// </summary>
    internal void BackupSoon()
    {
        if (Session.Root == null || !Backup.Configured(Session.Root) || Session.Settings.BackupMinutes <= 0) return;
        _backupSoon.Stop();
        _backupSoon.Start();
    }

    /// <summary>
    /// Every branch, the uncommitted changes and the shelves, to the backup repository. Quiet is the
    /// timer: a line in the log when something went, a toast when something was refused. Loud is a
    /// button: the strip shows the run.
    /// </summary>
    internal async Task<BackupResult?> BackupAsync(bool quiet)
    {
        var root = Session.Root;
        if (root == null || !Backup.Configured(root) || _backingUp) return null;
        _backingUp = true;
        try
        {
            var res = quiet
                ? await Runner.Quiet(Pane, () => Backup.Run(root))
                : await Runner.Run(Pane, "backup", () => Backup.Run(root));
            if (res == null) return null;
            var rejected = res.Items.Where(i => i.Rejected).Select(i => i.Name).ToList();
            if (!quiet || res.Pushed > 0 || rejected.Count > 0 || res.Items.Any(i => i.Failed))
                Pane.Append($"{DateTime.Now:HH:mm}  " + BackupPage.Sentence(res));
            if (rejected.Count > 0)
            {
                Pane.Append(res.Items.First(i => i.Rejected).Why ?? "");
                if (quiet)
                    Notifications.Show("Backup refused for " + string.Join(", ", rejected),
                        "The backup holds a version that did not come from here. Open Backup on the checkout to restore it, or back up under a folder of this machine's own.");
            }
            await RefreshAsync();
            return res;
        }
        finally { _backingUp = false; }
    }

    void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked) ShowSettings();
        else if (ReferenceEquals(args.InvokedItemContainer, UpdateItem)) _ = UpdateAsync();
        else if (ReferenceEquals(args.InvokedItemContainer, MonitorItem)) ShowMonitor(null);
        // Pressing the checkout that is already lit. SelectionChanged does not fire for it, and the pane
        // lights the checkout of whatever page is open, so from a commit or a push window the one obvious
        // way back to the card did nothing at all.
        else if (args.InvokedItemContainer?.Tag is CheckoutRow invoked) { _current = invoked; ShowOverview(invoked); }
    }

    /// <summary>The settings page. Leaving it re-arms the server check, whose interval it may have changed.</summary>
    public void ShowSettings() => Host.Go(() =>
    {
        var p = new SettingsPage();
        p.Left += () => { ArmMonitor(); ArmUpdates(); ArmBackup(); };
        return p;
    }, "settings");

    /// <summary>The project monitor, opened from the pane, the tray, or a toast that names a repository.</summary>
    public void ShowMonitor(string? selectId)
    {
        if (Host.Current is MonitorPage open && Host.CurrentKey == "monitor")
        {
            if (selectId != null) open.SelectById(selectId);
            return;
        }
        Host.Go(() => new MonitorPage(selectId), "monitor");
    }

    async Task UpdateAsync()
    {
        var check = _update;
        if (check == null || _updating) return;
        if (!await Dialogs.Confirm(this, "Update and restart",
                $"sg closes, {check.Remote} is written over {Updates.InstallDir()}, then sg opens again.\n\n"
                + "Anything you typed in another sg window is lost.", "Update"))
            return;

        _updating = true;
        UpdateItem.IsEnabled = false;
        try
        {
            Updates.StartInstaller();
        }
        catch (SgException ex)
        {
            _updating = false;
            UpdateItem.IsEnabled = true;
            await Dialogs.Info(this, "Update", ex.Message);
            return;
        }
        ExitForUpdate();
    }

    /// <summary>
    /// The installer is waiting for this process, and it will not install while the app is alive.
    /// App.ExitApp takes the tray icon down first, so the overview really closes instead of hiding.
    /// Application.Exit can leave the process up when another window holds it, so make sure.
    /// </summary>
    static void ExitForUpdate()
    {
        App.ExitApp();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Environment.Exit(0);
        });
    }

    /// <summary>The chip while the pane shows words, the platform badge while it is a rail of icons: one of them, never both.</summary>
    void UpdateMonitorBadge()
    {
        var unread = MonitorService.TotalUnread;
        MonitorChip.Count = unread;
        MonitorChip.Visibility = unread > 0 && Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        MonitorBadge.Value = unread;
        MonitorBadge.Visibility = unread > 0 && !Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- the server check ----

    /// <summary>The periodic server check. Runs while the overview is open.</summary>
    void ArmMonitor()
    {
        var minutes = Session.Settings.RemoteCheckMinutes;
        _monitor.Stop();
        if (minutes <= 0) return;
        _monitor.Interval = TimeSpan.FromMinutes(minutes);
        _monitor.IsRepeating = true;
        _monitor.Start();
    }

    async Task MonitorTickAsync()
    {
        ArmMonitor();
        if (Session.Root == null || _checking) return;
        var before = Remote.ToDictionary(kv => kv.Key, kv => kv.Value.Commits, StringComparer.OrdinalIgnoreCase);
        await CheckRemotesAsync(Session.Root, _generation);
        foreach (var (name, result) in Remote)
            if (result.Behind && before.GetValueOrDefault(name) != result.Commits)
            {
                Pane.Append($"{DateTime.Now:HH:mm}  server: {result.Commits} new commit(s) for {name}. Sync when ready.");
                var parts = string.Join(", ", result.Entries.Where(x => x.Behind).Select(x => $"{(x.Rel.Length == 0 ? "root" : x.Rel)} r{x.Snapshot}→r{x.Server}"));
                Notifications.Show($"{result.Commits} new SVN commit(s) for {name}", parts + ". Sync when ready.");
            }
        var root = Session.Root;
        var status = await Runner.Quiet(Pane, () => Ops.Status(root, checkSvn: false));
        if (status != null && _current != null && status.Worktrees.Any(w => w.Base == _current.Name))
        {
            _status = status;
            if (Overview.IsLoaded) Overview.Show(_current, _status);
        }
    }

    /// <summary>
    /// Asks the server about every checkout at once and paints each badge as its answer arrives.
    /// The svn calls wait on the network and on the checkout's own database, so running them one
    /// after another only added the waits up: three checkouts took three times as long to settle.
    /// </summary>
    async Task CheckRemotesAsync(SgRoot root, int generation)
    {
        if (_checking) { _recheckWanted = true; return; }
        _checking = true;
        // Enough to overlap the waits, few enough that the server is not hammered. Not disposed:
        // a generation change leaves this method before the jobs it started have released it.
        var gate = new SemaphoreSlim(4);
        try
        {
            var jobs = root.Config.Checkouts.ToList().Select(co => (
                Name: co.Name,
                Remote: Gated(gate, () =>
                {
                    try { return (Result: (RemoteCheckResult?)Ops.RemoteCheck(root, co), Error: (string?)null); }
                    catch (SgException ex) { return (null, ex.Message); }
                }),
                Edits: Gated(gate, () =>
                {
                    try { return (int?)Ops.LocalEditCount(root, co); }
                    catch (SgException) { return null; }
                }))).ToList();

            foreach (var job in jobs)
            {
                var (result, error) = await job.Remote;
                if (generation != _generation) return;
                if (result != null) { Remote[job.Name] = result; RemoteErrors.Remove(job.Name); }
                else if (error != null) RemoteErrors[job.Name] = error;
                var row = Nav.MenuItems.OfType<NavigationViewItem>().Select(i => i.Tag as CheckoutRow).FirstOrDefault(r => r?.Name == job.Name);
                if (row != null) ApplyRemoteBadge(row, result);
                if (_current?.Name == job.Name) Overview.ShowRemote(result, error);

                var edits = await job.Edits;
                if (generation != _generation) return;
                if (edits.HasValue) LocalEdits[job.Name] = edits.Value;
                if (row != null) ApplyLocalBadge(row, edits);
                if (_current?.Name == job.Name) Overview.ShowLocalEdits(edits);
            }
        }
        finally
        {
            _checking = false;
            // Against the root that is open now, and the generation that is current now: the one this call
            // captured may belong to a root the user has since closed.
            if (_recheckWanted)
            {
                _recheckWanted = false;
                if (Session.Root is { } current) _ = CheckRemotesAsync(current, _generation);
            }
        }
    }

    /// <summary>Runs the work on a pool thread, with no more than the gate allows running at once.</summary>
    static Task<T> Gated<T>(SemaphoreSlim gate, Func<T> work) => Task.Run(async () =>
    {
        await gate.WaitAsync();
        try { return work(); }
        finally { gate.Release(); }
    });

    static void ApplyLocalBadge(CheckoutRow row, int? edits)
    {
        if (row.LocalBadge == null) return;
        row.LocalBadge.Count = edits ?? 0;
        row.LocalBadge.Visibility = edits > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static void ApplyRemoteBadge(CheckoutRow row, RemoteCheckResult? result)
    {
        if (row.RemoteBadge == null) return;
        var behind = result is { Behind: true };
        row.RemoteBadge.Count = behind ? result!.Commits : 0;
        row.RemoteBadge.Visibility = behind ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- reading the root ----

    /// <summary>The Refresh button beside the path. F5 is the same thing without the ring on the button.</summary>
    async void RefreshAll_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => RefreshAllAsync());

    /// <summary>F5 and the Refresh button: forget what the server said and read everything again.</summary>
    internal Task RefreshAllAsync()
    {
        Remote.Clear();
        RemoteErrors.Clear();
        return RefreshAsync();
    }

    /// <summary>The bar along the top runs while the state is read; the first read of a root shows bars where the cards will be.</summary>
    internal async Task RefreshAsync(bool runStartAction = false)
    {
        var first = _status == null && Session.Root != null;
        if (first) Overview.ShowSkeleton(true);
        Overview.SetBusy(true);
        try { await RefreshCoreAsync(runStartAction); }
        finally
        {
            Overview.SetBusy(false);
            if (first) Overview.ShowSkeleton(false);
        }
    }

    async Task RefreshCoreAsync(bool runStartAction)
    {
        var generation = ++_generation;
        AppTitleBar.Subtitle = Session.Root?.RootPath ?? "no root open";
        var checkouts = Session.Root?.Config.Checkouts.Count ?? 0;
        // No root: the pane itself says so and offers the two ways in; the dots would only repeat them.
        PaneEmpty.Visibility = Session.Root == null ? Visibility.Visible : Visibility.Collapsed;
        RootMenu.Visibility = Session.Root == null ? Visibility.Collapsed : Visibility.Visible;
        AddCheckoutButton.IsEnabled = Session.Root != null;
        ToolTipService.SetToolTip(AddCheckoutTip, Session.Root != null
            ? "Add checkout. A working copy already on disk, or one checked out from a URL first, joins this root and the list here."
            : "Add checkout needs a root open. Use Open root, or New root.");
        ServerCheckoutButton.IsEnabled = checkouts > 0;
        ToolTipService.SetToolTip(ServerCheckoutTip, checkouts > 0
            ? "New server checkout. Copies the nearest checkout on disk and runs svn switch, so only differences download."
            : "New server checkout needs one checkout to copy from. Use New root, or register one with 'sg checkout add'.");
        if (Session.Root == null)
        {
            Nav.MenuItems.Clear();
            _current = null;
            _status = null;
            ShowOverview((CheckoutRow?)null);
            return;
        }
        var root = Session.Root;
        _status = await Runner.Quiet(Pane, () => Ops.Status(root, checkSvn: false));
        if (_status == null || generation != _generation) return;

        var wanted = _current?.Name
                     ?? (_startPath != null ? root.CheckoutContaining(_startPath)?.Name : null)
                     ?? (_startPath != null ? BaseOfWorktree(_status, _startPath) : null);
        // The pane is rebuilt only when the checkouts themselves changed. A plain refresh finds the same
        // ones, and clearing the pane there would build every row again, replay its animation and drop the
        // selection on the way.
        var shownRows = Nav.MenuItems.OfType<NavigationViewItem>()
            .Select(i => (Item: i, Row: i.Tag as CheckoutRow)).Where(p => p.Row != null).ToList();
        if (shownRows.Count == _status.Checkouts.Count
            && shownRows.Zip(_status.Checkouts).All(p => p.First.Row!.Name == p.Second.Name && p.First.Row.Path == p.Second.Path))
        {
            foreach (var (_, row) in shownRows)
            {
                var c = _status.Checkouts.First(x => x.Name == row!.Name);
                row!.Config = root.Checkout(c.Name);
                row.Detail = $"{c.Url}\n{c.Path}";
            }
            var keep = shownRows.FirstOrDefault(p => p.Row!.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).Item
                       ?? shownRows[0].Item;
            _current = keep.Tag as CheckoutRow;
            // The page on screen may be a commit or a push; the overview behind it takes the new state quietly.
            if (Host.Current is CheckoutPage) ShowOverview(_current);
            _ = CheckRemotesAsync(root, generation);
            if (runStartAction && _startAction != null) await RunStartAction();
            return;
        }
        _restoringSelection = true;
        Nav.MenuItems.Clear();
        CheckoutRow? select = null;
        foreach (var c in _status.Checkouts)
        {
            var row = new CheckoutRow
            {
                Name = c.Name,
                Path = c.Path,
                Config = root.Checkout(c.Name),
                Detail = $"{c.Url}\n{c.Path}",
            };
            // The pane has no room for words, so the chips here are glyph and count only.
            row.RemoteBadge = new StatusChip { Severity = ChipSeverity.Caution, Glyph = "", Visibility = Visibility.Collapsed };
            row.LocalBadge = new StatusChip { Severity = ChipSeverity.Attention, Glyph = "", Visibility = Visibility.Collapsed };
            ToolTipService.SetToolTip(row.RemoteBadge, "Commits on the SVN server that the snapshot does not have yet. Sync brings them in.");
            ToolTipService.SetToolTip(row.LocalBadge, "Files edited directly in the SVN checkout, not committed yet. 'Changes in the checkout' shows them.");
            // A long checkout name gives way to the chips, never the other way round.
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = c.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(row.RemoteBadge, 1);
            Grid.SetColumn(row.LocalBadge, 2);
            content.Children.Add(name);
            content.Children.Add(row.RemoteBadge);
            content.Children.Add(row.LocalBadge);
            var item = new NavigationViewItem
            {
                Content = content,
                Tag = row,
                Icon = new FontIcon { Glyph = "" },
                ContextFlyout = CheckoutMenu(row),
            };
            ApplyRemoteBadge(row, Remote.GetValueOrDefault(c.Name));
            ApplyLocalBadge(row, LocalEdits.TryGetValue(c.Name, out var known) ? known : null);
            Nav.MenuItems.Add(item);
            if (select == null || c.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) select = row;
        }
        _restoringSelection = false;
        _current = select;
        // A fresh set of checkouts: a page about one of them belonged to the old set. The monitor and
        // the settings are about neither, and the first read of a root lands after a launch that asked
        // for one of them has already opened it: putting the overview back over it lost the request.
        if (Host.Current is not MonitorPage and not SettingsPage) ShowOverview(select);

        _ = CheckRemotesAsync(root, generation);
        if (runStartAction && _startAction != null) await RunStartAction();
    }

    /// <summary>
    /// The right click menu on a checkout in the pane. Everything here already had a row on the
    /// card; the menu is for reaching one without selecting the checkout first.
    /// </summary>
    MenuFlyout CheckoutMenu(CheckoutRow row)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Edit checkout", "", () => EditCheckout(row),
            "Its name, the folders sg leaves alone, and where each external points."));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Sync", "", () => SyncOrPreview(row.Config),
            "svn update the checkout, then take a new snapshot."));
        menu.Items.Add(Item("Changes in the checkout", "", () => ShowSvnChanges(row),
            "Edits made directly in the checkout: diffs, discard, or commit them straight to SVN."));
        menu.Items.Add(Item("SVN log", "", () => ShowSvnLog(row),
            "The SVN history of the checkout root or any external."));
        menu.Items.Add(Item("Merge from another branch", "", () => ShowMerge(row),
            "Take changes from another branch of the same repository, all of them or a few revisions."));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Open folder", "", () => Session.OpenInExplorer(row.Path),
            "Open the checkout folder in Explorer."));
        return menu;
    }

    static MenuFlyoutItem Item(string text, string glyph, Action run, string tip)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        ToolTipService.SetToolTip(item, tip);
        item.Click += (_, _) => run();
        return item;
    }

    /// <summary>
    /// Which checkout the folder an Explorer launch named belongs to. Read out of the status that was just
    /// fetched off the UI thread, not asked of git again: this runs on the way back from that read, on the
    /// launch path, and it used to start "git worktree list" and "git config --get" with the window frozen
    /// behind them. WorktreeStatus.Base is that same branch.&lt;name&gt;.sgBase.
    /// </summary>
    static string? BaseOfWorktree(StatusResult? status, string path)
    {
        if (status == null) return null;
        var p = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        var wt = status.Worktrees
            .Where(w => w.Path.Length > 0)
            .Select(w => (Wt: w, Root: System.IO.Path.GetFullPath(w.Path).TrimEnd('\\', '/')))
            .Where(x => p.Equals(x.Root, StringComparison.OrdinalIgnoreCase)
                        || p.StartsWith(x.Root + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Root.Length)
            .Select(x => x.Wt)
            .FirstOrDefault();
        return string.IsNullOrEmpty(wt?.Base) ? null : wt.Base;
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringSelection || _syncingPane) return;
        if (args.IsSettingsSelected) { ShowSettings(); return; }
        if (ReferenceEquals(args.SelectedItem, MonitorItem)) { ShowMonitor(null); return; }
        if ((args.SelectedItem as NavigationViewItem)?.Tag is not CheckoutRow row) return;
        _current = row;
        ShowOverview(row);
    }

    async Task RunStartAction()
    {
        var root = Session.Require();
        var path = _startPath ?? Environment.CurrentDirectory;
        switch (_startAction)
        {
            case "sync":
                if (_current != null) SyncOrPreview(_current.Config);
                break;
            case "branch":
                await NewBranchAsync(_current?.Config);
                break;
            case "rebase":
            {
                var wt = Session.WorktreeAt(path);
                if (wt != null) await Runner.Run(Pane, "rebase", () => Ops.Rebase(root, wt.Path));
                else Pane.Append(path + " is not inside a worktree");
                await RefreshAsync();
                break;
            }
            case "server-checkout":
                await ServerCheckoutAsync(_current?.Config);
                break;
            case "push" or "commit" or "log":
                Pane.Append(path + " is not inside a worktree, so there is nothing to " + _startAction);
                break;
        }
    }

    // ---- pages a checkout leads to ----

    /// <summary>Opens a page under the current checkout. The overview reads the root again when the page is left.</summary>
    void GoUnder(CheckoutRow? row, Func<SgPage> make, string key, Action? left = null) => Host.Go(() =>
    {
        var p = make();
        p.Checkout ??= row?.Name;
        if (left != null) p.Left += left;
        return p;
    }, key);

    internal void ShowSvnChanges(CheckoutRow row) =>
        GoUnder(row, () => new SvnCommitPage(row.Config), "changes:" + row.Name, () => LocalEdits.Remove(row.Name));

    internal void ShowSvnLog(CheckoutRow row) =>
        GoUnder(row, () => new SvnLogPage(row.Config), "svnlog:" + row.Name);

    internal void ShowMerge(CheckoutRow row) =>
        GoUnder(row, () => new MergePage(row.Config), "merge:" + row.Name);

    /// <summary>What a sync would bring in. The page hands the sync back here when the user says go.</summary>
    internal void ShowIncoming(CheckoutConfig co)
    {
        SvnLogPage? page = null;
        GoUnder(RowOf(co), () => page = new SvnLogPage(co, incoming: true), "incoming:" + co.Name,
            () => { if (page is { SyncRequested: true }) _ = SyncAsync(co); });
    }

    internal void ShowServerBranch(CheckoutConfig? co)
    {
        if (Session.Root == null) return;
        GoUnder(co == null ? null : RowOf(co), () => new ServerBranchPage(co), "server-branch");
    }

    internal void EditCheckout(CheckoutRow row)
    {
        EditCheckoutPage? page = null;
        GoUnder(row, () => page = new EditCheckoutPage(row.Config), "edit:" + row.Name,
            () => { if (page is { Changed: true }) { Remote.Clear(); RemoteErrors.Clear(); } });
    }

    CheckoutRow? RowOf(CheckoutConfig co) =>
        Nav.MenuItems.OfType<NavigationViewItem>().Select(i => i.Tag as CheckoutRow).FirstOrDefault(r => r?.Name == co.Name);

    // ---- pane and checkout actions ----

    async void OpenRoot_Click(object sender, RoutedEventArgs e) => await OpenRootAsync();

    /// <summary>Pick a folder that holds .sg and read it. The pane, the dots and the empty page all lead here.</summary>
    internal async Task OpenRootAsync()
    {
        var path = await WindowHelper.PickFolder(this);
        if (path == null) return;
        if (!Session.Open(path)) Pane.Append("no sg root found at or above " + path);
        _current = null;
        Remote.Clear();
        await RefreshAsync();
    }

    void AddCheckout_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Root == null) return;
        Host.Go(() => new AddCheckoutPage(), "add-checkout");
    }

    void NewRoot_Click(object sender, RoutedEventArgs e) => NewRoot();

    internal void NewRoot() => Host.Go(() =>
    {
        var p = new NewRootPage();
        p.Left += () => { _current = null; Remote.Clear(); RemoteErrors.Clear(); };
        return p;
    }, "new-root");

    /// <summary>
    /// Sync shows its work first: the incoming page lists what it would bring in and reads the diffs, and
    /// the sync starts when the user says go there. Only a server check that came back saying the checkout
    /// has everything skips the page. Not knowing yet is not the same as nothing waiting, so it looks.
    /// </summary>
    internal void SyncOrPreview(CheckoutConfig co, object? busySender = null)
    {
        if (Remote.GetValueOrDefault(co.Name) is { Behind: false })
        {
            if (busySender != null) _ = Busy.During(busySender, () => SyncAsync(co));
            else _ = SyncAsync(co);
            return;
        }
        ShowIncoming(co);
    }

    async void ServerCheckout_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => ServerCheckoutAsync(_current?.Config));

    async Task SyncAsync(CheckoutConfig co)
    {
        var root = Session.Require();
        // Pane is the strip inside the overview, and the pane's own menu can start this from any page:
        // every line of a sync begun from a commit window went to a strip that was not on screen.
        //
        // Enqueued, never called straight. One caller is the Incoming changes page's own Left event, which
        // NavHost raises in the middle of Show, before it has finished swapping the page: navigating from
        // there walked the host's list out from under the navigation that was already running, and the app
        // went with it. By the time this runs that navigation has finished, and Host.Current is the truth.
        DispatcherQueue.TryEnqueue(() => { if (Host.Current is not CheckoutPage) ShowOverview(RowOf(co)); });
        var r = await Runner.Run(Pane, "sync " + co.Name, () => Ops.Sync(root, co));
        if (r != null)
        {
            var line = $"{r.Checkout}: r{r.Revision}, {(r.Changed ? "new snapshot" : "no change")}"
                       + (r.Overlaid > 0 ? $", {r.Overlaid} local edit(s) left out" : "")
                       + (r.Conflicts > 0 ? $", {r.Conflicts} svn conflict(s) in the checkout" : "")
                       + (r.KeptSwitched.Count > 0 ? $", kept {string.Join(", ", r.KeptSwitched)} switched" : "");
            Pane.Append(line);
            if (!WindowHelper.IsForeground(this)) Notifications.Show("Sync done", line);
        }
        else if (!WindowHelper.IsForeground(this)) Notifications.Show("Sync failed", co.Name + ": see the log in sg.");
        Remote.Remove(co.Name);
        RemoteErrors.Remove(co.Name);
        await RefreshAsync();
    }

    internal async Task NewBranchAsync(CheckoutConfig? preselect)
    {
        if (Session.Root == null) return;
        var root = Session.Root;
        var input = await Dialogs.NewBranch(this, root, preselect);
        if (input == null) return;
        var r = await Runner.Run(Pane, "new branch " + input.Name,
            () => Ops.Branch(root, input.Name, input.Checkout, input.Without, input.Minimal, input.Shared));
        if (r != null)
        {
            Pane.Append($"worktree: {r.Path}");
            if (r.Shared.Count > 0) Pane.Append(SharedFolders.Describe(r.SharedMode) + ": " + string.Join(", ", r.Shared));
        }
        await RefreshAsync();
    }

    async Task ServerCheckoutAsync(CheckoutConfig? preselect)
    {
        var root = Session.Root;
        if (root == null || root.Config.Checkouts.Count == 0) return;   // the button is disabled in that case
        var input = await Dialogs.ServerCheckout(this, root, preselect);
        if (input == null) return;
        var r = await Runner.Run(Pane, "server checkout " + input.Target, () => Server.Checkout(root, input.Near, input.Target, input.Name));
        if (r != null) Pane.Append($"checkout {r.Checkout.Name}: {r.Checkout.Path}, r{r.Snapshot.Revision}");
        await RefreshAsync();
    }
}
