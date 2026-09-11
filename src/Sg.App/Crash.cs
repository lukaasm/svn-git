using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>
/// What the app does instead of disappearing. WinUI ends the process on any exception that reaches the
/// top of the UI thread, which an async void handler does by design: the window, the log pane and every
/// line of what just happened go with it, and the person it happened to has nothing to report but "it
/// closed". This keeps the app up where it can, writes the trace to a file beside the settings, and puts
/// it on screen with a button that copies it.
/// </summary>
public static class Crash
{
    static Window? _owner;
    static bool _showing;

    /// <summary>Where the traces go. One file, appended to, so a run that crashed twice keeps both.</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sg", "crash.log");

    /// <summary>
    /// Every way an exception can arrive uncaught: off the UI thread's dispatcher, off a background
    /// thread, and out of a Task nobody awaited. The first is the one that matters and the only one
    /// that can be swallowed; the other two are recorded and left to do what they were going to do.
    /// </summary>
    public static void Install(Application app)
    {
        app.UnhandledException += (_, e) =>
        {
            // Handled, so the app stays up. The state it is in is whatever the failed handler left, which
            // is worth keeping: the alternative is closing on the user with their message half typed.
            e.Handled = true;
            Report(e.Exception, "on the UI thread");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write(Describe(ex, "on a background thread"));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Write(Describe(e.Exception, "in a task nobody waited for"));
        };
    }

    /// <summary>The window a crash dialog belongs to. The main window sets it; a page window may replace it.</summary>
    public static void Owner(Window window) => _owner = window;

    public static void Report(Exception ex, string where)
    {
        var text = Describe(ex, where);
        Write(text);
        try { Session.Log.Cmd("crash: " + FirstLine(ex)); }
        catch (Exception) { /* the log itself is gone; the file is the record */ }
        Show(ex, text);
    }

    /// <summary>
    /// The trace, and enough around it to make sense of it a week later: what sg was doing, which build,
    /// and the whole chain of inner exceptions rather than only the outermost one.
    /// </summary>
    static string Describe(Exception ex, string where)
    {
        var sb = new StringBuilder();
        sb.Append("=== ").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")).Append("  ").Append(where).Append(" ===\n");
        sb.Append("sg-ui ").Append(typeof(Crash).Assembly.GetName().Version?.ToString() ?? "?")
          .Append("   ").Append(Environment.OSVersion.VersionString)
          .Append("   .NET ").Append(Environment.Version).Append('\n');
        try
        {
            if (Session.Root is { } root) sb.Append("root ").Append(root.RootPath).Append('\n');
        }
        catch (Exception) { /* a crash while reading the session must not become the crash that is reported */ }
        for (var e = ex; e != null; e = e.InnerException)
        {
            sb.Append(e.GetType().FullName).Append(": ").Append(e.Message).Append('\n');
            if (e.StackTrace != null) sb.Append(e.StackTrace).Append('\n');
            if (e.InnerException != null) sb.Append("--- caused by ---\n");
        }
        sb.Append('\n');
        return sb.ToString();
    }

    static string FirstLine(Exception ex) => ex.GetType().Name + ": " + ex.Message.Split('\n')[0];

    static void Write(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, text, new UTF8Encoding(false));
        }
        catch (Exception) { /* nowhere to write it is not worth a second crash */ }
    }

    /// <summary>
    /// One dialog at a time. A crash inside the crash dialog would otherwise stack them until the app
    /// really does die, and the second one is never the interesting one.
    /// </summary>
    static async void Show(Exception ex, string text)
    {
        if (_showing) return;
        var root = _owner?.Content?.XamlRoot;
        if (root == null) return;
        _showing = true;
        try
        {
            var box = new TextBox
            {
                Text = text.TrimEnd(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                MaxHeight = 320,
                MinWidth = 620,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "sg hit something it did not expect. It is still running, but what it was doing did not finish. "
                       + "Copy this and it can be fixed; it is also appended to\n" + LogPath,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(box);

            var d = new ContentDialog
            {
                XamlRoot = root,
                Title = FirstLine(ex),
                Content = panel,
                PrimaryButtonText = "Copy",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await d.ShowAsync() == ContentDialogResult.Primary)
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(text);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
        }
        catch (Exception) { /* the file already has it */ }
        finally { _showing = false; }
    }
}
