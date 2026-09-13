using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>One count on a report: a chip with its number, and the words in its tooltip. A zero is not shown.</summary>
public sealed class ReportCount
{
    public ReportCount(ChipSeverity severity, string glyph, int count, string tip)
    {
        Severity = severity;
        Glyph = glyph;
        Count = count;
        Tip = tip;
    }

    public ChipSeverity Severity { get; }
    public string Glyph { get; }
    public int Count { get; }
    public string Tip { get; }
}

/// <summary>One thing an operation did or could not do: its chip, its name, what it was, and why when it went wrong.</summary>
public sealed class ReportRow
{
    public ReportRow(ChipSeverity severity, string glyph, string name, string what, string detail, string tip)
    {
        Severity = severity;
        Glyph = glyph;
        Name = name;
        What = what;
        Detail = detail;
        Tip = tip;
    }

    public ChipSeverity Severity { get; }
    public string Glyph { get; }
    public string Name { get; }
    public string What { get; }
    public string Detail { get; }
    public string Tip { get; }
}

/// <summary>
/// How an operation went, on the page that ran it. The footer line said "backup: done" over a backup whose
/// every branch had failed; this says it on the page, in the colour of the outcome, with a row per thing
/// that did not go and the reason on that row. One control, so every operation reports the same way.
/// </summary>
public sealed partial class ReportCard : UserControl
{
    public ReportCard()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The operation is running: a ring where the badge goes, and nothing from the last run.</summary>
    public void Running(string headline, string detail = "")
    {
        Ring.IsActive = true;
        Ring.Visibility = Visibility.Visible;
        Chip.Visibility = Visibility.Collapsed;
        Headline.Text = headline;
        SetDetail(detail);
        Counts.ItemsSource = null;
        RowList.ItemsSource = null;
        RowsCard.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    /// <summary>How it went. Counts of zero are left out, and the rows card only shows when there are rows.</summary>
    public void Show(ChipSeverity severity, string glyph, string headline, string detail,
        IEnumerable<ReportCount>? counts = null, IEnumerable<ReportRow>? rows = null)
    {
        Ring.IsActive = false;
        Ring.Visibility = Visibility.Collapsed;
        Chip.Visibility = Visibility.Visible;
        Chip.Severity = severity;
        Chip.Glyph = glyph;
        Headline.Text = headline;
        SetDetail(detail);
        Counts.ItemsSource = (counts ?? []).Where(c => c.Count > 0).ToList();
        var list = (rows ?? []).ToList();
        RowList.ItemsSource = list;
        RowsCard.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    /// <summary>A close button in the corner. For a report of one operation; a standing state, like the last backup, has none.</summary>
    public bool Closable
    {
        get => CloseButton.Visibility == Visibility.Visible;
        set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The reader closed it.</summary>
    public event Action? Closed;

    void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        Closed?.Invoke();
    }

    void SetDetail(string detail)
    {
        DetailText.Text = detail;
        DetailText.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
