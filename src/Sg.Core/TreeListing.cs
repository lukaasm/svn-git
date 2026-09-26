using System.Text;

namespace Sg.Core;

/// <summary>
/// `git ls-tree -z [-r] &lt;tree-ish&gt; [-- &lt;path&gt;...]` without a process: the tree objects read on
/// the operation's reader, and git's own rules for which entries a path names and which folders are
/// opened - ported from tree-walk.c (tree_entry_interesting, match_entry, match_dir_prefix) and
/// builtin/ls-tree.c (show_recursive, show_tree_common) for the literal paths ls-tree takes. The
/// answer is the same list, in the same order, with the same modes; tests hold it against git's.
/// Anything this does not cover goes back to git: a path git would read as magic or refuse, a tree it
/// cannot read.
/// </summary>
static class TreeListing
{
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Reading a tree failed, or a path is one this does not read as git does: git is asked instead.</summary>
    sealed class AskGit : Exception;

    /// <summary>
    /// The listing, or null when git has to be asked. read hands back a tree's id and bytes for a
    /// revision, or null when the revision names nothing or could not be asked.
    /// </summary>
    public static List<TreeEntry>? List(Func<string, (string Oid, byte[] Data)?> read, string treeish, IReadOnlyList<string>? paths, bool recursive)
    {
        if (paths != null && !paths.All(Plain)) return null;
        var items = paths is { Count: > 0 } ? paths : null;
        try
        {
            if (read(treeish + "^{tree}") is not { } root) return null;
            // An object id is 20 bytes in a SHA-1 store and 32 in a SHA-256 one; the root's says which.
            var result = new List<TreeEntry>();
            Walk(read, root.Data, root.Oid.Length / 2, "", items, recursive, result);
            return result;
        }
        catch (AskGit) { return null; }
    }

    /// <summary>A path git takes as the plain path it is. Anything else - magic, "..", an empty one - git handles, with its own words.</summary>
    static bool Plain(string path) =>
        path.Length > 0 && path[0] != ':' && path[0] != '/' && !path.Contains('\\') && !path.Contains("//", StringComparison.Ordinal)
        && path.Split('/').SkipLast(path.EndsWith('/') ? 1 : 0).All(s => s.Length > 0 && s != "." && s != "..");

    static void Walk(Func<string, (string Oid, byte[] Data)?> read, byte[] tree, int oidBytes, string @base, IReadOnlyList<string>? items, bool recursive, List<TreeEntry> result)
    {
        var at = 0;
        while (at < tree.Length)
        {
            var space = Array.IndexOf(tree, (byte)' ', at);
            var nul = space < 0 ? -1 : Array.IndexOf(tree, (byte)0, space + 1);
            if (space < 0 || nul < 0 || nul + 1 + oidBytes > tree.Length) throw new AskGit();
            var raw = Convert.ToInt32(Encoding.ASCII.GetString(tree, at, space - at), 8);
            var name = Utf8.GetString(tree, space + 1, nul - space - 1);
            var oid = Convert.ToHexString(tree, nul + 1, oidBytes).ToLowerInvariant();
            at = nul + 1 + oidBytes;

            var mode = Canonical(raw);
            var isTree = (mode & 0xF000) == 0x4000;
            var isGitlink = mode == 0xE000;
            if (items != null && !Interesting(@base, name, isTree, isGitlink, items)) continue;
            if (isTree && ShowRecursive(@base, name, recursive, items))
            {
                var sub = read(oid) ?? throw new AskGit();
                Walk(read, sub.Data, oidBytes, @base + name + "/", items, recursive, result);
                continue;
            }
            result.Add(new TreeEntry(Convert.ToString(mode, 8).PadLeft(6, '0'), isTree ? "tree" : isGitlink ? "commit" : "blob", oid, @base + name));
        }
    }

    /// <summary>canon_mode: a file is 100644 or 100755 by its exec bit, a link, a folder or a submodule by its kind.</summary>
    static int Canonical(int mode) => (mode & 0xF000) switch
    {
        0x8000 => 0x8000 | ((mode & 0x40) != 0 ? 0x1ED : 0x1A4),
        0xA000 => 0xA000,
        0x4000 => 0x4000,
        _ => 0xE000,
    };

    /// <summary>tree_entry_interesting for literal paths: the entry is named, or is a folder on the way to one, or lies under one.</summary>
    static bool Interesting(string @base, string name, bool isTree, bool isGitlink, IReadOnlyList<string> items)
    {
        var baselen = @base.Length;
        foreach (var match in items)
        {
            var matchlen = match.Length;
            if (baselen >= matchlen)
            {
                // match_dir_prefix: the folder this entry is in lies under the path.
                if (string.CompareOrdinal(@base, 0, match, 0, matchlen) == 0
                    && (matchlen == 0 || (matchlen < baselen && @base[matchlen] == '/') || match[matchlen - 1] == '/'))
                    return true;
                continue;
            }
            if (baselen == 0 || string.CompareOrdinal(@base, 0, match, 0, baselen) == 0)
                if (MatchEntry(name, isTree, isGitlink, match[baselen..])) return true;
        }
        return false;
    }

    /// <summary>match_entry: the entry is the path, or a folder (or a submodule named with one slash) the path goes on into.</summary>
    static bool MatchEntry(string name, bool isTree, bool isGitlink, string match)
    {
        var pathlen = name.Length;
        var matchlen = match.Length;
        if (pathlen > matchlen) return false;
        if (matchlen > pathlen)
        {
            if (match[pathlen] != '/') return false;
            if (!isTree && (!isGitlink || matchlen > pathlen + 1)) return false;
        }
        return string.CompareOrdinal(match, 0, name, 0, pathlen) == 0;
    }

    /// <summary>show_recursive: a folder is opened with -r, or when a path goes on past it; else it is listed as itself.</summary>
    static bool ShowRecursive(string @base, string name, bool recursive, IReadOnlyList<string>? items)
    {
        if (recursive) return true;
        if (items == null) return false;
        var baselen = @base.Length;
        foreach (var spec in items)
        {
            if (spec.Length < baselen || string.CompareOrdinal(@base, 0, spec, 0, baselen) != 0) continue;
            var rest = spec.Length - baselen;
            var len = name.Length;
            if (rest <= len) continue;
            if (spec[baselen + len] != '/') continue;
            if (string.CompareOrdinal(name, 0, spec, baselen, len) != 0) continue;
            return true;
        }
        return false;
    }
}
