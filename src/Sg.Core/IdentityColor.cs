namespace Sg.Core;

/// <summary>Stable presentation identity; the UI supplies the theme's palette rather than duplicating hex colors.</summary>
public static class IdentityColor
{
    public static int Index(string name)
    {
        var normalized = name.Trim().ToUpperInvariant();
        return Convert.ToInt32(WorkspaceVersion.Hash(normalized)[..2], 16) % 8;
    }
}
