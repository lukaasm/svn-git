using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sg.Core;

/// <summary>Small, root-owned checkout images. Config stores only a content-addressed filename.</summary>
public static class CheckoutAppearance
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxDimension = 128;

    public static string? IconPath(SgRoot root, CheckoutConfig checkout)
    {
        var name = checkout.Icon;
        if (name is not { Length: 68 } || !name.EndsWith(".png", StringComparison.Ordinal)
            || name[..64].Any(c => !char.IsAsciiHexDigit(c))) return null;
        var folder = Path.Combine(root.StorePath, "icons");
        var path = Path.Combine(folder, name);
        // An icon reference must never redirect reads or writes outside the managed directory.
        if (Linked(folder) || Linked(path)) return null;
        return path;
    }

    static bool Linked(string path) => (File.Exists(path) || Directory.Exists(path))
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>The UI decodes and scales an image before publishing it here. Null restores initials.</summary>
    public static void SetIcon(SgRoot root, string checkout, byte[]? png)
    {
        using var lease = root.Lock();
        var co = root.Checkout(checkout);
        var old = co.Icon;
        var oldPath = IconPath(root, co);
        string? name = null;
        if (png != null)
        {
            Validate(png);
            name = Convert.ToHexStringLower(SHA256.HashData(png)) + ".png";
            var path = IconPath(root, new CheckoutConfig { Icon = name })
                ?? throw new SgException("The checkout icon folder must be a regular folder inside this sg root.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, png);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        co.Icon = name;
        try { root.Save(); }
        catch { co.Icon = old; throw; }
        // Other checkouts may use the same image. Retire only this checkout's unused previous asset.
        if (oldPath != null && old != name && !root.Config.Checkouts.Any(c => c.Icon == old))
        {
            try { File.Delete(oldPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void Validate(byte[] png)
    {
        if (png.Length is < 33 or > MaxBytes || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) is 0 or > MaxDimension
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) is 0 or > MaxDimension)
            throw new SgException("Choose an image that can be converted to a small PNG checkout icon.");
    }
}
