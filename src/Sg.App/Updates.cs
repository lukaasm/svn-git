using System.Diagnostics;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// The app cannot overwrite its own files while it runs. So sg.exe does the work:
/// the app starts it, closes itself, and sg.exe waits, installs, and starts the app again.
/// </summary>
public static class Updates
{
    /// <summary>%LOCALAPPDATA%\sg, the folder that holds sg.exe and the ui subfolder.</summary>
    public static string InstallDir()
    {
        var uiDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(uiDir);
        if (parent != null && File.Exists(Path.Combine(parent, "sg.exe"))) return parent;
        if (File.Exists(Path.Combine(uiDir, "sg.exe"))) return uiDir;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sg");
    }

    static string? _lastFailure;

    public static string SgExe() => Path.Combine(InstallDir(), "sg.exe");

    static string UiExe() => Path.Combine(AppContext.BaseDirectory, "sg-ui.exe");

    /// <summary>Asks GitHub for the newest build. Returns null when the check fails, for example with no network.</summary>
    public static async Task<UpdateCheck?> CheckAsync()
    {
        try { return await Task.Run(() => Updater.Check(Updater.Repo(null), InstallDir(), new NullLog())); }
        catch (Exception ex)
        {
            // A silent null reads exactly like "up to date", which hides an expired token for good.
            if (_lastFailure != ex.Message)
            {
                _lastFailure = ex.Message;
                Session.Log.Warn("update check: " + ex.Message);
            }
            return null;
        }
    }

    /// <summary>Deletes what an earlier update could not delete while the app was running.</summary>
    public static void Sweep()
    {
        try { Updater.Sweep(InstallDir()); }
        catch (Exception) { /* nothing to do */ }
    }

    /// <summary>
    /// Starts sg.exe, which waits for this process to close, installs the build, and starts the app again.
    /// The caller must close the app right after this returns true.
    /// </summary>
    public static bool StartInstaller()
    {
        var sg = SgExe();
        if (!File.Exists(sg)) throw new SgException("sg.exe is not next to the app: " + sg);

        var psi = new ProcessStartInfo(sg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),   // never hold a handle on the folder being replaced
        };
        psi.ArgumentList.Add("update");
        psi.ArgumentList.Add("--wait-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("--relaunch");
        psi.ArgumentList.Add(UiExe());
        return Process.Start(psi) != null;
    }
}
