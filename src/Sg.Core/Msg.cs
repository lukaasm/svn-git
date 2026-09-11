using System.Globalization;

namespace Sg.Core;

/// <summary>
/// The one line of a commit message, and the moment a revision was written, in the shapes a list row
/// can hold. Both live here because both arrive from git and svn in a shape a row cannot use: to git a
/// message whose line breaks are lone carriage returns has one subject twenty kilobytes long, and an
/// svn date is an ISO instant with microseconds and a Z on the end.
/// </summary>
public static class Msg
{
    /// <summary>
    /// The first line of a message, whatever broke it. Git's %s splits on \n alone, so a message written
    /// with lone \r line breaks — which some SVN tools write — comes back whole, and the row
    /// that draws it grows to the height of the list. This cuts at the first break of either kind.
    /// </summary>
    public static string Subject(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var end = message.AsSpan().IndexOfAny('\r', '\n');
        return (end < 0 ? message : message[..end]).Trim();
    }

    /// <summary>
    /// A message with every line break written the same way, so a details pane wraps where the author
    /// meant it to. A lone \r becomes a break; a \r\n stays one break rather than becoming two.
    /// </summary>
    public static string Body(string? message) =>
        string.IsNullOrEmpty(message) ? "" : message.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    /// <summary>
    /// When a revision was written, in local time, to the minute: "2026-09-08 18:23". SVN answers with
    /// "2026-09-08T16:23:52.195383Z", which is the same fact and unreadable in a row. A date this cannot
    /// parse comes back as it arrived: the raw string is worse to read than a date and better than a wrong one.
    /// </summary>
    public static string When(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return "";
        // Git already answers "2026-09-06" or "2026-09-06 21:05:34 +0200". Only an instant needs work.
        if (!date.Contains('T') && !date.Contains(':')) return date;
        return DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : date;
    }

    /// <summary>The day alone, for a column with room for nothing else. Git already answers this way.</summary>
    public static string Day(string? date)
    {
        var s = When(date);
        return s.Length >= 10 && s[4] == '-' ? s[..10] : s;
    }
}
