using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Sg.Core;

/// <summary>
/// Blobs, trees and commits written into the store without a git process: the object is its header
/// ("&lt;type&gt; &lt;size&gt;\0") and its bytes, named by the SHA-1 of both and kept zlib-compressed under
/// objects/xx/, which is all a loose object is. What goes in each is laid out byte for byte as
/// hash-object, mktree and commit-tree lay it out; tests hold every id against git's. Anything git
/// would write differently - a signed commit, a name git would clean up, a date it would read its own
/// way - is left to git by the caller.
/// </summary>
static class ObjectWriter
{
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>The id an object would have, and writes it into the store when it is not there.</summary>
    public static string Write(string store, string type, byte[] content)
    {
        var header = Encoding.ASCII.GetBytes(type + " " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0");
        var raw = new byte[header.Length + content.Length];
        header.CopyTo(raw, 0);
        content.CopyTo(raw, header.Length);
        var oid = Convert.ToHexString(SHA1.HashData(raw)).ToLowerInvariant();
        var dir = Path.Combine(store, "objects", oid[..2]);
        var file = Path.Combine(dir, oid[2..]);
        if (File.Exists(file)) return oid;
        Directory.CreateDirectory(dir);
        // Into a temporary file first and moved into place, so a reader never meets half an object; the
        // store asks for no compression of loose objects (core.looseCompression 0), and gets none.
        var temp = Path.Combine(dir, "tmp_obj_" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var z = new ZLibStream(fs, CompressionLevel.NoCompression))
                z.Write(raw);
            File.SetAttributes(temp, FileAttributes.ReadOnly);
            try { File.Move(temp, file); }
            catch (IOException) when (File.Exists(file)) { /* another writer made the same object */ }
        }
        finally
        {
            if (File.Exists(temp)) { File.SetAttributes(temp, FileAttributes.Normal); File.Delete(temp); }
        }
        return oid;
    }

    public static string Blob(string store, byte[] content) => Write(store, "blob", content);

    /// <summary>
    /// mktree's tree: the entries in git's order - by name, a folder's name read as if it ended in a
    /// slash - each as its mode in octal without a leading zero, its name, a NUL and the raw id.
    /// Null when an entry is not one mktree would take as it is.
    /// </summary>
    public static byte[]? TreeBytes(IEnumerable<TreeEntry> entries)
    {
        var list = new List<(byte[] Name, bool IsTree, int Mode, byte[] Id)>();
        foreach (var e in entries)
        {
            if (e.Path.Length == 0 || e.Path.Contains('/') || e.Path.Contains('\0') || e.Sha.Length != 40 || !e.Sha.All(Uri.IsHexDigit)) return null;
            int mode;
            try { mode = Convert.ToInt32(e.Mode, 8); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { return null; }
            var expected = mode switch { 0x4000 => "tree", 0xE000 => "commit", 0x81A4 or 0x81ED or 0xA000 => "blob", _ => null };
            if (expected == null || expected != e.Type) return null;
            list.Add((Utf8.GetBytes(e.Path), mode == 0x4000, mode, Convert.FromHexString(e.Sha)));
        }
        list.Sort((a, b) => Compare(a.Name, a.IsTree, b.Name, b.IsTree));
        for (var i = 1; i < list.Count; i++)
            if (list[i].Name.AsSpan().SequenceEqual(list[i - 1].Name)) return null;
        using var ms = new MemoryStream();
        foreach (var (name, _, mode, id) in list)
        {
            ms.Write(Encoding.ASCII.GetBytes(Convert.ToString(mode, 8) + " "));
            ms.Write(name);
            ms.WriteByte(0);
            ms.Write(id);
        }
        return ms.ToArray();
    }

    /// <summary>base_name_compare: bytes in order, a folder's name as if it went on with a slash.</summary>
    static int Compare(byte[] a, bool aTree, byte[] b, bool bTree)
    {
        var n = Math.Min(a.Length, b.Length);
        var c = a.AsSpan(0, n).SequenceCompareTo(b.AsSpan(0, n));
        if (c != 0) return c;
        int ca = a.Length > n ? a[n] : aTree ? '/' : 0;
        int cb = b.Length > n ? b[n] : bTree ? '/' : 0;
        return ca - cb;
    }

    /// <summary>
    /// commit-tree's commit: the tree, each parent, the author and the committer as "name &lt;email&gt;
    /// seconds zone", a blank line and the message exactly as given. Null when a name, an email or a
    /// date is one git would write differently.
    /// </summary>
    public static byte[]? CommitBytes(string tree, IEnumerable<string> parents, Ident author, Ident committer, string message)
    {
        if (!author.Plain || !committer.Plain) return null;
        var sb = new StringBuilder();
        sb.Append("tree ").Append(tree).Append('\n');
        foreach (var p in parents.Distinct(StringComparer.Ordinal)) sb.Append("parent ").Append(p).Append('\n');
        sb.Append("author ").Append(author.Line).Append('\n');
        sb.Append("committer ").Append(committer.Line).Append('\n');
        sb.Append('\n').Append(message);
        return Utf8.GetBytes(sb.ToString());
    }

    /// <summary>Who and when, as a commit's author or committer line holds them.</summary>
    public sealed record Ident(string Name, string Email, long Seconds, TimeSpan Zone)
    {
        public string Line => $"{Name} <{Email}> {Seconds.ToString(CultureInfo.InvariantCulture)} {(Zone < TimeSpan.Zero ? '-' : '+')}{Math.Abs(Zone.Hours):00}{Math.Abs(Zone.Minutes):00}";

        /// <summary>
        /// Nothing git's ident code would take out: at either end the characters it trims ("crud"),
        /// anywhere the ones that delimit the line. A name or an email with any of them is git's to write.
        /// </summary>
        public bool Plain => Name.Length > 0 && Clean(Name) && Clean(Email);

        static bool Clean(string s) =>
            s.Length > 0 && !Crud(s[0]) && !Crud(s[^1]) && s.IndexOfAny(['<', '>', '\n', '\0']) < 0;

        static bool Crud(char c) => c <= ' ' || c is '.' or ',' or ':' or ';' or '<' or '>' or '"' or '\\' or '\'';

        /// <summary>
        /// A date the way git takes GIT_AUTHOR_DATE here: the strict ISO form %aI writes, or the raw
        /// "seconds +zone". Null for anything else, which git reads its own way.
        /// </summary>
        public static (long Seconds, TimeSpan Zone)? Date(string text)
        {
            if (DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
                return (iso.ToUnixTimeSeconds(), iso.Offset);
            var parts = text.Split(' ');
            if (parts.Length == 2 && long.TryParse(parts[0].TrimStart('@'), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && parts[1].Length == 5 && parts[1][0] is '+' or '-' && int.TryParse(parts[1][1..3], out var h) && int.TryParse(parts[1][3..], out var m))
            {
                var zone = new TimeSpan(h, m, 0);
                return (seconds, parts[1][0] == '-' ? -zone : zone);
            }
            return null;
        }

        /// <summary>Now, in this machine's zone, as git stamps a commit that is given no date.</summary>
        public static (long Seconds, TimeSpan Zone) Now()
        {
            var now = DateTimeOffset.Now;
            return (now.ToUnixTimeSeconds(), now.Offset);
        }
    }
}
