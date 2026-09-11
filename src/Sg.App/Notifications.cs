using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Sg.App;

/// <summary>
/// Windows toast notifications. An app with no package tells Windows who it is by writing a display
/// name and an icon under its own app id; registering without them leaves the app id half written,
/// and Windows then drops every toast from it without an error and without showing anything.
/// </summary>
public static class Notifications
{
    static bool _registered;

    /// <summary>True once Windows has taken the registration. Nothing is sent before that.</summary>
    public static bool IsRegistered => _registered;

    /// <summary>
    /// Why the last toast did not go out, or null when it did. Every failure here used to be
    /// swallowed, which left nothing to look at when the toasts stopped arriving.
    /// </summary>
    public static string? LastError { get; private set; }

    public static void Register(Action<IDictionary<string, string>> onInvoked)
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, a) => onInvoked(a.Arguments);
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "sg.ico");
            if (File.Exists(icon)) AppNotificationManager.Default.Register("sg", new Uri(icon));
            else AppNotificationManager.Default.Register();
            _registered = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            _registered = false;
            LastError = "Windows refused the registration: " + ex.Message;
        }
    }

    /// <summary>What the Settings window says about the state of toasts on this machine.</summary>
    public static string State()
    {
        if (!_registered) return "Not registered. " + (LastError ?? "Windows did not say why.");
        if (LastError != null) return LastError;
        return Session.Settings.Notify
            ? "Registered with Windows. Toasts go to the notification area."
            : "Registered with Windows, and the switch here is off.";
    }

    public static void Show(string title, string body, IDictionary<string, string>? args = null)
    {
        if (!_registered || !Session.Settings.Notify) return;
        Send(title, body, args);
    }

    /// <summary>
    /// One toast shaped like the monitor's own, and then a look at whether Windows actually took it.
    /// Show never fails when Windows drops a toast, so asking for the app's notifications back is the
    /// only way to tell a delivered toast from a silently discarded one. Pressing it opens the
    /// monitor, which is what a real one does. It ignores the switch: the user just asked for it.
    /// </summary>
    public static async Task<string> SendTestAsync()
    {
        if (!_registered) return "Not registered with Windows. " + (LastError ?? "Windows did not say why.");
        Send("sg notifications work",
            "This is a test. A real one names the repository and its newest commit.",
            new Dictionary<string, string> { ["action"] = "monitor" });
        if (LastError != null) return LastError;
        try
        {
            var mine = await AppNotificationManager.Default.GetAllAsync();
            return mine.Count > 0
                ? "Windows took the toast. Look in the notification area, and press it to open the monitor."
                : "Windows dropped the toast without an error. Check that notifications are on for sg in Windows Settings.";
        }
        catch (Exception ex)
        {
            return "Sent, but Windows would not say whether it kept it: " + ex.Message;
        }
    }

    static void Send(string title, string body, IDictionary<string, string>? args)
    {
        try
        {
            var b = new AppNotificationBuilder().AddText(title).AddText(body);
            if (args != null) foreach (var (k, v) in args) b.AddArgument(k, v);
            AppNotificationManager.Default.Show(b.BuildNotification());
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = "Windows refused the last toast: " + ex.Message;
        }
    }
}
