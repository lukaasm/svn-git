using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>
/// The app's settings and the root's bridge settings, one card each. A control writes its setting
/// the moment it changes, the way Windows Settings does; the Save button went with the window.
/// </summary>
public sealed partial class SettingsPage : SgPage
{
    /// <summary>The controls are being filled from the settings, so their change events are not the user's.</summary>
    bool _filling = true;

    public SettingsPage()
    {
        InitializeComponent();
        Title = "Settings";
        var root = Session.Root;
        Subtitle = root?.RootPath ?? "no root open";
        RootText.Text = root != null ? "Stored in " + root.ConfigPath : "No root open. Bridge settings need one.";
        MinLength.Value = root?.Config.MinMessageLength ?? 10;
        AgentPush.IsOn = root?.Config.AllowAgentPush ?? false;
        MinLength.IsEnabled = AgentPush.IsEnabled = root != null;
        BackupUrl.Text = root?.Config.Backup?.Url ?? "";
        BackupPrefix.Text = root?.Config.Backup?.Prefix ?? "";
        BackupUncommitted.IsOn = root?.Config.Backup?.Uncommitted ?? true;
        BackupUrl.IsEnabled = BackupPrefix.IsEnabled = BackupUncommitted.IsEnabled = BackupCheck.IsEnabled = root != null;
        BackupMinutes.Value = Session.Settings.BackupMinutes;
        ThemeBox.ItemsSource = Themes.Names.ToList();
        ThemeBox.SelectedItem = Themes.Current.Name;
        MonacoUrl.Text = Session.Settings.MonacoUrl;
        Verbose.IsOn = Session.Settings.Verbose;
        RemoteMinutes.Value = Session.Settings.RemoteCheckMinutes;
        UpdateMinutes.Value = Session.Settings.UpdateCheckMinutes;
        EditorCommand.Text = Session.Settings.EditorCommand;
        Notify.IsOn = Session.Settings.Notify;
        Tray.IsOn = Session.Settings.Tray;
        Startup.IsOn = Shell.IsStartup();
        ShowMenuState();
        ShowNotifyState();
        ShowBuild(null);
        _filling = false;
        // One write per pause in the typing. Save writes both files whole, so writing it per letter is
        // what the debounce is for; waiting for the box to lose focus is what lost the text entirely.
        _typing.Tick += (_, _) => { _typing.Stop(); Save(); };
        MonacoUrl.TextChanged += (_, _) => Type();
        EditorCommand.TextChanged += (_, _) => Type();
        BackupUrl.TextChanged += (_, _) => Type();
        BackupPrefix.TextChanged += (_, _) => Type();
    }

    /// <summary>
    /// The build installed, read off build.json next to sg.exe, and what GitHub said when asked. A
    /// debug build run from its own folder shows the installed one: that is the build.json there is.
    /// </summary>
    void ShowBuild(Sg.Core.UpdateCheck? check)
    {
        var dir = Updates.InstallDir();
        var local = Sg.Core.Updater.ReadStamp(dir);
        var text = local != null
            ? $"{local}, in {dir}."
            : $"No build.json next to sg.exe in {dir}: a build made by hand, or one older than the stamp.";
        if (check != null)
            text += check.Newer
                ? $" GitHub has {check.Remote}, which is newer: Update and restart is in the pane."
                : $" GitHub has {check.Remote}. This is the newest.";
        BuildCard.Description = text;
    }

    async void CheckBuild_Click(object sender, RoutedEventArgs e)
    {
        var check = await Busy.During(sender, Updates.CheckAsync);
        if (check == null)
        {
            ShowBuild(null);
            BuildCard.Description += " GitHub could not be asked; the log says why.";
            return;
        }
        ShowBuild(check);
    }

    readonly Microsoft.UI.Xaml.DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(400) };

    void Type()
    {
        if (_filling) return;
        _typing.Stop();
        _typing.Start();
    }

    void ShowNotifyState() => NotifyState.Description = Notifications.State();

    /// <summary>
    /// Themes.Use writes the setting itself, because it also has to repaint the brushes and every diff
    /// on screen. Save is not called here: it would write the box's old value back over it.
    /// </summary>
    void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || ThemeBox.SelectedItem is not string name) return;
        Themes.Use(name);
    }

    /// <summary>
    /// Proves the whole path in one press: Windows takes the toast, and pressing that toast opens the
    /// project monitor, exactly as a real one does. Waiting for someone to commit was the only other way.
    /// </summary>
    async void TestNotify_Click(object sender, RoutedEventArgs e)
    {
        TestNotify.IsEnabled = false;
        try { Status.Text = await Notifications.SendTestAsync(); }
        finally { TestNotify.IsEnabled = true; }
        ShowNotifyState();
    }

    void ShowMenuState()
    {
        if (!Shell.IsInstalled()) { MenuState.Description = "Not installed."; return; }
        var exe = Shell.InstalledExe();
        MenuState.Description = "Installed, pointing at " + exe
                                + (exe != null && !exe.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ? "\nThis exe is elsewhere: " + Environment.ProcessPath + ". Install again to update." : "");
    }

    void InstallMenu_Click(object sender, RoutedEventArgs e)
    {
        try { Shell.Install(); Status.Text = "menu installed"; }
        catch (Exception ex) { Status.Text = "failed: " + ex.Message; }
        ShowMenuState();
    }

    void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        try { Shell.Uninstall(); Status.Text = "menu removed"; }
        catch (Exception ex) { Status.Text = "failed: " + ex.Message; }
        ShowMenuState();
    }

    /// <summary>Asks the URL whether a git repository answers there, now rather than on the timer.</summary>
    async void BackupCheck_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Root;
        var url = BackupUrl.Text.Trim();
        if (root == null || url.Length == 0) { Status.Text = "give a URL first"; return; }
        BackupCheck.IsEnabled = false;
        try
        {
            var refs = await Task.Run(() => root.Git.LsRemote(url));
            Status.Text = $"a git repository answers at {url}: {refs.Count} ref(s) there";
        }
        catch (Sg.Core.SgException ex) { Status.Text = ex.Message; }
        finally { BackupCheck.IsEnabled = true; }
    }

    void Changed(object sender, RoutedEventArgs e) => Save();

    void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => Save();

    /// <summary>Writes everything, every time: the two files are small and one write is one place to get right.</summary>
    void Save()
    {
        if (_filling) return;
        _typing.Stop();
        var root = Session.Root;
        if (root != null)
        {
            root.Config.MinMessageLength = double.IsNaN(MinLength.Value) ? 10 : (int)MinLength.Value;
            root.Config.AllowAgentPush = AgentPush.IsOn;
            var url = BackupUrl.Text.Trim();
            root.Config.Backup = url.Length == 0 ? null
                : new Sg.Core.BackupConfig { Url = url, Prefix = BackupPrefix.Text.Trim().Trim('/'), Uncommitted = BackupUncommitted.IsOn };
            root.Save();
        }
        Session.Settings.MonacoUrl = MonacoUrl.Text.Trim().Length > 0 ? MonacoUrl.Text.Trim() : new AppSettings().MonacoUrl;
        Session.Settings.Verbose = Verbose.IsOn;
        Session.Settings.RemoteCheckMinutes = double.IsNaN(RemoteMinutes.Value) ? 2 : (int)RemoteMinutes.Value;
        Session.Settings.UpdateCheckMinutes = double.IsNaN(UpdateMinutes.Value) ? 60 : (int)UpdateMinutes.Value;
        Session.Settings.BackupMinutes = double.IsNaN(BackupMinutes.Value) ? 15 : (int)BackupMinutes.Value;
        Session.Settings.EditorCommand = EditorCommand.Text.Trim();
        Session.Settings.Notify = Notify.IsOn;
        Session.Settings.Tray = Tray.IsOn;
        Session.Settings.Save();
        // Before the early return below: the registration line matters most on the path that fails.
        ShowNotifyState();
        if (Tray.IsOn) App.EnsureTray(); else App.RemoveTray();
        try { Shell.SetStartup(Startup.IsOn); }
        catch (Exception ex) { Status.Text = "startup entry failed: " + ex.Message; return; }
        Status.Text = $"saved {DateTime.Now:HH:mm:ss}";
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
