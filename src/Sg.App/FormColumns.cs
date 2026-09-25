using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Sg.App;

/// <summary>Equal form columns, stacked in reading order when either field would become too narrow.</summary>
public sealed class FormColumns : Panel
{
    const double Gap = 12;
    const double MinimumFieldWidth = 280;
    static int Columns(double width) => width >= MinimumFieldWidth * 2 + Gap ? 2 : 1;

    protected override Size MeasureOverride(Size available)
    {
        var columns = Columns(available.Width);
        var width = (available.Width - (columns - 1) * Gap) / columns;
        double height = 0, rowHeight = 0, desiredWidth = 0;
        var index = 0;
        foreach (var child in Children.Where(c => c.Visibility != Visibility.Collapsed))
        {
            child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
            desiredWidth = Math.Max(desiredWidth, child.DesiredSize.Width);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++index % columns == 0) { height += rowHeight + Gap; rowHeight = 0; }
        }
        height += index % columns != 0 ? rowHeight : index > 0 ? -Gap : 0;
        return new Size(double.IsInfinity(available.Width) ? desiredWidth * columns + (columns - 1) * Gap : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Children.Where(c => c.Visibility != Visibility.Collapsed).ToList();
        var columns = Columns(final.Width);
        var width = Math.Max(0, (final.Width - (columns - 1) * Gap) / columns);
        double y = 0;
        for (var i = 0; i < children.Count; i += columns)
        {
            var row = children.Skip(i).Take(columns).ToList();
            var height = row.Max(c => c.DesiredSize.Height);
            for (var j = 0; j < row.Count; j++) row[j].Arrange(new Rect(j * (width + Gap), y, width, height));
            y += height + Gap;
        }
        return final;
    }
}
