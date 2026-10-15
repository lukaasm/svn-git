using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Sg.App;

/// <summary>The small chevron in a section's header. Down while the section is open, right while it is folded.</summary>
public sealed class SectionChevron : Button
{
    readonly FontIcon _icon = new() { FontSize = 10 };
    bool _expanded = true;

    public SectionChevron()
    {
        Content = _icon;
        Padding = new Thickness(6, 4, 6, 4);
        MinWidth = 0;
        MinHeight = 0;
        Background = new SolidColorBrush(Colors.Transparent);
        BorderThickness = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Center;
        Expanded = true;
    }

    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            _icon.Glyph = value ? "" : "";
            var text = value ? "Collapse this section" : "Expand this section";
            ToolTipService.SetToolTip(this, text);
            AutomationProperties.SetName(this, text);
        }
    }
}

/// <summary>
/// The sections of one column, top to bottom: each has a body in its own grid row, and may have a chevron
/// that folds it away and a splitter under it that drags its height. The last open section takes the rest
/// of the column, so folding the one at the bottom gives its room to the one above it. Which sections are
/// folded is kept in the settings, per page, so the next visit finds the column as it was left. Sizes are
/// not kept: a window of another height would make them wrong.
/// </summary>
public sealed class SectionStack
{
    const double LastMinHeight = 80;

    sealed class Section
    {
        public string Key = "";
        public RowDefinition Row = null!;
        public FrameworkElement Body = null!;
        public SectionChevron? Chevron;
        public Thumb? Splitter;
        public GridLength Original;
        public double OriginalMax;
        public double? Sized;
    }

    readonly Grid _grid;
    readonly string _page;
    readonly List<Section> _sections = new();

    public SectionStack(Grid grid, string page)
    {
        _grid = grid;
        _page = page;
    }

    /// <summary>Adds the next section. The row keeps the height and the body keeps the cap it had in the markup until the user drags.</summary>
    public SectionStack Add(string key, int bodyRow, FrameworkElement body, SectionChevron? chevron = null, Thumb? splitterBelow = null)
    {
        var row = _grid.RowDefinitions[bodyRow];
        var s = new Section
        {
            Key = _page + "." + key, Row = row, Body = body, Chevron = chevron, Splitter = splitterBelow,
            Original = row.Height, OriginalMax = body.MaxHeight,
        };
        if (chevron != null) chevron.Click += (_, _) => Toggle(s);
        if (splitterBelow != null) splitterBelow.DragDelta += (_, e) => Drag(s, e.VerticalChange);
        _sections.Add(s);
        return this;
    }

    bool IsOpen(Section s) => !Session.Settings.CollapsedSections.Contains(s.Key);

    void Toggle(Section s)
    {
        var folded = Session.Settings.CollapsedSections;
        if (!folded.Remove(s.Key)) folded.Add(s.Key);
        Session.Settings.Save();
        Apply();
    }

    /// <summary>Lays the column out again from the settings. Call once after the last Add.</summary>
    public void Apply()
    {
        var last = _sections.LastOrDefault(IsOpen);
        foreach (var s in _sections)
        {
            var open = IsOpen(s);
            s.Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (s.Chevron != null) s.Chevron.Expanded = open;
            if (!open)
            {
                s.Row.Height = GridLength.Auto;
                s.Row.MinHeight = 0;
            }
            else if (s == last)
            {
                s.Row.Height = new GridLength(s.Original.IsStar ? s.Original.Value : 1, GridUnitType.Star);
                s.Row.MinHeight = LastMinHeight;
                s.Body.MaxHeight = double.PositiveInfinity;
            }
            else
            {
                s.Row.MinHeight = 0;
                s.Row.Height = s.Sized is { } h ? new GridLength(h) : s.Original;
                s.Body.MaxHeight = s.Sized != null ? double.PositiveInfinity : s.OriginalMax;
            }
            if (s.Splitter != null) s.Splitter.Visibility = open && s != last ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The section above the splitter grows or shrinks; the open section at the bottom is the one that gives
    /// way, as the columns do in ColumnSplitter. Its minimum is what stops the drag.
    /// </summary>
    void Drag(Section s, double change)
    {
        var last = _sections.LastOrDefault(IsOpen);
        if (last == null || last == s || !IsOpen(s)) return;
        var height = s.Row.ActualHeight;
        var slack = Math.Max(0, last.Row.ActualHeight - LastMinHeight);
        var next = Math.Clamp(height + change, Math.Min(60, height), height + slack);
        s.Sized = next;
        s.Body.MaxHeight = double.PositiveInfinity;
        s.Row.Height = new GridLength(next);
    }
}
