using Microsoft.Win32;

namespace Sg.App;

/// <summary>The classic Explorer context menu: a cascading "sg" menu on folders and on folder backgrounds. Registry only, current user only.</summary>
public static class Shell
{
    static readonly (string Key, string Label, string Action)[] Items =
    {
        ("01overview", "Overview", "overview"),
        ("02sync", "Sync", "sync"),
        ("03branch", "New worktree...", "branch"),
        ("04commit", "Commit... (git in a worktree, SVN in a checkout)", "commit"),
        ("05shelf", "Shelved changes", "shelf"),
        ("06log", "Log (git in a worktree, SVN in a checkout)", "log"),
        ("07rebase", "Rebase", "rebase"),
        ("08push", "Push to SVN...", "push"),
        ("09serverbranch", "New server branch...", "server-branch"),
        ("10servercheckout", "New server checkout...", "server-checkout"),
        ("11monitor", "Project monitor", "monitor"),
        ("12settings", "Settings", "settings"),
    };

    const string FolderKey = @"Software\Classes\Directory\shell\sg";
    const string BackgroundKey = @"Software\Classes\Directory\Background\shell\sg";

    public static void Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        // The whole menu is written again, not added to. The keys carry their order in their names, so a
        // version that puts a new entry in the middle renumbers the ones under it, and the old numbers
        // would otherwise stay behind as a second copy of every entry below the new one.
        Uninstall();
        foreach (var (baseKey, arg) in new[] { (FolderKey, "%1"), (BackgroundKey, "%V") })
        {
            using var k = Registry.CurrentUser.CreateSubKey(baseKey);
            k.SetValue("MUIVerb", "sg");
            k.SetValue("Icon", exe + ",0");
            k.SetValue("SubCommands", "");
            using var shell = k.CreateSubKey("shell");
            foreach (var (key, label, action) in Items)
            {
                using var item = shell.CreateSubKey(key);
                item.SetValue("MUIVerb", label);
                using var cmd = item.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" {action} \"{arg}\"");
            }
        }
    }

    public static void Uninstall()
    {
        Registry.CurrentUser.DeleteSubKeyTree(FolderKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(BackgroundKey, false);
    }

    public static bool IsInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(FolderKey);
        return k != null;
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "sg-ui";

    /// <summary>Start hidden in the tray when the user logs in. Current user only.</summary>
    public static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(RunValue, false);
    }

    public static bool IsStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is string;
    }

    public static string? InstalledExe()
    {
        using var k = Registry.CurrentUser.OpenSubKey(FolderKey + @"\shell\01overview\command");
        return ExeFrom(k);
    }

    static string? ExeFrom(RegistryKey? k)
    {
        var v = k?.GetValue("") as string;
        if (v == null) return null;
        var end = v.IndexOf('"', 1);
        return end > 1 ? v[1..end] : v;
    }
}
