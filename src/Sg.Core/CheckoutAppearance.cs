using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sg.Core;

/// <summary>Portable metadata shared by remote backups and export archives. Null Icon explicitly selects initials.</summary>
internal sealed record CheckoutAppearanceData(int Format, byte[]? Icon);
internal sealed record CheckoutAppearanceRestore(bool Restored = false, string? Warning = null);

/// <summary>Small, root-owned checkout images. Config stores only a content-addressed filename.</summary>
public static class CheckoutAppearance
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxDimension = 128;
    internal const int MaxEncodedBytes = MaxBytes * 4 / 3 + 1024;

    internal static CheckoutAppearanceData? Capture(SgRoot root, CheckoutConfig checkout)
    {
        try
        {
            return checkout.Icon switch
            {
                null => null,
                "" => new(1, null),
                _ => new(1, ReadIcon(root, checkout)),
            };
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SgException("The checkout icon is missing. Choose an image again or use initials before backing up or exporting.");
        }
    }

    internal static byte[] Encode(CheckoutAppearanceData data) => JsonSerializer.SerializeToUtf8Bytes(data);

    internal static CheckoutAppearanceData Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxEncodedBytes) throw new SgException("Saved checkout appearance exceeds the supported size.");
        CheckoutAppearanceData data;
        try
        {
            data = JsonSerializer.Deserialize<CheckoutAppearanceData>(bytes)
                ?? throw new SgException("Invalid saved checkout appearance.");
        }
        catch (JsonException) { throw new SgException("Invalid saved checkout appearance."); }
        if (data.Format != 1) throw new SgException("Unsupported checkout appearance format.");
        if (data.Icon != null) Validate(data.Icon);
        return data;
    }

    internal static CheckoutAppearanceRestore RestoreIfUnset(SgRoot root, CheckoutConfig checkout, CheckoutAppearanceData? data)
    {
        using var lease = root.Lock();
        // Both a custom image and an explicit choice of initials belong to this machine.
        if (data == null || checkout.Icon != null) return new();
        try
        {
            SetIcon(root, checkout.Name, data.Icon);
            return new(Restored: true);
        }
        catch (Exception e) when (e is SgException or IOException or UnauthorizedAccessException)
        {
            // Work has already been recovered. A cosmetic write failure must not hide that result.
            var warning = "Work recovered, but the checkout appearance could not be saved: " + e.Message;
            root.Log.Warn(warning);
            return new(Warning: warning);
        }
    }

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
        // Empty is an explicit choice of initials; null means no local choice yet.
        string name = "";
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

    internal static byte[] ReadIcon(SgRoot root, CheckoutConfig checkout)
    {
        var path = IconPath(root, checkout)
            ?? throw new SgException("The checkout icon is not a managed image. Choose it again before backing up or exporting.");
        using var file = File.OpenRead(path);
        if (file.Length > MaxBytes) throw new SgException("The checkout icon exceeds the supported size.");
        var png = new byte[checked((int)file.Length)];
        file.ReadExactly(png);
        Validate(png);
        if (Convert.ToHexStringLower(SHA256.HashData(png)) + ".png" != checkout.Icon)
            throw new SgException("The checkout icon changed on disk. Choose it again before backing up or exporting.");
        return png;
    }

    internal static void Validate(byte[] png)
    {
        if (png.Length is < 33 or > MaxBytes || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8)
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) is 0 or > MaxDimension
            || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) is 0 or > MaxDimension)
            throw new SgException("Choose an image that can be converted to a small PNG checkout icon.");
    }
}
