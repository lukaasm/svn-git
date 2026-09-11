using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sg.Core;

/// <summary>
/// One @@ block of a unified diff, kept verbatim so a patch built out of it is byte for byte what the
/// diff said. Lines carry their marker: a space for context, + for a line the new side has, - for one
/// only the old side has, and \ for "no newline at end of file".
/// </summary>
public sealed class PatchHunk
{
    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }

    /// <summary>Whatever git wrote after the second @@, usually the enclosing function. Kept for the reader.</summary>
    public string Heading { get; init; } = "";

    /// <summary>The body, verbatim, marker included.</summary>
    public List<string> Lines { get; init; } = new();

    /// <summary>Where it sits in its file's list, from 0. The UI names a hunk by this.</summary>
    public int Index { get; set; }

    /// <summary>The file it came from, as the b side names it.</summary>
    public string Path { get; set; } = "";

    public int Added => Lines.Count(l => l.StartsWith('+'));
    public int Removed => Lines.Count(l => l.StartsWith('-'));

    /// <summary>The lines of the new side, markers stripped. Context and added lines, in order.</summary>
    public List<string> NewLines => Body('+');

    /// <summary>The lines of the old side, markers stripped. Context and removed lines, in order.</summary>
    public List<string> OldLines => Body('-');

    List<string> Body(char own)
    {
        var res = new List<string>();
        foreach (var l in Lines)
        {
            if (l.Length == 0) { res.Add(""); continue; }   // a bare empty line means an empty context line
            var m = l[0];
            if (m == '\\') continue;
            if (m == ' ' || m == own) res.Add(Trim(l[1..]));
        }
        return res;
    }

    /// <summary>A CRLF file puts the CR inside the diff line; the file's own text is split on the break, so it goes.</summary>
    static string Trim(string s) => s.EndsWith('\r') ? s[..^1] : s;

    /// <summary>
    /// What the button says: the lines of the file this block actually changes. The block itself is
    /// wider, because a diff carries a few unchanged lines around each change so it can be placed
    /// again; naming those would say a block touches lines it leaves exactly as they were.
    /// </summary>
    public string Describe()
    {
        var line = NewStart;
        int firstAdded = 0, lastAdded = 0, removed = 0, removedAfter = Math.Max(NewStart - 1, 0);
        foreach (var l in Lines)
        {
            if (l.Length == 0 || l[0] == '\\') continue;
            if (l[0] == '+')
            {
                if (firstAdded == 0) firstAdded = line;
                lastAdded = line;
                line++;
            }
            else if (l[0] == '-')
            {
                removed++;
                if (firstAdded == 0) removedAfter = line - 1;
            }
            else line++;
        }
        if (firstAdded == 0) return $"{removed} deleted line(s) after line {removedAfter}";
        return firstAdded == lastAdded ? $"line {firstAdded}" : $"lines {firstAdded}-{lastAdded}";
    }

    /// <summary>The line under the cursor sits in this block. A block with no lines on the new side sits between two.</summary>
    public bool Covers(int newLine) =>
        NewCount == 0 ? newLine == NewStart || newLine == NewStart + 1
                      : newLine >= NewStart && newLine <= NewStart + NewCount - 1;
}

/// <summary>One file of a unified diff: the lines above the first @@, kept as they were, and the blocks under them.</summary>
public sealed class PatchFile
{
    /// <summary>The name the file has after the change: the b side for git, the Index: line for svn.</summary>
    public string Path { get; set; } = "";

    /// <summary>The name it had before, when the diff renames it. The same as Path otherwise.</summary>
    public string OldPath { get; set; } = "";

    /// <summary>Every line before the first @@, verbatim. A partial patch reuses them unchanged.</summary>
    public List<string> Header { get; init; } = new();

    public List<PatchHunk> Hunks { get; init; } = new();

    /// <summary>git said "Binary files differ" instead of hunks. Nothing here can be staged by block.</summary>
    public bool Binary { get; set; }
}

/// <summary>
/// Unified diffs, read as blocks and written back out. Everything TortoiseGit's "stage this hunk" and
/// TortoiseSVN's "revert this hunk" need sits here: git and svn both write this format, the parser keeps
/// every line as it was, and a patch built from a few blocks is one git apply understands.
/// </summary>
public static class Patch
{
    static readonly Regex HunkHead = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$", RegexOptions.Compiled);
    static readonly Regex LineBreak = new(@"\r\n|\r|\n", RegexOptions.Compiled);

    /// <summary>Reads a git or svn unified diff. Files come out in the order the diff names them.</summary>
    public static List<PatchFile> Parse(string unified)
    {
        var files = new List<PatchFile>();
        PatchFile? file = null;
        PatchHunk? hunk = null;
        List<string>? body = null;
        // How much of the block has arrived. A body is over when both sides have the lines the header promised.
        int oldSeen = 0, newSeen = 0;

        void CloseHunk()
        {
            if (file != null && hunk != null)
            {
                hunk.Index = file.Hunks.Count;
                hunk.Path = file.Path;
                file.Hunks.Add(hunk);
            }
            hunk = null;
            body = null;
            oldSeen = newSeen = 0;
        }

        foreach (var raw in unified.Split('\n'))
        {
            // A CRLF file keeps its CR inside the diff line. Match on the text without it, store the line with it.
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                CloseHunk();
                file = new PatchFile();
                var (a, b) = SplitGitHeader(line);
                file.OldPath = a;
                file.Path = b;
                file.Header.Add(line);
                files.Add(file);
                continue;
            }
            if (line.StartsWith("Index: ", StringComparison.Ordinal))
            {
                CloseHunk();
                file = new PatchFile { Path = line[7..].Trim() };
                file.OldPath = file.Path;
                file.Header.Add(line);
                files.Add(file);
                continue;
            }
            if (file == null) continue;

            var m = HunkHead.Match(line);
            if (m.Success)
            {
                CloseHunk();
                body = new List<string>();
                hunk = new PatchHunk
                {
                    OldStart = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    OldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1,
                    NewStart = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    NewCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 1,
                    Heading = m.Groups[5].Value,
                    Lines = body,
                };
                continue;
            }

            if (hunk == null)
            {
                if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                    line.StartsWith("GIT binary patch", StringComparison.Ordinal) ||
                    line.StartsWith("Cannot display: file marked as a binary type", StringComparison.Ordinal))
                    file.Binary = true;
                // Everything above the first @@ belongs to the file, and a partial patch needs it back.
                file.Header.Add(raw);
                continue;
            }

            // Inside a block. "\ No newline at end of file" belongs to it whether or not the counts are full.
            if (raw.StartsWith('\\')) { body!.Add(raw); continue; }
            // The header said how many lines each side has. Past that the block is over, and what follows
            // is the next file's header, or the empty string the final newline leaves behind.
            if (oldSeen >= hunk.OldCount && newSeen >= hunk.NewCount) { CloseHunk(); continue; }
            // git writes a single space for an empty context line, other tools write nothing at all.
            var marker = raw.Length == 0 ? ' ' : raw[0];
            if (marker == ' ') { body!.Add(raw.Length == 0 ? " " : raw); oldSeen++; newSeen++; continue; }
            if (marker == '+') { body!.Add(raw); newSeen++; continue; }
            if (marker == '-') { body!.Add(raw); oldSeen++; continue; }
            CloseHunk();
        }
        CloseHunk();

        return files;
    }

    static (string Old, string New) SplitGitHeader(string line)
    {
        // "diff --git a/old b/new". A path with a space in it makes this ambiguous; the b side after the
        // last " b/" is what every other reader in this repository takes, so take the same.
        var rest = line["diff --git ".Length..];
        var i = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        if (i < 0) return (rest, rest);
        var a = rest[..i];
        var b = rest[(i + 3)..];
        if (a.StartsWith("a/", StringComparison.Ordinal)) a = a[2..];
        return (Unquote(a), Unquote(b));
    }

    static string Unquote(string s) => s.Length > 1 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\\\"", "\"") : s;

    /// <summary>
    /// A patch holding only the chosen blocks of one file, ready for git apply. The old side is untouched,
    /// so every block still names the lines it named before; the new side moves up by what the blocks kept
    /// before it added or removed, which is exactly what git add --patch writes.
    /// </summary>
    public static string Render(PatchFile file, IEnumerable<PatchHunk> hunks)
    {
        var picked = hunks.Where(h => file.Hunks.Contains(h)).OrderBy(h => h.Index).ToList();
        if (picked.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var h in file.Header) sb.Append(h).Append('\n');
        var delta = 0;
        foreach (var h in picked)
        {
            var newStart = h.OldStart + delta;
            sb.Append("@@ -").Append(h.OldStart).Append(',').Append(h.OldCount)
              .Append(" +").Append(newStart).Append(',').Append(h.NewCount)
              .Append(" @@").Append(h.Heading).Append('\n');
            foreach (var l in h.Lines) sb.Append(l).Append('\n');
            delta += h.NewCount - h.OldCount;
        }
        return sb.ToString();
    }

    /// <summary>Every block of the file, as one patch. The same text the diff gave, rebuilt.</summary>
    public static string Render(PatchFile file) => Render(file, file.Hunks);

    /// <summary>
    /// Puts the chosen blocks back the way the old side has them, in text that is currently the new side.
    /// This is "revert this hunk": the rest of the file stays exactly as it is. It refuses rather than
    /// guesses when the text on disk no longer matches what the block says it should be.
    /// </summary>
    public static string Reverse(string text, IEnumerable<PatchHunk> hunks)
    {
        var lines = LineBreak.Split(text).ToList();
        var eol = Eol(text) ?? "\r\n";
        var offset = 0;
        foreach (var h in hunks.OrderBy(h => h.Index))
        {
            var newSide = h.NewLines;
            var oldSide = h.OldLines;
            // A block with nothing on the new side sits after line NewStart; every other one starts at it.
            var at = (h.NewCount == 0 ? h.NewStart : h.NewStart - 1) + offset;
            if (at < 0 || at + newSide.Count > lines.Count)
                throw new SgException($"the block names lines {h.NewStart}-{h.NewStart + Math.Max(newSide.Count, 1) - 1}, the file has {lines.Count} line(s). Refresh and pick it again.");
            for (var i = 0; i < newSide.Count; i++)
                if (!string.Equals(lines[at + i], newSide[i], StringComparison.Ordinal))
                    throw new SgException($"line {at + i + 1} is not what this block says it is. The file changed since the diff was read: refresh, then pick the block again.");
            lines.RemoveRange(at, newSide.Count);
            lines.InsertRange(at, oldSide);
            offset += oldSide.Count - newSide.Count;
        }
        return string.Join(eol, lines);
    }

    /// <summary>The line ending most lines use, or null when the text is a single line. A rewritten file keeps its own.</summary>
    public static string? Eol(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        if (crlf + lf + cr == 0) return null;
        return crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
    }

    /// <summary>The file of a parsed diff whose path ends the given one, or is ended by it. Diffs name paths relative to different roots.</summary>
    public static PatchFile? FileFor(IEnumerable<PatchFile> files, string path)
    {
        var p = path.Replace('\\', '/');
        PatchFile? loose = null;
        foreach (var f in files)
        {
            var k = f.Path.Replace('\\', '/');
            if (string.Equals(k, p, StringComparison.OrdinalIgnoreCase)) return f;
            if (p.EndsWith("/" + k, StringComparison.OrdinalIgnoreCase) || k.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase)) loose ??= f;
        }
        return loose;
    }

    /// <summary>The block a line of the new side sits in, or null when the line is unchanged text.</summary>
    public static PatchHunk? HunkAt(PatchFile? file, int newLine) => file?.Hunks.FirstOrDefault(h => h.Covers(newLine));

    /// <summary>Every block that overlaps a run of lines on the new side. Selecting across a file picks them all.</summary>
    public static List<PatchHunk> HunksIn(PatchFile? file, int firstLine, int lastLine)
    {
        if (file == null) return new();
        return file.Hunks.Where(h =>
        {
            var e = h.NewCount == 0 ? h.NewStart + 1 : h.NewStart + h.NewCount - 1;
            return h.NewStart <= lastLine && e >= firstLine;
        }).ToList();
    }
}
