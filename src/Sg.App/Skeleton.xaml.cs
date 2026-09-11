using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Sg.App;

/// <summary>
/// Grey bars that pulse where a list is about to appear. A window shows it while it reads, and hides it
/// when the rows arrive, so an empty card never sits there looking finished.
/// </summary>
public sealed partial class Skeleton : UserControl
{
    public static readonly DependencyProperty RowCountProperty = DependencyProperty.Register(
        nameof(RowCount), typeof(int), typeof(Skeleton), new PropertyMetadata(6, (d, _) => ((Skeleton)d).Build()));

    /// <summary>How many bars. Roughly the rows the list will have, so the height does not jump.</summary>
    public int RowCount
    {
        get => (int)GetValue(RowCountProperty);
        set => SetValue(RowCountProperty, value);
    }

    public static readonly DependencyProperty BadgesProperty = DependencyProperty.Register(
        nameof(Badges), typeof(bool), typeof(Skeleton), new PropertyMetadata(true, (d, _) => ((Skeleton)d).Build()));

    /// <summary>A small square before each bar, where a file row has its status letter.</summary>
    public bool Badges
    {
        get => (bool)GetValue(BadgesProperty);
        set => SetValue(BadgesProperty, value);
    }

    static readonly double[] Widths = { 0.82, 0.58, 0.71, 0.47, 0.66, 0.53, 0.77, 0.42 };

    static double RowHeight => (double)Application.Current.Resources["RowMinHeight"];
    static double Spacing => (double)Application.Current.Resources["RowSpacing"];

    public Skeleton()
    {
        InitializeComponent();
        Build();
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Pulse.Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Sync());
    }

    public void Show() => Visibility = Visibility.Visible;
    public void Hide() => Visibility = Visibility.Collapsed;

    void Sync()
    {
        if (Visibility == Visibility.Visible && IsLoaded) Pulse.Begin();
        else Pulse.Stop();
    }

    void Build()
    {
        Rows.Children.Clear();
        var brush = (Brush)Application.Current.Resources["SkeletonBrush"];
        for (var i = 0; i < RowCount; i++)
        {
            var width = Widths[i % Widths.Length];
            // One bar per row, at the height and spacing a real row has, so nothing shifts when they arrive.
            var row = new Grid { ColumnSpacing = Spacing, Height = RowHeight };
            if (Badges)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new Border { Width = 24, Height = 18, CornerRadius = new CornerRadius(4), Background = brush });
            }
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - width, GridUnitType.Star) });
            var bar = new Border { Height = 12, CornerRadius = new CornerRadius(4), Background = brush, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(bar, Badges ? 1 : 0);
            row.Children.Add(bar);
            Rows.Children.Add(row);
        }
    }
}
