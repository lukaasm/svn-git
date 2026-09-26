using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>
/// The left of a worktree card's header, one control for the overview and the Backup page so the two
/// read the same: the folder, one press away; the name; and the backup badge after it, a glyph whose
/// colour is the state and whose tooltip holds the words. A worktree with no folder here wears a cloud
/// where the folder button would be, so the names still start on one line.
/// </summary>
public sealed partial class WorktreeTitle : UserControl
{
    readonly Button _folder;
    readonly FontIcon _remote;
    readonly TextBlock _name;
    readonly StatusChip _badge;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(WorktreeTitle), new PropertyMetadata("", Changed));

    /// <summary>The worktree's name.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(WorktreeTitle), new PropertyMetadata("", Changed));

    /// <summary>Its folder on this machine. Empty when the only copy is the backup.</summary>
    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public static readonly DependencyProperty BadgeSeverityProperty = DependencyProperty.Register(
        nameof(BadgeSeverity), typeof(ChipSeverity), typeof(WorktreeTitle), new PropertyMetadata(ChipSeverity.Neutral, Changed));

    public ChipSeverity BadgeSeverity
    {
        get => (ChipSeverity)GetValue(BadgeSeverityProperty);
        set => SetValue(BadgeSeverityProperty, value);
    }

    public static readonly DependencyProperty BadgeTextProperty = DependencyProperty.Register(
        nameof(BadgeText), typeof(string), typeof(WorktreeTitle), new PropertyMetadata("", Changed));

    /// <summary>The state in a few words. Read out by a screen reader, never written on the card.</summary>
    public string BadgeText
    {
        get => (string)GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public static readonly DependencyProperty BadgeTipProperty = DependencyProperty.Register(
        nameof(BadgeTip), typeof(string), typeof(WorktreeTitle), new PropertyMetadata("", Changed));

    /// <summary>The state in a sentence, on hover.</summary>
    public string BadgeTip
    {
        get => (string)GetValue(BadgeTipProperty);
        set => SetValue(BadgeTipProperty, value);
    }

    public static readonly DependencyProperty BadgeVisibilityProperty = DependencyProperty.Register(
        nameof(BadgeVisibility), typeof(Visibility), typeof(WorktreeTitle), new PropertyMetadata(Visibility.Visible, Changed));

    /// <summary>Collapsed when no backup is set up, so there is nothing to say.</summary>
    public Visibility BadgeVisibility
    {
        get => (Visibility)GetValue(BadgeVisibilityProperty);
        set => SetValue(BadgeVisibilityProperty, value);
    }

    public WorktreeTitle()
    {
        _folder = new Button
        {
            Style = (Style)Application.Current.Resources["QuietButton"], Padding = new Thickness(4), MinHeight = 0, MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center, Content = new FontIcon { Glyph = "", FontSize = 14 },
        };
        AutomationProperties.SetName(_folder, "Open folder");
        _folder.Click += (_, _) => { if (Path.Length > 0) Session.OpenInExplorer(Path); };
        _remote = new FontIcon
        {
            Glyph = "", FontSize = 14, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center,
            Foreground = StatusChip.ForegroundFor(ChipSeverity.Neutral),
        };
        ToolTipService.SetToolTip(_remote, "Only the backup holds this worktree. Restore makes a folder for it here.");
        _name = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        _badge = new StatusChip { Glyph = "", VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(_folder);
        row.Children.Add(_remote);
        row.Children.Add(_name);
        row.Children.Add(_badge);
        Content = row;
        IsTabStop = false;
        Apply();
    }

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WorktreeTitle)d).Apply();

    void Apply()
    {
        var local = Path.Length > 0;
        _folder.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        _remote.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(_folder, local ? "Open " + Path + " in your file manager." : null);
        _name.Text = Text;
        ToolTipService.SetToolTip(_name, Text);
        _badge.Severity = BadgeSeverity;
        _badge.Visibility = BadgeVisibility;
        ToolTipService.SetToolTip(_badge, BadgeTip.Length > 0 ? BadgeTip : null);
        AutomationProperties.SetName(_badge, BadgeText);
        AutomationProperties.SetHelpText(_badge, BadgeTip);
    }
}
