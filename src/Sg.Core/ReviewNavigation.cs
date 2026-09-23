namespace Sg.Core;

public static class ReviewNavigation
{
    public static CodeThread[] Ordered(IEnumerable<CodeThread> threads) => threads
        .OrderBy(t => t.Anchor.File, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Anchor.File, StringComparer.Ordinal)
        .ThenBy(t => t.Anchor.First).ThenBy(t => t.Anchor.Side, StringComparer.Ordinal)
        .ThenBy(t => t.Id, StringComparer.Ordinal).ToArray();

    /// <summary>Wrap through open feedback, retaining the position of a thread just resolved.</summary>
    public static CodeThread? Next(IEnumerable<CodeThread> threads, string? currentId, string? file, bool forward)
    {
        var all = Ordered(threads);
        var current = Array.FindIndex(all, t => t.Id == currentId);
        if (current >= 0)
        {
            for (var offset = 1; offset <= all.Length; offset++)
            {
                var candidate = all[(current + (forward ? offset : -offset) + all.Length) % all.Length];
                if (candidate.State == "open") return candidate;
            }
            return null;
        }
        var open = all.Where(t => t.State == "open").ToArray();
        if (!forward) Array.Reverse(open);
        return open.FirstOrDefault(t => t.Anchor.File == file)
            ?? open.FirstOrDefault(t => forward ? StringComparer.OrdinalIgnoreCase.Compare(t.Anchor.File, file) > 0 : StringComparer.OrdinalIgnoreCase.Compare(t.Anchor.File, file) < 0)
            ?? open.FirstOrDefault();
    }
}
