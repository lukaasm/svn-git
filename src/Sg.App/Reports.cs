namespace Sg.App;

/// <summary>
/// An operation run with a ReportCard saying so: a ring while it runs, then how it went. The strip at the
/// foot of the page still takes its lines and the log still takes all of them; the card is what stays on
/// the page, where "sync: done" used to be the whole report of a sync that left conflicts behind.
/// </summary>
public static class Reports
{
    /// <summary>Runs the work. Done, the card shows what show makes of the result; failed, the error; cancelled, nothing.</summary>
    public static async Task<T?> Run<T>(ReportCard card, StatusStrip pane, string title, Func<T> work, Action<ReportCard, T> show) where T : class
    {
        card.Running(Capital(title) + "...");
        string? error = null;
        var r = await Runner.Run(pane, title, work, m => error = m);
        if (r != null) show(card, r);
        else if (error != null) Failed(card, title, error);
        else card.Hide();
        return r;
    }

    /// <summary>The same, for work that hands nothing back.</summary>
    public static async Task<bool> Run(ReportCard card, StatusStrip pane, string title, Action work, Action<ReportCard> show)
    {
        card.Running(Capital(title) + "...");
        string? error = null;
        var ok = await Runner.Run(pane, title, work, m => error = m);
        if (ok) show(card);
        else if (error != null) Failed(card, title, error);
        else card.Hide();
        return ok;
    }

    /// <summary>The operation stopped: its first line is the sentence, the rest goes under it, and the log keeps all of it.</summary>
    public static void Failed(ReportCard card, string title, string error)
    {
        var lines = error.Split('\n', 2);
        card.Show(ChipSeverity.Critical, "", Capital(title) + " failed: " + lines[0].Trim(), lines.Length > 1 ? lines[1].Trim() : "");
    }

    public static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
