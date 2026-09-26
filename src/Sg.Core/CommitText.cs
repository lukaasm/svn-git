using System.Globalization;
using System.Text;

namespace Sg.Core;

/// <summary>
/// What `git log -1 --format=%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%B` says of a commit, read from
/// the commit's own bytes: the author and committer lines, their dates in git's strict ISO form (Z
/// for a zone of +0000, else ±HH:MM), and the message as it is kept, with the newline the format puts
/// after it. Null when git has to say it: a commit in another encoding, which git turns into UTF-8, or
/// a line that is not what git writes.
/// </summary>
static class CommitText
{
    static readonly UTF8Encoding Utf8 = new(false);

    public static CommitIdentity? Identity(byte[] raw)
    {
        var text = Utf8.GetString(raw);
        var end = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = end < 0 ? text : text[..end];
        var message = end < 0 ? "" : text[(end + 2)..];
        (string Name, string Email, string Date)? author = null, committer = null;
        foreach (var line in headers.Split('\n'))
        {
            if (line.StartsWith("encoding ", StringComparison.Ordinal)) return null;
            if (line.StartsWith("author ", StringComparison.Ordinal)) author = Ident(line[7..]);
            else if (line.StartsWith("committer ", StringComparison.Ordinal)) committer = Ident(line[10..]);
        }
        if (author is not { } a || committer is not { } c) return null;
        return new CommitIdentity(a.Name, a.Email, a.Date, c.Name, c.Email, c.Date, message + "\n");
    }

    /// <summary>"Name &lt;email&gt; seconds zone" as %an, %ae and %aI give them back.</summary>
    static (string Name, string Email, string Date)? Ident(string line)
    {
        var lt = line.IndexOf(" <", StringComparison.Ordinal);
        var gt = lt < 0 ? -1 : line.IndexOf('>', lt + 2);
        if (lt < 0 || gt < 0) return null;
        var when = line[(gt + 1)..].Trim().Split(' ');
        if (when.Length != 2 || !long.TryParse(when[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || when[1].Length != 5 || when[1][0] is not ('+' or '-')
            || !int.TryParse(when[1][1..3], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(when[1][3..], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            return null;
        var zone = new TimeSpan(h, m, 0);
        if (when[1][0] == '-') zone = -zone;
        var local = DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(zone);
        var date = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            + (zone == TimeSpan.Zero ? "Z" : (zone < TimeSpan.Zero ? "-" : "+") + $"{Math.Abs(zone.Hours):00}:{Math.Abs(zone.Minutes):00}");
        return (line[..lt], line[(lt + 2)..gt], date);
    }
}
