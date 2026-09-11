using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Sg.App;

/// <summary>
/// One set of colours for reading code and reading a change. It covers both because they are the same
/// screen: the letter beside a file name in the list and the tint on the line in the diff stand for the
/// same thing, and a palette that only reached the editor would leave the list next to it disagreeing.
///
/// Every value is a plain "rrggbb", the way both Monaco and a XAML colour want it, and nothing here
/// carries alpha: the washes over an added or a removed line are worked out from Added and Removed, so
/// a theme says what green means once and the diff, the badges and the counts all follow it.
/// </summary>
public sealed record DiffTheme
{
    public required string Name { get; init; }

    /// <summary>Which of Monaco's two bases it builds on, and whether the app's dark brushes suit it.</summary>
    public required bool Dark { get; init; }

    // ---- the editor
    public required string Background { get; init; }
    public required string Foreground { get; init; }
    public required string LineNumber { get; init; }
    public required string Selection { get; init; }

    // ---- what the code is made of
    public required string Keyword { get; init; }
    public required string Literal { get; init; }
    public required string Comment { get; init; }
    public required string Number { get; init; }
    public required string Type { get; init; }
    public required string Function { get; init; }

    // ---- what a change is made of. These are the badge colours too.
    public required string Added { get; init; }
    public required string Removed { get; init; }
    public required string Modified { get; init; }
    public required string Renamed { get; init; }
    public required string Conflict { get; init; }
    public required string Untracked { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// The themes that ship. The first is what sg has always looked like; the rest are the palettes people
/// already have in their editor, written out here rather than downloaded, so a machine with no internet
/// still gets them and nothing has to be parsed at startup.
/// </summary>
public static class Themes
{
    public const string Default = "sg";

    public static readonly IReadOnlyList<DiffTheme> All = new[]
    {
        new DiffTheme
        {
            Name = "sg", Dark = true,
            Background = "1e1e1e", Foreground = "d4d4d4", LineNumber = "858585", Selection = "264f78",
            Keyword = "569cd6", Literal = "ce9178", Comment = "6a9955", Number = "b5cea8", Type = "4ec9b0", Function = "dcdcaa",
            Added = "73c991", Removed = "f14c4c", Modified = "e2c08d", Renamed = "b180d7", Conflict = "ff8c5a", Untracked = "9da5b4",
        },
        new DiffTheme
        {
            Name = "One Dark Pro", Dark = true,
            Background = "282c34", Foreground = "abb2bf", LineNumber = "4b5263", Selection = "3e4451",
            Keyword = "c678dd", Literal = "98c379", Comment = "5c6370", Number = "d19a66", Type = "e5c07b", Function = "61afef",
            Added = "98c379", Removed = "e06c75", Modified = "e5c07b", Renamed = "c678dd", Conflict = "d19a66", Untracked = "5c6370",
        },
        new DiffTheme
        {
            Name = "Dracula", Dark = true,
            Background = "282a36", Foreground = "f8f8f2", LineNumber = "6272a4", Selection = "44475a",
            Keyword = "ff79c6", Literal = "f1fa8c", Comment = "6272a4", Number = "bd93f9", Type = "8be9fd", Function = "50fa7b",
            Added = "50fa7b", Removed = "ff5555", Modified = "f1fa8c", Renamed = "bd93f9", Conflict = "ffb86c", Untracked = "6272a4",
        },
        new DiffTheme
        {
            Name = "Monokai", Dark = true,
            Background = "272822", Foreground = "f8f8f2", LineNumber = "90908a", Selection = "49483e",
            Keyword = "f92672", Literal = "e6db74", Comment = "75715e", Number = "ae81ff", Type = "66d9ef", Function = "a6e22e",
            Added = "a6e22e", Removed = "f92672", Modified = "e6db74", Renamed = "ae81ff", Conflict = "fd971f", Untracked = "75715e",
        },
        new DiffTheme
        {
            Name = "Nord", Dark = true,
            Background = "2e3440", Foreground = "d8dee9", LineNumber = "4c566a", Selection = "434c5e",
            Keyword = "81a1c1", Literal = "a3be8c", Comment = "616e88", Number = "b48ead", Type = "8fbcbb", Function = "88c0d0",
            Added = "a3be8c", Removed = "bf616a", Modified = "ebcb8b", Renamed = "b48ead", Conflict = "d08770", Untracked = "616e88",
        },
        new DiffTheme
        {
            Name = "Gruvbox Dark", Dark = true,
            Background = "282828", Foreground = "ebdbb2", LineNumber = "7c6f64", Selection = "3c3836",
            Keyword = "fb4934", Literal = "b8bb26", Comment = "928374", Number = "d3869b", Type = "8ec07c", Function = "fabd2f",
            Added = "b8bb26", Removed = "fb4934", Modified = "fabd2f", Renamed = "d3869b", Conflict = "fe8019", Untracked = "928374",
        },
        new DiffTheme
        {
            Name = "Tokyo Night", Dark = true,
            Background = "1a1b26", Foreground = "a9b1d6", LineNumber = "3b4261", Selection = "283457",
            Keyword = "bb9af7", Literal = "9ece6a", Comment = "565f89", Number = "ff9e64", Type = "2ac3de", Function = "7aa2f7",
            Added = "9ece6a", Removed = "f7768e", Modified = "e0af68", Renamed = "bb9af7", Conflict = "ff9e64", Untracked = "565f89",
        },
        new DiffTheme
        {
            Name = "GitHub Dark", Dark = true,
            Background = "0d1117", Foreground = "e6edf3", LineNumber = "6e7681", Selection = "264f78",
            Keyword = "ff7b72", Literal = "a5d6ff", Comment = "8b949e", Number = "79c0ff", Type = "ffa657", Function = "d2a8ff",
            Added = "3fb950", Removed = "f85149", Modified = "d29922", Renamed = "a371f7", Conflict = "db6d28", Untracked = "8b949e",
        },
        new DiffTheme
        {
            Name = "Solarized Dark", Dark = true,
            Background = "002b36", Foreground = "93a1a1", LineNumber = "586e75", Selection = "073642",
            Keyword = "859900", Literal = "2aa198", Comment = "586e75", Number = "d33682", Type = "b58900", Function = "268bd2",
            Added = "859900", Removed = "dc322f", Modified = "b58900", Renamed = "6c71c4", Conflict = "cb4b16", Untracked = "586e75",
        },
        new DiffTheme
        {
            Name = "GitHub Light", Dark = false,
            Background = "ffffff", Foreground = "1f2328", LineNumber = "8c959f", Selection = "b6d7ff",
            Keyword = "cf222e", Literal = "0a3069", Comment = "6e7781", Number = "0550ae", Type = "953800", Function = "8250df",
            Added = "1a7f37", Removed = "cf222e", Modified = "9a6700", Renamed = "8250df", Conflict = "bc4c00", Untracked = "6e7781",
        },
        new DiffTheme
        {
            Name = "Solarized Light", Dark = false,
            Background = "fdf6e3", Foreground = "657b83", LineNumber = "93a1a1", Selection = "eee8d5",
            Keyword = "859900", Literal = "2aa198", Comment = "93a1a1", Number = "d33682", Type = "b58900", Function = "268bd2",
            Added = "859900", Removed = "dc322f", Modified = "b58900", Renamed = "6c71c4", Conflict = "cb4b16", Untracked = "93a1a1",
        },
    };

    public static IEnumerable<string> Names => All.Select(t => t.Name);

    /// <summary>The theme by name, or the one sg has always had. A name from a newer build is not an error.</summary>
    public static DiffTheme ByName(string? name) =>
        All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static DiffTheme Current => ByName(Session.Settings.DiffTheme);

    /// <summary>The theme changed. Every diff on screen listens, so all of them repaint together.</summary>
    public static event Action? Changed;

    /// <summary>Picks one and makes it so: written to the settings, on the app's brushes, and on every diff.</summary>
    public static void Use(string name)
    {
        var theme = ByName(name);
        Session.Settings.DiffTheme = theme.Name;
        Session.Settings.Save();
        ApplyToApp(theme);
        Changed?.Invoke();
    }

    /// <summary>
    /// Paints the app's own status colours from the theme, so the letter beside a file name and the tint
    /// on its line in the diff are the same green. The brushes in Templates.xaml are shared instances:
    /// changing the colour on one repaints every row already on screen, with nothing to rebind.
    ///
    /// The dictionary this reaches is the one for the theme Windows is in. A theme built for the other
    /// one keeps its own colours, which is what should happen: a palette meant for a dark editor has no
    /// business deciding what a light one looks like.
    /// </summary>
    public static void ApplyToApp(DiffTheme theme)
    {
        Paint("StatusAddedBrush", theme.Added);
        Paint("StatusAddedBackgroundBrush", theme.Added, 0x2e);
        Paint("StatusDeletedBrush", theme.Removed);
        Paint("StatusDeletedBackgroundBrush", theme.Removed, 0x2e);
        Paint("StatusModifiedBrush", theme.Modified);
        Paint("StatusModifiedBackgroundBrush", theme.Modified, 0x2e);
        Paint("StatusRenamedBrush", theme.Renamed);
        Paint("StatusRenamedBackgroundBrush", theme.Renamed, 0x2e);
        Paint("StatusConflictBrush", theme.Conflict);
        Paint("StatusConflictBackgroundBrush", theme.Conflict, 0x2e);
        Paint("StatusUntrackedBrush", theme.Untracked);
        Paint("StatusUntrackedBackgroundBrush", theme.Untracked, 0x24);
        Paint("StatusOtherBrush", theme.Untracked);
    }

    static void Paint(string key, string hex, byte alpha = 0xff)
    {
        if (Application.Current.Resources.TryGetValue(key, out var found) && found is SolidColorBrush brush
            && Parse(hex) is { } c)
            brush.Color = Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    /// <summary>"rrggbb" to a colour. Null for anything that is not six hex digits.</summary>
    public static Color? Parse(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6) return null;
        return byte.TryParse(s[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
               && byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
               && byte.TryParse(s[4..], System.Globalization.NumberStyles.HexNumber, null, out var b)
            ? Color.FromArgb(0xff, r, g, b)
            : null;
    }
}
