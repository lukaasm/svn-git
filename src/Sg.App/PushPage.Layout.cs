using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

public sealed partial class PushPage
{
    bool _compact, _showDiff;
    double _wideLeft = 540;

    // Both views keep their controls and selection. Narrow windows switch panes instead of clipping
    // a fixed-width diff, and selecting a file opens its diff without rebuilding the file tree.
    void ArrangeReview()
    {
        // The child grid can report its columns' minimum width even while clipped. Use the page's
        // available width so those very minimums cannot prevent the compact layout from activating.
        var width = ActualWidth;
        if (width <= 0) return;
        if (!_compact && LeftCol.Width.IsAbsolute) _wideLeft = LeftCol.Width.Value;
        _compact = width < 900;
        LeftCol.MinWidth = _compact ? 0 : 320;
        RightCol.MinWidth = _compact ? 0 : 400;
        LeftCol.Width = _compact ? new GridLength(1, GridUnitType.Star)
            : new GridLength(Math.Clamp(_wideLeft, 320, Math.Max(320, width - 410)));
        RightCol.Width = _compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SplitterCol.Width = new GridLength(_compact ? 0 : 10);
        Splitter.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        CompactViews.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(DiffPane, _compact ? 0 : 2);
        ChangesPane.Visibility = !_compact || !_showDiff ? Visibility.Visible : Visibility.Collapsed;
        DiffPane.Visibility = !_compact || _showDiff ? Visibility.Visible : Visibility.Collapsed;
        var selected = _showDiff ? DiffTab : ChangesTab;
        if (CompactViews.SelectedItem != selected) CompactViews.SelectedItem = selected;
    }

    void ShowDiff(bool show) { _showDiff = show; ArrangeReview(); }
    void ReviewView_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var show = sender.SelectedItem == DiffTab;
        if (_showDiff != show) ShowDiff(show);
    }
}
