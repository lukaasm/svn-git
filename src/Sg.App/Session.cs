using System.Diagnostics;
using System.Text.Json;
using Sg.Core;

namespace Sg.App;

/// <summary>Per-user GUI settings in %LOCALAPPDATA%\sg\app.json. The bridge settings live in the root's sg.json.</summary>
public sealed class AppSettings
{
    public string? LastRoot { get; set; }
    public string MonacoUrl { get; set; } = "https://cdn.jsdelivr.net/npm/monaco-editor@0.52.2/min/vs";
    public bool Verbose { get; set; }
    /// <summary>How often the overview asks the server for new commits. 0 turns it off.</summary>
    public int RemoteCheckMinutes { get; set; } = 2;
    /// <summary>Inline diff instead of side by side.</summary>
    public bool DiffInline { get; set; }
    /// <summary>Hide the unchanged parts of a file's diff, keeping a few lines around each change.</summary>
    public bool DiffCollapsed { get; set; }
    /// <summary>Treat lines that differ only in leading or trailing space as unchanged, in the diff on screen.</summary>
    public bool DiffIgnoreWhitespace { get; set; }

    /// <summary>
    /// The palette the diff reads in, by name, and with it the colours of the status letters beside a
    /// file name. A name this build does not know falls back to sg's own rather than failing to start.
    /// </summary>
    public string DiffTheme { get; set; } = Sg.App.Themes.Default;

    /// <summary>
    /// The last commit messages that went through, newest first. The message dialog offers them again:
    /// the same sentence goes on a dozen commits in a row while one change is being carried across
    /// repositories, and every Tortoise window has kept a list like this.
    /// </summary>
    public List<string> RecentMessages { get; set; } = new();

    /// <summary>How many of them are kept. Older ones fall off the end.</summary>
    public const int MaxRecentMessages = 25;

    /// <summary>Puts a message at the top of the list, without a second copy of it further down.</summary>
    public void RememberMessage(string message)
    {
        var text = message.Trim();
        if (text.Length == 0) return;
        RecentMessages.RemoveAll(m => string.Equals(m.Trim(), text, StringComparison.Ordinal));
        RecentMessages.Insert(0, text);
        if (RecentMessages.Count > MaxRecentMessages) RecentMessages.RemoveRange(MaxRecentMessages, RecentMessages.Count - MaxRecentMessages);
        Save();
    }

    /// <summary>Toasts for new server commits and for long operations that finish in the background.</summary>
    public bool Notify { get; set; } = true;
    /// <summary>Closing the overview hides it into the tray. The app keeps running and keeps checking.</summary>
    public bool Tray { get; set; } = true;
    /// <summary>How often the app asks GitHub for a newer build. 0 turns it off.</summary>
    public int UpdateCheckMinutes { get; set; } = 60;
    /// <summary>How often the app sends what changed to the backup repository. 0 turns the timer off.</summary>
    public int BackupMinutes { get; set; } = 15;
    /// <summary>Program that opens a worktree folder, for example "code" or "cursor". Empty means the Windows default.</summary>
    public string EditorCommand { get; set; } = "";

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sg", "app.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>The one open root, its log sink, and helpers every window uses.</summary>
public static class Session
{
    public static readonly UiLog Log = new();
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static SgRoot? Root { get; private set; }

    public static bool Open(string? hint)
    {
        SgRoot? r = null;
        try { if (hint != null) r = SgRoot.Find(hint, Log); }
        catch (SgException) { }
        try
        {
            if (r == null && Settings.LastRoot != null && Directory.Exists(Settings.LastRoot)) r = SgRoot.Find(Settings.LastRoot, Log);
        }
        catch (SgException) { }
        if (r == null) return false;
        Root = r;
        Settings.LastRoot = r.RootPath;
        Settings.Save();
        return true;
    }

    public static void Reload()
    {
        if (Root != null) Root = SgRoot.Open(Root.RootPath, Log);
    }

    public static SgRoot Require() => Root ?? throw new SgException("no sg root is open. Use 'Open root' first.");

    /// <summary>The branch worktree that holds the path. Checkouts do not count.</summary>
    public static WorktreeInfo? WorktreeAt(string path)
    {
        var root = Root;
        if (root == null) return null;
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        var coPaths = root.Config.Checkouts.Select(c => c.Path.TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return root.Git.WorktreeList()
            .Where(w => !w.Bare && w.Branch != null && !coPaths.Contains(w.Path.TrimEnd('\\', '/')))
            .Where(w => p.Equals(w.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                        || p.StartsWith(w.Path.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Path.Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Opens a folder in whatever the shell opens folders with. Starting explorer.exe by name walked
    /// past a file manager the user had put in its place, so the folder is handed to the shell as a
    /// document instead and the shell picks the handler, the way a double click in a dialog does.
    /// </summary>
    public static void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* nothing to do */ }
    }

    public static bool LooksBinary(string text) => text.Contains('\0');
}

/// <summary>Runs core operations off the UI thread and routes their log into a pane.</summary>
public static class Runner
{
    /// <summary>
    /// The log sink is one field for the whole app, so an operation has to give it back. A push runs
    /// for minutes, and until this restored the previous sink, any window that finished a read in the
    /// meantime kept the sink and the push's own log went quiet with nothing said about it.
    /// </summary>
    public static async Task<T?> Run<T>(StatusStrip pane, string title, Func<T> work) where T : class
    {
        var previous = Session.Log.Sink;
        Session.Log.Sink = pane;
        pane.Begin(title);
        using var cancel = new CancellationTokenSource();
        pane.Arm(cancel);
        try
        {
            // The token rides the async flow into Proc, which ends the child process on cancel.
            using (Cancellation.Use(cancel.Token))
            {
                var result = await Task.Run(work);
                pane.End(title + ": done");
                return result;
            }
        }
        catch (SgCancelledException)
        {
            pane.End(title + ": cancelled");
            pane.Append("cancelled");
            return null;
        }
        catch (OperationCanceledException)
        {
            pane.End(title + ": cancelled");
            pane.Append("cancelled");
            return null;
        }
        catch (SgException ex)
        {
            pane.Error(ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            pane.Error(Unexpected(ex));
            return null;
        }
        finally
        {
            pane.Arm(null);
            Session.Log.Sink = previous;
        }
    }

    /// <summary>
    /// A failure sg did not plan for. The message and the kind of error are what a person can act on;
    /// a .NET stack trace in a small monospace box only buries them, so it goes to the verbose log.
    /// </summary>
    static string Unexpected(Exception ex)
    {
        var line = ex.Message.Trim();
        if (line.Length == 0) line = ex.GetType().Name;
        Session.Log.Cmd(ex.ToString());
        return line + "  (" + ex.GetType().Name + ")";
    }

    public static async Task<bool> Run(StatusStrip pane, string title, Action work)
    {
        var r = await Run(pane, title, () => { work(); return "ok"; });
        return r != null;
    }

    /// <summary>
    /// For reads that need no log line of their own. It deliberately leaves the sink alone: the
    /// overview polls through here every couple of minutes, and taking the sink would have pulled a
    /// running push's log over to the overview halfway through the push.
    /// </summary>
    public static async Task<T?> Quiet<T>(StatusStrip pane, Func<T> work) where T : class
    {
        try { return await Task.Run(work); }
        catch (SgException ex) { pane.Error(ex.Message); return null; }
        catch (Exception ex) { pane.Error(Unexpected(ex)); return null; }
    }

    /// <summary>
    /// The same, for work that is already a task: several reads fanned out with Task.WhenAll, or one that
    /// starts itself. Without it a page had the choice of awaiting the fan-out bare - and dying on the
    /// first server that refused - or giving up the fan-out.
    /// </summary>
    public static async Task<T?> Quiet<T>(StatusStrip pane, Func<Task<T>> work) where T : class
    {
        try { return await work(); }
        catch (SgException ex) { pane.Error(ex.Message); return null; }
        catch (Exception ex) { pane.Error(Unexpected(ex)); return null; }
    }
}
