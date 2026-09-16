namespace Sg.Core;

/// <summary>Publish complete text in one rename, keeping the previous file intact on write failure.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, leaveOpen: true)) { writer.Write(text); writer.Flush(); }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
