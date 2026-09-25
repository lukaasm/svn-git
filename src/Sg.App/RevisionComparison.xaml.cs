using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

public sealed record RevisionComparisonRow(string Where, string Url, string Saved, string Local, bool Differs)
{
    public Brush LocalBrush => (Brush)Application.Current.Resources[
        Differs ? "StatusModifiedBrush" : "TextFillColorSecondaryBrush"];
    public override string ToString() => $"{Where}: {Saved} → {Local}. {Url}";
}

/// <summary>One cancellable destination preview, shared by file import and backup restore.</summary>
public sealed partial class RevisionComparison : UserControl
{
    readonly PageReads _reads = new();
    sealed record Source(SgRoot Root, ExportMeta Meta, CheckoutConfig? Checkout, StatusStrip Pane);
    Source? _source;
    public bool Ready { get; private set; }
    public string Reason { get; private set; } = "Choose a destination checkout to compare revisions.";
    public event Action? Changed;

    public RevisionComparison()
    {
        InitializeComponent();
        Unloaded += (_, _) => Clear();
    }

    public void Clear()
    {
        _reads.Cancel();
        _source = null;
        Ready = false;
        Reading.Hide();
        Visibility = Visibility.Collapsed;
    }

    public void Show(SgRoot root, ExportMeta meta, CheckoutConfig? checkout, StatusStrip pane)
    {
        _source = new(root, meta, checkout, pane);
        _ = CompareAsync(_source);
    }

    async Task CompareAsync(Source source)
    {
        using var read = _reads.Begin();
        Ready = false;
        Visibility = Visibility.Visible;
        ReadError.IsOpen = DriftBar.IsOpen = false;
        var checkout = source.Checkout;
        Reason = checkout == null ? "Choose a destination checkout to compare revisions."
            : $"Comparing SVN revisions with {checkout.Name}…";
        ComparisonSummary.Text = Reason;
        ShowRows(source.Meta, [], checkout == null ? "Choose checkout" : "Checking…");
        Changed?.Invoke();
        if (checkout == null) { Reading.Hide(); return; }

        Reading.Show(Reason, placeholders: false);
        var drift = await read.Run(source.Pane, () => Export.DriftOf(source.Root, source.Meta, checkout),
            error => ReadError.Message = error);
        if (!read.Current || source.Root != Session.Root) return;
        Reading.Hide();
        if (drift == null)
        {
            Reason = "Retry the revision comparison before continuing.";
            ReadError.IsOpen = true;
            ShowRows(source.Meta, [], "Not checked");
        }
        else
        {
            ShowRows(source.Meta, drift);
            Ready = true;
            Reason = source.Meta.Bases.Count == 0 ? "No saved SVN revisions to compare."
                : $"{checkout.Name} · {source.Meta.Bases.Count} working cop{(source.Meta.Bases.Count == 1 ? "y" : "ies")} · "
                    + (drift.Count == 0 ? "Revisions match" : drift.Count == 1 ? "1 differs" : $"{drift.Count} differ");
            DriftBar.IsOpen = drift.Count > 0;
            DriftBar.Message = drift.Any(d => d.Elsewhere)
                ? "A working copy points at another branch of its repository. The revisions are not directly comparable, and replaying commits may produce a large merge."
                : "Commits will be replayed across these revision differences. Review the result before pushing to SVN.";
        }
        ComparisonSummary.Text = Reason;
        Changed?.Invoke();
    }

    void ShowRows(ExportMeta meta, List<ExportDrift> drift, string? pending = null)
    {
        var byPath = new Dictionary<string, ExportDrift>(StringComparer.Ordinal);
        foreach (var item in drift) byPath.TryAdd(item.Where, item);
        BasesCard.Visibility = meta.Bases.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Bases.ItemsSource = meta.Bases.Select(b =>
        {
            byPath.TryGetValue(b.Where, out var d);
            return new RevisionComparisonRow(b.Where, d?.LocalUrl ?? b.Url, "Saved r" + b.Revision,
                pending ?? (d == null ? "Here r" + b.Revision : d.Elsewhere ? "Another branch"
                    : d.Missing ? "Not here" : "Here r" + d.Local), d != null);
        }).ToList();
    }

    void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_source is { } source) _ = CompareAsync(source);
    }
}
