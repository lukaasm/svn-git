using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Sg.App;

/// <summary>A resizable list/detail split that keeps both views alive when narrow windows use tabs.</summary>
internal sealed class ReviewLayout
{
    readonly FrameworkElement _page, _list, _detail;
    readonly Grid _body;
    readonly Thumb _splitter;
    readonly SelectorBar _views;
    bool _compact, _showDetail;
    double _wideLeft;

    internal sealed record ViewState(bool Details, double ListWidth);
    public ViewState Capture() => new(_showDetail,
        !_compact && _body.ColumnDefinitions[0].Width.IsAbsolute ? _body.ColumnDefinitions[0].Width.Value : _wideLeft);
    public void Restore(ViewState state)
    {
        _showDetail = state.Details;
        _wideLeft = state.ListWidth;
        // Restore selection before first layout; its initial SelectionChanged must not reset the tab.
        _views.SelectedItem = _views.Items[_showDetail ? 1 : 0];
        if (!_compact) _body.ColumnDefinitions[0].Width = new GridLength(_wideLeft);
        Arrange();
    }

    public ReviewLayout(FrameworkElement page, Grid body, FrameworkElement list, FrameworkElement detail,
        Thumb splitter, SelectorBar views)
    {
        _page = page; _body = body; _list = list; _detail = detail; _splitter = splitter; _views = views;
        _wideLeft = body.ColumnDefinitions[0].Width.Value;
        ColumnSplitter.Attach(splitter);
        page.SizeChanged += (_, _) => Arrange();
        page.Loaded += (_, _) => Arrange();
        views.SelectionChanged += (_, _) =>
        {
            var show = views.SelectedItem == views.Items[1];
            if (_showDetail == show) return;
            _showDetail = show;
            Arrange();
        };
    }

    public void ShowDetails()
    {
        if (!_compact) return;
        _showDetail = true;
        Arrange();
    }

    void Arrange()
    {
        // Child grids can report their columns' minimum widths even when clipped. The page tells
        // us how much space is actually available, so those minimums cannot prevent compact mode.
        var width = _page.ActualWidth;
        if (width <= 0) return;
        var left = _body.ColumnDefinitions[0];
        var splitter = _body.ColumnDefinitions[1];
        var right = _body.ColumnDefinitions[2];
        if (!_compact && left.Width.IsAbsolute) _wideLeft = left.Width.Value;
        _compact = width < 900;
        left.MinWidth = _compact ? 0 : 320;
        right.MinWidth = _compact ? 0 : 400;
        left.Width = _compact ? new GridLength(1, GridUnitType.Star)
            : new GridLength(Math.Clamp(_wideLeft, 320, Math.Max(320, width - 410)));
        right.Width = _compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        splitter.Width = new GridLength(_compact ? 0 : 10);
        _splitter.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        _views.Visibility = _compact ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(_detail, _compact ? 0 : 2);
        _list.Visibility = !_compact || !_showDetail ? Visibility.Visible : Visibility.Collapsed;
        _detail.Visibility = !_compact || _showDetail ? Visibility.Visible : Visibility.Collapsed;
        var selected = _views.Items[_showDetail ? 1 : 0];
        if (_views.SelectedItem != selected) _views.SelectedItem = selected;
    }
}
