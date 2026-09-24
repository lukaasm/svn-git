namespace Sg.Core;

/// <summary>Stable presentation identity; the UI supplies the theme's palette rather than duplicating hex colors.</summary>
public static class IdentityColor
{
    /// <summary>Two text elements, or the first letters of two words. Never splits a Unicode character.</summary>
    public static string Initials(string name)
    {
        var words = System.Text.RegularExpressions.Regex.Matches(name.Trim(), @"[\p{L}\p{Nd}][\p{L}\p{M}\p{Nd}]*").Select(m => m.Value).ToArray();
        if (words.Length == 0) return "?";
        var text = words.Length > 1 ? System.Globalization.StringInfo.GetNextTextElement(words[0]) + System.Globalization.StringInfo.GetNextTextElement(words[1]) : words[0];
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(text.ToUpperInvariant());
        var upper = text.ToUpperInvariant();
        return elements.Length > 2 ? upper[..elements[2]] : upper;
    }
    public static int Index(string name)
    {
        var normalized = name.Trim().ToUpperInvariant();
        return Convert.ToInt32(WorkspaceVersion.Hash(normalized)[..2], 16) % 8;
    }
}
