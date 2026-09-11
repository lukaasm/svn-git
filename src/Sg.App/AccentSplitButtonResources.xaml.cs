using Microsoft.UI.Xaml;

namespace Sg.App;

/// <summary>
/// The accent brushes an <see cref="AccentSplitButton"/> merges into its own resources. It is a XAML
/// dictionary rather than a table in code so the framework owns the theme switch: it holds one set per
/// theme and re-resolves them itself, which is the part a code loop cannot do.
/// </summary>
public sealed partial class AccentSplitButtonResources : ResourceDictionary
{
    public AccentSplitButtonResources() => InitializeComponent();
}
