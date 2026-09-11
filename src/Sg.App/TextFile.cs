using System.Text;

namespace Sg.App;

/// <summary>
/// Reads a text file the way the diff shows it, and writes it back in the same encoding. A file that is not
/// UTF-8 is read as Latin-1, which keeps every byte as it was, so a chunk revert never scrambles it.
/// </summary>
public static class TextFile
{
    public sealed record Content(string Text, Encoding Encoding);

    static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    public static Content Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new Content(Utf8Bom.GetString(bytes, 3, bytes.Length - 3), Utf8Bom);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return new Content(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return new Content(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);
        try { return new Content(Utf8.GetString(bytes), Utf8); }
        catch (DecoderFallbackException) { return new Content(Encoding.Latin1.GetString(bytes), Encoding.Latin1); }
    }

    public static void Write(string path, string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        File.WriteAllBytes(path, all);
    }
}
