using System.Text;

namespace Sg.Core;

/// <summary>
/// A tree that is another tree with some files put in and some taken out, built without a process:
/// what `git read-tree &lt;base&gt;` into a private index, `git update-index -z --index-info` and
/// `git write-tree` make, ported from their rules. update-index's --index-info may add, replace and
/// remove, so a file put where a folder is replaces the folder and a folder needed where a file is
/// replaces the file; mode 0 removes the one file named, and a folder is not a file. write-tree keeps
/// the id of every folder no change went into - read-tree's cache-tree - and writes the others again
/// from their entries, with the modes the index keeps (a file is 100644 or 100755 by its exec bit),
/// leaving out any folder that ends up empty. It refuses an object the store does not have.
/// The answer is null when the caller has to ask git: a path git would check or refuse, a mode it
/// would read its own way, an object that is not there, a tree that cannot be read.
/// </summary>
static class TreeEditor
{
    /// <summary>Names that are not UTF-8 are git's: decoding them here would change the bytes written back.</summary>
    static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);
    const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    sealed class Folder
    {
        public string? Sha;
        public byte[]? Raw;
        public SortedDictionary<string, object>? Children;
        public bool Touched;
    }

    sealed record File(int Mode, string Sha);

    sealed class AskGit : Exception;

    /// <param name="read">A tree's id and bytes for a revision or an id; null when it cannot be read.</param>
    /// <param name="exists">Whether the store has the object.</param>
    /// <param name="write">Writes tree bytes and hands back the tree's id.</param>
    public static string? Edit(Func<string, (string Oid, byte[] Data)?> read, Func<string, bool> exists, Func<byte[], string> write,
        string? baseTreeish, IEnumerable<(string Mode, string Sha, string Path)> changes)
    {
        try
        {
            // An index read from a tree and left alone writes that tree again; an empty one writes the empty tree.
            var root = new Folder { Children = baseTreeish == null ? new(StringComparer.Ordinal) : null, Touched = baseTreeish == null };
            if (baseTreeish != null)
            {
                if (read(baseTreeish + "^{tree}") is not { } top || top.Oid.Length != 40) return null;
                root.Sha = top.Oid;
                root.Raw = top.Data;
            }
            foreach (var (mode, sha, path) in changes)
            {
                var parts = Parts(path) ?? throw new AskGit();
                if (mode == "0") Remove(read, root, parts);
                else
                {
                    var m = Mode(mode) ?? throw new AskGit();
                    if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new AskGit();
                    if (m != 0xE000 && !exists(sha)) throw new AskGit();
                    Add(read, root, parts, new File(m, sha.ToLowerInvariant()));
                }
            }
            return Write(read, write, root) ?? write([]);
        }
        catch (Exception e) when (e is AskGit or DecoderFallbackException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>A path's parts when git takes it as it is, else null: nothing empty, no "." or "..", no .git, nothing NTFS reads differently.</summary>
    static string[]? Parts(string path)
    {
        if (path.Length == 0) return null;
        var parts = path.Split('/');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')) return null;
            if (p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.Equals("git~1", StringComparison.OrdinalIgnoreCase)) return null;
            if (p.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 || p.Any(c => c < ' ')) return null;
        }
        return parts;
    }

    /// <summary>A mode the index keeps as it is: a file, an executable, a link or a submodule.</summary>
    static int? Mode(string mode) => mode switch
    {
        "100644" => 0x81A4,
        "100755" => 0x81ED,
        "120000" => 0xA000,
        "160000" => 0xE000,
        _ => null,
    };

    static SortedDictionary<string, object> Load(Func<string, (string Oid, byte[] Data)?> read, Folder f)
    {
        if (f.Children != null) return f.Children;
        var raw = f.Raw ?? (read(f.Sha!) ?? throw new AskGit()).Data;
        var children = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var at = 0;
        while (at < raw.Length)
        {
            var space = Array.IndexOf(raw, (byte)' ', at);
            var nul = space < 0 ? -1 : Array.IndexOf(raw, (byte)0, space + 1);
            if (space < 0 || nul < 0 || nul + 21 > raw.Length) throw new AskGit();
            var mode = Convert.ToInt32(Encoding.ASCII.GetString(raw, at, space - at), 8);
            var name = Utf8.GetString(raw, space + 1, nul - space - 1);
            var oid = Convert.ToHexString(raw, nul + 1, 20).ToLowerInvariant();
            at = nul + 21;
            children[name] = (mode & 0xF000) == 0x4000 ? new Folder { Sha = oid } : new File(Canonical(mode), oid);
        }
        f.Raw = null;
        return f.Children = children;
    }

    /// <summary>create_ce_mode: what the index keeps for a file read from a tree.</summary>
    static int Canonical(int mode) => (mode & 0xF000) switch
    {
        0x8000 => (mode & 0x40) != 0 ? 0x81ED : 0x81A4,
        0xA000 => 0xA000,
        _ => 0xE000,
    };

    /// <summary>
    /// remove_file_from_index: the file of that name goes, if there is one. Either way the folders on
    /// the way are no longer the tree read-tree gave (cache_tree_invalidate_path) and are written again.
    /// </summary>
    static void Remove(Func<string, (string Oid, byte[] Data)?> read, Folder root, string[] parts)
    {
        var at = root;
        at.Touched = true;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!Load(read, at).TryGetValue(parts[i], out var next) || next is not Folder f) return;
            at = f;
            at.Touched = true;
        }
        var children = Load(read, at);
        if (children.TryGetValue(parts[^1], out var leaf) && leaf is File) children.Remove(parts[^1]);
    }

    /// <summary>add_index_entry with add and replace allowed: folders made on the way, a file in the way replaced, a folder in the way replaced.</summary>
    static void Add(Func<string, (string Oid, byte[] Data)?> read, Folder root, string[] parts, File file)
    {
        var at = root;
        at.Touched = true;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var children = Load(read, at);
            if (!children.TryGetValue(parts[i], out var next) || next is not Folder f)
                children[parts[i]] = f = new Folder { Children = new(StringComparer.Ordinal) };
            at = f;
            at.Touched = true;
        }
        Load(read, at)[parts[^1]] = file;
    }

    /// <summary>The folder's id: as it was when nothing went into it, else written from its entries; null when it holds nothing.</summary>
    static string? Write(Func<string, (string Oid, byte[] Data)?> read, Func<byte[], string> write, Folder f)
    {
        if (!f.Touched) return f.Sha == EmptyTree ? null : f.Sha;
        var entries = new List<TreeEntry>();
        foreach (var (name, child) in Load(read, f))
        {
            if (child is File file)
                entries.Add(new TreeEntry(Convert.ToString(file.Mode, 8), file.Mode == 0xE000 ? "commit" : "blob", file.Sha, name));
            else if (Write(read, write, (Folder)child) is { } sub)
                entries.Add(new TreeEntry("40000", "tree", sub, name));
        }
        if (entries.Count == 0) return null;
        return write(ObjectWriter.TreeBytes(entries) ?? throw new AskGit());
    }
}
