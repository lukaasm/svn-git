using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sg.Core;

/// <summary>Identity of the actual reviewed/planned content, including index and untracked files.</summary>
public static class WorkspaceVersion
{
    public static string Of(SgRoot root, string path, bool checkout = false)
    {
        var git = root.Git;
        var paths = checkout ? Ops.CheckoutChanges(root, root.CheckoutContaining(path)!).Select(x => x.Path)
            : git.StatusEntries(path, untracked: true).Select(x => x.Path);
        var files = paths.Distinct().Order(StringComparer.Ordinal).ToList();
        var hashes = git.HashFiles(files.Select(p => PathUtil.Join(path, p)).Where(File.Exists));
        var text = checkout ? JsonSerializer.Serialize(Ops.CheckoutChanges(root, root.CheckoutContaining(path)!).Select(x => new { x.Path, x.Item, x.Props })) + JsonSerializer.Serialize(hashes.OrderBy(x => x.Key)) : git.HeadSha(path) + "\n" + git.Run(path, "diff", "--binary", "HEAD").EnsureOk().StdOut
                   + git.Run(path, "diff", "--cached", "--binary").EnsureOk().StdOut
                   + JsonSerializer.Serialize(files) + JsonSerializer.Serialize(hashes.OrderBy(x => x.Key));
        return Hash(text);
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
