using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Sg.App;

/// <summary>
/// The accent SplitButton WinUI does not ship. A page's primary action has to read as the primary action
/// whether it is one button or two halves of one, and the framework has an accent style only for Button.
///
/// The brushes go into the control's own resources, which is where its template looks for them first.
/// They cannot go into a Style: Style.Resources is a WPF idea and the XAML compiler refuses it here. They
/// come from a XAML dictionary rather than a loop in code, because a control that is already templated
/// does not read a resource entry again just because the entry was replaced: the framework re-resolves it
/// on a theme change only if it owns both sets, which is what AccentSplitButtonResources gives it.
/// </summary>
public sealed class AccentSplitButton : SplitButton
{
    public AccentSplitButton()
    {
        Resources.MergedDictionaries.Add(new AccentSplitButtonResources());
        IsEnabledChanged += (_, _) => { if (IsEnabled) Retemplate(); };
    }

    /// <summary>
    /// The stock template greys a disabled SplitButton through the two Buttons inside it: their Disabled
    /// state paints the disabled background and foreground onto their own root grid and presenter. In
    /// WinUI a state setter that took a ThemeResource is not taken back when the state is left, so the
    /// button that was disabled while the checkout was read came back enabled and still wearing the
    /// disabled paint: a see-through face and grey words on a button that worked. Building the template
    /// again makes the inner buttons fresh, in Normal from the start.
    /// </summary>
    void Retemplate()
    {
        var template = Template;
        if (template == null) return;
        Template = null;
        Template = template;
    }
}

/// <summary>
/// A button that says what it does with a glyph and a word. Every command in the app is one of these, so
/// the icon size, the gap and the reading order are decided once instead of in thirty places.
/// </summary>
public sealed class IconButton : Button
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(IconButton), new PropertyMetadata("", Changed));

    /// <summary>The Fluent glyph, as the character itself.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(IconButton), new PropertyMetadata("", Changed));

    /// <summary>The word on the button. It is the name a screen reader announces too.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IconButton() => Build();

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((IconButton)d).Build();

    void Build()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (Glyph.Length > 0) row.Children.Add(new FontIcon { Glyph = Glyph, FontSize = 14 });
        if (Text.Length > 0) row.Children.Add(new TextBlock { Text = Text });
        Content = row;
        AutomationProperties.SetName(this, Text);
    }
}

/// <summary>
/// The behaviour of the grab handle between two panes: it resizes the grid column immediately to its
/// left, keeping that column and the rest of the row above a minimum. Thumb is sealed, so this
/// attaches rather than inherits; the look lives in the Splitter style. Every window with a split
/// used to carry its own copy of both.
/// </summary>
public static class ColumnSplitter
{
    public static void Attach(Thumb thumb, double minLeft = 320, double minRight = 420) =>
        thumb.DragDelta += (_, e) =>
        {
            // The column to the left is the one being dragged; the thumb sits in the one after it.
            if (thumb.Parent is not Grid grid) return;
            var index = Grid.GetColumn(thumb) - 1;
            if (index < 0 || index >= grid.ColumnDefinitions.Count) return;
            var column = grid.ColumnDefinitions[index];

            // Everything else that cannot give way: the other fixed columns, this thumb's included.
            var fixedElsewhere = 0.0;
            for (var i = 0; i < grid.ColumnDefinitions.Count; i++)
                if (i != index && !grid.ColumnDefinitions[i].Width.IsStar)
                    fixedElsewhere += grid.ColumnDefinitions[i].ActualWidth;

            var most = Math.Max(minLeft, grid.ActualWidth - fixedElsewhere - minRight);
            column.Width = new GridLength(Math.Clamp(column.ActualWidth + e.HorizontalChange, minLeft, most));
        };
}

/// <summary>
/// A horizontal row that starts a new line instead of running off the edge. WinUI ships no such panel,
/// and a StackPanel does not wrap: a worktree that is behind and dirty and half pushed wears three
/// badges beside its branch name, and the last of them used to leave the card with no ellipsis and no
/// way to scroll to it. Anything that can show a variable number of things side by side goes in here.
/// </summary>
public sealed class WrapRow : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapRow), new PropertyMetadata(8.0, Relayout));

    /// <summary>The gap between two items, used both along a line and between lines.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(WrapRow), new PropertyMetadata(double.NaN, Relayout));

    /// <summary>The gap between two lines. Unset follows Spacing, so one number is usually enough.</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    static void Relayout(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((WrapRow)d).InvalidateMeasure();

    double Between => double.IsNaN(LineSpacing) ? Spacing : LineSpacing;

    protected override Size MeasureOverride(Size available)
    {
        // An unbounded width is one endless line, which is what a StackPanel would have given anyway.
        var limit = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
        double lineWidth = 0, lineHeight = 0, widest = 0, total = 0;
        var first = true;

        foreach (var child in Children)
        {
            child.Measure(new Size(limit, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0) continue;   // a collapsed badge takes no gap either

            var gap = lineWidth > 0 ? Spacing : 0;
            if (lineWidth > 0 && lineWidth + gap + size.Width > limit)
            {
                widest = Math.Max(widest, lineWidth);
                total += lineHeight + (first ? 0 : Between);
                first = false;
                lineWidth = 0;
                lineHeight = 0;
                gap = 0;
            }
            lineWidth += gap + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        widest = Math.Max(widest, lineWidth);
        total += lineHeight + (first || lineHeight == 0 ? 0 : Between);
        return new Size(double.IsInfinity(available.Width) ? widest : Math.Min(widest, limit), total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, lineHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width == 0 && size.Height == 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var gap = x > 0 ? Spacing : 0;
            if (x > 0 && x + gap + size.Width > final.Width)
            {
                x = 0;
                y += lineHeight + Between;
                lineHeight = 0;
                gap = 0;
            }
            child.Arrange(new Rect(x + gap, y, size.Width, size.Height));
            x += gap + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return final;
    }
}

/// <summary>
/// The motion this app allows itself. Short, and only where something would otherwise change with no
/// warning: a page arriving, a progress bar starting and stopping. Rows do not animate at all - seventy
/// files sliding into place one after another was a wait, not a welcome - and neither does anything the
/// reader is about to press. When Windows says animations are off, none of this runs.
/// </summary>
public static class Motion
{
    static bool? _enabled;

    /// <summary>The "Animation effects" switch in Windows Settings. Read once: it needs a restart to change.</summary>
    public static bool Enabled
    {
        get
        {
            if (_enabled is { } known) return known;
            try { _enabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
            catch (Exception) { _enabled = true; }
            return _enabled.Value;
        }
    }

    /// <summary>
    /// Fades an element to an opacity. The thin bar over a page used to snap from nothing to a running
    /// sweep and back, which reads as a flicker rather than as work starting.
    /// </summary>
    public static void FadeTo(UIElement element, double to, int ms = 140)
    {
        if (!Enabled) { element.Opacity = to; return; }
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Begin();
    }

    /// <summary>
    /// A page arriving: it fades up and settles a few pixels sideways, from the right going forward and
    /// from the left coming back, so a back button is felt and not only read. Short enough that nobody
    /// waits for it, which is the whole budget a navigation gets.
    /// </summary>
    public static void Enter(FrameworkElement page, bool returning)
    {
        if (!Enabled) { page.Opacity = 1; page.RenderTransform = null; return; }
        var slide = new TranslateTransform { X = returning ? -14 : 14 };
        page.RenderTransform = slide;
        page.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(130), EasingFunction = ease };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var move = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = ease };
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, "X");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(move);
        // The page keeps neither the opacity nor the transform: a long lived page, like the overview,
        // comes back through here every time and must not accumulate either.
        sb.Completed += (_, _) => { page.Opacity = 1; page.RenderTransform = null; };
        sb.Begin();
    }
}

/// <summary>
/// A list of rows: the app's row height and padding, and the same staggered entrance every other list
/// uses. Lists differ in what they show, never in how they behave, so they all start here.
/// </summary>
public class RowListView : ListView
{
    public RowListView()
    {
        ItemContainerStyle = (Style)Application.Current.Resources["CompactItem"];
        // Rows appear; they do not cascade in. Seventy files sliding into place one after another was
        // a wait, not a welcome.
        ItemContainerTransitions = new TransitionCollection();
    }
}

/// <summary>
/// A tree of files drawn on a list: one line per visible node, indented by its depth, a chevron on a
/// folder. The filter fills it and folds it. Every file tree in the app is one of these, so the density
/// is the density every other list has, and Left and Right fold and unfold the way Explorer does.
/// </summary>
public class RowTreeView : ListView
{
    public RowTreeView()
    {
        ItemContainerStyle = (Style)Application.Current.Resources["CompactItem"];
        ItemContainerTransitions = new TransitionCollection();
        SelectionMode = ListViewSelectionMode.Single;
        KeyDown += (_, e) =>
        {
            if (SelectedItem is not TreeNode node) return;
            if (e.Key == Windows.System.VirtualKey.Right)
            {
                if (node.IsFolder && !node.IsExpanded) node.IsExpanded = true;
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Left)
            {
                if (node.IsFolder && node.IsExpanded) node.IsExpanded = false;
                else if (node.Parent != null) SelectedItem = node.Parent;
                e.Handled = true;
            }
        };
        DoubleTapped += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is TreeNode { IsFolder: true } node) node.Toggle();
        };
    }
}

/// <summary>
/// What an empty list says instead of nothing: a glyph in a tinted circle, a line that says whether
/// the state is fine or what to do about it, and room for the one button that does it. It sits in
/// the middle of the space the rows would have filled, so an empty page never looks like a page
/// that failed to load.
/// </summary>
public sealed class EmptyState : ContentControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyState), new PropertyMetadata("\uE73E", Changed));

    /// <summary>The Fluent glyph in the circle.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata("", Changed));

    /// <summary>One line, the state itself: "No changes", "Up to date with the server".</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(EmptyState), new PropertyMetadata("", Changed));

    /// <summary>The line under it: what that means, or what to do next.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(UIElement), typeof(EmptyState), new PropertyMetadata(null, Changed));

    /// <summary>The one button that changes the state, when there is one.</summary>
    public UIElement? Action
    {
        get => (UIElement?)GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    readonly FontIcon _icon = new() { FontSize = 36 };
    readonly Border _circle = new() { Width = 88, Height = 88, CornerRadius = new CornerRadius(44), HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _title = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _text = new() { FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    readonly StackPanel _panel = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public EmptyState()
    {
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Padding = new Thickness(24);
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _circle.Child = _icon;
        _circle.Margin = new Thickness(0, 0, 0, 8);
        _panel.Children.Add(_circle);
        _panel.Children.Add(_title);
        _panel.Children.Add(_text);
        Content = _panel;
        Paint();
        ActualThemeChanged += (_, _) => Paint();
    }

    static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((EmptyState)d).Apply(e);

    void Apply(DependencyPropertyChangedEventArgs e)
    {
        _icon.Glyph = Glyph;
        _title.Text = Title;
        _title.Visibility = Title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = Text;
        _text.Visibility = Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (e.Property != ActionProperty) return;
        if (e.OldValue is UIElement old) _panel.Children.Remove(old);
        if (e.NewValue is UIElement added)
        {
            if (added is FrameworkElement f)
            {
                f.HorizontalAlignment = HorizontalAlignment.Center;
                f.Margin = new Thickness(0, 8, 0, 0);
            }
            _panel.Children.Add(added);
        }
    }

    void Paint()
    {
        _circle.Background = Res("SubtleFillColorSecondaryBrush");
        _icon.Foreground = Res("AccentTextFillColorPrimaryBrush");
        _text.Foreground = Res("TextFillColorSecondaryBrush");
    }

    static Brush? Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b ? b : null;

    public void Show() => Visibility = Visibility.Visible;
    public void Hide() => Visibility = Visibility.Collapsed;
}
