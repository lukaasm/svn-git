using Microsoft.UI.Xaml;

namespace Sg.App;

/// <summary>Templates and brushes every window shares. A dictionary with code behind, so x:Bind works inside it.</summary>
public sealed partial class Templates : ResourceDictionary
{
    public Templates()
    {
        InitializeComponent();
    }

    /// <summary>The chevron on a folder line. The button keeps the click, so the line is not selected by it.</summary>
    void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TreeNode node) node.Toggle();
    }

    /// <summary>
    /// The tick box on a line. The binding only reads, and the click is what writes: a two way binding
    /// wrote the box's own idea of the half state back into the tree, and a folder that landed on half
    /// then ticked everything under it, unversioned files included. The box cycles itself first, so it
    /// is put back to whatever the node worked out afterwards.
    /// </summary>
    void Check_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.CheckBox box || box.DataContext is not TreeNode node) return;
        // Half ticked or unticked, a click ticks the whole line; ticked, it clears it.
        node.Checked = node.Checked != true;
        box.IsChecked = node.Checked;
    }
}
