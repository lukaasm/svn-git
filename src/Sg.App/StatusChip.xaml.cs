using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Sg.App;

/// <summary>How loud a chip is, and the colour that goes with it.</summary>
public enum ChipSeverity
{
    /// <summary>Worth a word, not worth a colour.</summary>
    Neutral,
    /// <summary>Something of yours is in there: edits, uncommitted work, unread commits.</summary>
    Attention,
    /// <summary>Nothing is broken, but the state is behind and wants an action.</summary>
    Caution,
    /// <summary>Stopped, missing, or half done.</summary>
    Critical,
    /// <summary>Done, matching, up to date.</summary>
    Success,
}

/// <summary>
/// The one badge in the app: a glyph in the colour of its severity, an optional count, an optional label.
/// A plain coloured circle only says "something"; the glyph says which something, and says it again for
/// anyone who cannot separate the colours.
/// </summary>
public sealed partial class StatusChip : UserControl
{
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(ChipSeverity), typeof(StatusChip), new PropertyMetadata(ChipSeverity.Neutral, Changed));

    public ChipSeverity Severity
    {
        get => (ChipSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(StatusChip), new PropertyMetadata("", Changed));

    /// <summary>The Fluent glyph that says what this is about.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusChip), new PropertyMetadata("", Changed));

    /// <summary>The words next to the glyph. Empty where there is no room for them, as in the pane.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int), typeof(StatusChip), new PropertyMetadata(0, Changed));

    /// <summary>How many. Zero shows no number, so a chip can be a plain marker.</summary>
    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public StatusChip()
    {
        InitializeComponent();
        Apply();
        ActualThemeChanged += (_, _) => Apply();
    }

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((StatusChip)d).Apply();

    void Apply()
    {
        var (fill, background) = Severity switch
        {
            ChipSeverity.Attention => ("SystemFillColorAttentionBrush", "SystemFillColorAttentionBackgroundBrush"),
            ChipSeverity.Caution => ("SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush"),
            ChipSeverity.Critical => ("SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush"),
            ChipSeverity.Success => ("SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush"),
            _ => ("TextFillColorSecondaryBrush", "SystemFillColorNeutralBackgroundBrush"),
        };
        Pill.Background = Res(background, "SubtleFillColorSecondaryBrush");
        Icon.Foreground = Res(fill);
        CountText.Foreground = Res(fill);
        Icon.Glyph = Glyph;
        Icon.Visibility = Glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CountText.Text = Count.ToString();
        CountText.Visibility = Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Label.Text = Text;
        Label.Visibility = Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The first of these the theme defines. A missing brush leaves the chip plain, never crashes it.</summary>
    static Brush? Res(params string[] keys)
    {
        foreach (var key in keys)
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush) return brush;
        return null;
    }
}
