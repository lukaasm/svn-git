using System.Text;

namespace Sg.Core;

/// <summary>
/// `git diff --name-status -z --no-renames &lt;from&gt; &lt;to&gt;` between two trees, without a process:
/// both trees walked side by side, a folder whose id is the same on both sides skipped whole. A file
/// on one side only is A or D, the same path with another id or mode is M, a file that became a
/// link or a submodule (or back) is T, and a folder that became a file, or a file a folder, is the
/// one side's files gone and the other's come. The entries come in git's order, by path.
/// The answer is null when git has to be asked: a tree that cannot be read.
/// </summary>
static class TreeDiff
{
    static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    sealed class AskGit : Exception;

    readonly record struct Item(int Mode, string Oid);

    public static List<DiffEntry>? NameStatus(Func<string, (string Oid, byte[] Data)?> read, string from, string to)
    {
        try
        {
            if (read(from + "^{tree}") is not { } a || read(to + "^{tree}") is not { } b) return null;
            if (a.Oid.Length != 40 || b.Oid.Length != 40) return null;
            var res = new List<DiffEntry>();
            Walk(read, Parse(a.Data), Parse(b.Data), "", res);
            // git sorts what it found by path, a path compared byte by byte.
            res.Sort((x, y) => Utf8.GetBytes(x.Path).AsSpan().SequenceCompareTo(Utf8.GetBytes(y.Path)));
            return res;
        }
        catch (Exception e) when (e is AskGit or DecoderFallbackException or FormatException or ArgumentException) { return null; }
    }

    static Dictionary<string, Item> Parse(byte[] raw)
    {
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        var at = 0;
        while (at < raw.Length)
        {
            var space = Array.IndexOf(raw, (byte)' ', at);
            var nul = space < 0 ? -1 : Array.IndexOf(raw, (byte)0, space + 1);
            if (space < 0 || nul < 0 || nul + 21 > raw.Length) throw new AskGit();
            var mode = Convert.ToInt32(Encoding.ASCII.GetString(raw, at, space - at), 8);
            items[Utf8.GetString(raw, space + 1, nul - space - 1)] = new Item(mode, Convert.ToHexString(raw, nul + 1, 20).ToLowerInvariant());
            at = nul + 21;
        }
        return items;
    }

    static bool IsTree(Item i) => (i.Mode & 0xF000) == 0x4000;

    /// <summary>A file's kind as the diff tells them apart: a file (whatever its exec bit), a link, a submodule.</summary>
    static int Kind(Item i) => i.Mode & 0xF000;

    static void Walk(Func<string, (string Oid, byte[] Data)?> read, Dictionary<string, Item>? a, Dictionary<string, Item>? b, string @base, List<DiffEntry> res)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (a != null) names.UnionWith(a.Keys);
        if (b != null) names.UnionWith(b.Keys);
        foreach (var name in names)
        {
            var path = @base + name;
            Item? x = a != null && a.TryGetValue(name, out var ax) ? ax : null;
            Item? y = b != null && b.TryGetValue(name, out var by) ? by : null;
            if (x is { } l && y is { } r && l.Mode == r.Mode && l.Oid == r.Oid) continue;
            var xTree = x is { } xt && IsTree(xt);
            var yTree = y is { } yt && IsTree(yt);
            if (xTree || yTree)
            {
                // A folder on either side is opened; a file on the other side is gone or come by itself.
                if (x is { } lf && !xTree) res.Add(new DiffEntry('D', path, null));
                if (y is { } rf && !yTree) res.Add(new DiffEntry('A', path, null));
                Walk(read, xTree ? Parse(Tree(read, x!.Value.Oid)) : null, yTree ? Parse(Tree(read, y!.Value.Oid)) : null, path + "/", res);
                continue;
            }
            if (x == null) res.Add(new DiffEntry('A', path, null));
            else if (y == null) res.Add(new DiffEntry('D', path, null));
            else res.Add(new DiffEntry(Kind(x.Value) == Kind(y.Value) ? 'M' : 'T', path, null));
        }
    }

    static byte[] Tree(Func<string, (string Oid, byte[] Data)?> read, string oid) => (read(oid) ?? throw new AskGit()).Data;
}
