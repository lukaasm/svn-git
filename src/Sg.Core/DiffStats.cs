namespace Sg.Core;

/// <summary>
/// Lines added and removed per file, read off a unified diff. git and svn both write one and only the
/// file headers differ, so the patch a page already loads for its "all files" view is also where the
/// numbers beside each file come from; nothing runs a second time for them.
/// </summary>
public static class DiffStats
{
    public readonly record struct Count(int Added, int Removed);

    /// <summary>
    /// Keys are the paths as the diff names them: relative to the worktree for git, relative to the
    /// folder the diff was asked for with svn. A binary file is in the result with zero on both sides.
    /// </summary>
    public static Dictionary<string, Count> Parse(string unified)
    {
        var result = new Dictionary<string, Count>(StringComparer.OrdinalIgnoreCase);
        string? file = null;
        int added = 0, removed = 0;

        void Flush()
        {
            if (file != null) result[file] = new Count(added, removed);
            file = null;
            added = removed = 0;
        }

        foreach (var raw in unified.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                // "diff --git a/old b/new": the b side is the name the file has now, which is the one a row shows.
                Flush();
                var i = line.LastIndexOf(" b/", StringComparison.Ordinal);
                file = i > 0 ? Unquote(line[(i + 3)..]) : null;
                continue;
            }
            if (line.StartsWith("Index: ", StringComparison.Ordinal))
            {
                Flush();
                file = line[7..].Trim();
                continue;
            }
            if (file == null) continue;
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (line.StartsWith('+')) added++;
            else if (line.StartsWith('-')) removed++;
        }
        Flush();
        return result;
    }

    /// <summary>The count for a row's path: an exact match, or the diff's shorter relative path that ends the row's.</summary>
    public static Count? For(IReadOnlyDictionary<string, Count> stats, string path)
    {
        var p = path.Replace('\\', '/');
        if (stats.TryGetValue(p, out var exact)) return exact;
        foreach (var (key, count) in stats)
        {
            var k = key.Replace('\\', '/');
            if (p.EndsWith("/" + k, StringComparison.OrdinalIgnoreCase) || k.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase)) return count;
        }
        return null;
    }

    /// <summary>git quotes a path with unusual characters; the row does not.</summary>
    static string Unquote(string s) => s.Length > 1 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\\\"", "\"") : s;
}
