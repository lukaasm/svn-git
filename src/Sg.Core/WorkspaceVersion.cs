using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sg.Core;

/// <summary>Identity of the actual reviewed/planned content, including index and untracked files.</summary>
public static class WorkspaceVersion
{
    public static string Of(SgRoot root, string path, bool checkout = false)
    {
        return checkout
            ? OfCheckout(root, path, Ops.CheckoutChanges(root, root.CheckoutContaining(path)!))
            : OfBranch(root, path, root.Git.StatusEntries(path, untracked: true));
    }

    // A plan already scanned these files. Fingerprint that same inventory instead of walking it again.
    internal static string OfBranch(SgRoot root, string path, IReadOnlyList<StatusEntry> entries)
    {
        var git = root.Git;
        var files = entries.Select(x => x.Path).Distinct().Order(StringComparer.Ordinal).ToList();
        var hashes = HashFiles(root, path, files);
        return Hash(git.HeadSha(path) + "\n" + git.Run(path, "diff", "--binary", "HEAD").EnsureOk().StdOut
            + git.Run(path, "diff", "--cached", "--binary").EnsureOk().StdOut
            + JsonSerializer.Serialize(files) + JsonSerializer.Serialize(hashes.OrderBy(x => x.Key)));
    }

    internal static string OfCheckout(SgRoot root, string path, IReadOnlyList<Ops.SvnChange> entries)
    {
        var hashes = HashFiles(root, path, entries.Select(x => x.Path));
        return Hash(JsonSerializer.Serialize(entries.Select(x => new { x.Path, x.Item, x.Props }))
            + JsonSerializer.Serialize(hashes.OrderBy(x => x.Key)));
    }

    static Dictionary<string, string> HashFiles(SgRoot root, string path, IEnumerable<string> paths)
    {
        var files = paths.Distinct().Order(StringComparer.Ordinal).ToList();
        return root.Git.HashFiles(files.Select(p => PathUtil.Join(path, p)).Where(File.Exists));
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
