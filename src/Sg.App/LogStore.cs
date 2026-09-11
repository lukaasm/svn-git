using System.Text;

namespace Sg.App;

/// <summary>
/// Every line every operation writes, in one place. Each window used to own a console welded to its
/// footer, which cost a fifth of the overview to say nothing between operations and still only ever
/// held that window's half of the story. The lines belong to the app; a window only has to say that
/// something of its own is running.
/// </summary>
public static class LogStore
{
    /// <summary>A sync of 173k files writes a lot of lines. Past this the oldest half goes.</summary>
    const int MaxChars = 400_000;

    static readonly object Gate = new();
    static readonly StringBuilder Buffer = new();

    /// <summary>Raised on whatever thread wrote. A window marshals it to its own.</summary>
    public static event Action? Changed;

    public static string Text
    {
        get { lock (Gate) return Buffer.ToString(); }
    }

    public static void Append(string line)
    {
        lock (Gate)
        {
            Buffer.Append(line).Append('\n');
            if (Buffer.Length > MaxChars) Trim();
        }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
        Changed?.Invoke();
    }

    /// <summary>Caller holds the gate.</summary>
    static void Trim()
    {
        var text = Buffer.ToString();
        var cut = text.IndexOf('\n', text.Length / 2);
        Buffer.Clear()
            .Append("... earlier lines dropped ...\n")
            .Append(text[(cut < 0 ? text.Length / 2 : cut + 1)..]);
    }
}
