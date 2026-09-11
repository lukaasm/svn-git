namespace Sg.Core;

/// <summary>Relative paths inside a checkout or worktree always use forward slashes and no leading "./".</summary>
public static class PathUtil
{
    /// <summary>
    /// Every path git and svn report comes through here. Replace returns the same string when there is
    /// no backslash, and the rest reads the ends rather than cutting a new string off each one.
    /// </summary>
    public static string Rel(string p)
    {
        var s = p.Replace('\\', '/');
        var start = 0;
        while (start + 1 < s.Length && s[start] == '.' && s[start + 1] == '/') start += 2;
        var end = s.Length;
        while (end > start && s[end - 1] == '/') end--;
        if (end - start == 1 && s[start] == '.') return "";
        return start == 0 && end == s.Length ? s : s[start..end];
    }

    /// <summary>Absolute path in the form git likes.</summary>
    public static string Git(string absolute) => Path.GetFullPath(absolute).Replace('\\', '/');

    public static string Join(string root, string rel) =>
        rel.Length == 0 ? root : Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True when rel equals prefix or lies below it. An empty prefix matches everything.</summary>
    public static bool IsUnder(string rel, string prefix) =>
        prefix.Length == 0
        // Grouping a push by working copy asks this of every changed path against every external,
        // so it reads the prefix off rel rather than building "prefix/" to compare against.
        || (rel.Length >= prefix.Length
            && rel.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase)
            && (rel.Length == prefix.Length || rel[prefix.Length] == '/'));

    /// <summary>
    /// CON, PRN, AUX, NUL, COM1..9 and LPT1..9, alone or with any extension. Windows refuses those names,
    /// and git refuses to hash them. A snapshot asks this of every one of a hundred thousand paths, so it
    /// reads the segments in place: no split array, and no regex where two length checks answer it.
    /// </summary>
    public static bool HasReservedName(string rel)
    {
        var start = 0;
        while (start <= rel.Length)
        {
            var end = rel.IndexOf('/', start);
            if (end < 0) end = rel.Length;
            if (IsReservedSegment(rel.AsSpan(start, end - start))) return true;
            start = end + 1;
        }
        return false;
    }

    static bool IsReservedSegment(ReadOnlySpan<char> segment)
    {
        var dot = segment.IndexOf('.');
        var stem = dot < 0 ? segment : segment[..dot];
        if (stem.Length == 3)
            return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                   || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        if (stem.Length != 4 || stem[3] is < '1' or > '9') return false;
        return stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
               || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"a/b/c" gives "a/b", then "a". Never the empty root.</summary>
    public static IEnumerable<string> Ancestors(string rel)
    {
        var i = rel.LastIndexOf('/');
        while (i > 0)
        {
            rel = rel[..i];
            yield return rel;
            i = rel.LastIndexOf('/');
        }
    }

    public static string RelativeTo(string baseAbs, string abs)
    {
        var r = Path.GetRelativePath(Path.GetFullPath(baseAbs), Path.GetFullPath(abs));
        if (r.StartsWith("..")) throw new SgException($"{abs} is not inside {baseAbs}");
        return Rel(r);
    }

    public static bool IsReparsePoint(string dir) =>
        Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);
}
