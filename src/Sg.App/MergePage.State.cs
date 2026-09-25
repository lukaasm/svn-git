using Microsoft.UI.Xaml;
using Sg.Core;

namespace Sg.App;

public sealed partial class MergePage
{
    sealed record RevisionKey(string Wc, string Source, long Revision);
    sealed record ViewState(string? TargetWc, string? TargetUrl, string? SourceUrl, RevisionKey[] Picked,
        bool MergedOpen, ReviewLayout.ViewState Layout);
    ViewState? _returning;
    RevisionKey[]? _missingSelection;
    readonly PageReads _reads = new();
    bool _hidden;

    static RevisionKey Key(MergeRevision revision) => new(revision.Pair.Target.Wc, revision.Pair.SourceUrl, revision.Revision);
    internal override object? CaptureViewState() => (_returning ?? new ViewState(Target?.Wc, Target?.Url,
        Source?.Url, _missingSelection ?? Picked().Select(Key).ToArray(), _mergedOpen, _layout.Capture()))
        with { Layout = _layout.Capture() };
    internal override void RestoreViewState(object? state)
    {
        if (state is not ViewState view) return;
        _returning = view;
        _mergedOpen = view.MergedOpen;
        _layout.Restore(view.Layout);
    }
    public override void OnShown(bool returning) { _hidden = false; _ = LoadTargetsAsync(); }
    public override void OnHidden()
    {
        _hidden = true;
        ++_generation;
        _reads.Cancel();
    }

    void SelectionUnavailable(string message)
    {
        SelectionNotice.Message = message;
        SelectionNotice.IsOpen = true;
    }

    void RestoreRevisions(ViewState view)
    {
        var keys = view.Picked.ToHashSet();
        var rows = _offered.Where(p => keys.Contains(Key(p.Value))).Select(p => p.Key).ToList();
        _missingSelection = rows.Count == keys.Count ? null : view.Picked;
        if (rows.Any(r => r.Merged)) { _mergedOpen = true; ShowRevisions(); }
        _binding = true;
        foreach (var row in rows) Revisions.SelectedItems.Add(row);
        _binding = false;
        if (_missingSelection != null)
            SelectionUnavailable("Some previously selected revisions are no longer available. Choose revisions again, or choose Use all revisions explicitly.");
        else SelectionNotice.IsOpen = false;
        _layout.Restore(view.Layout);
        SyncButtons();
        ShowPickedText();
    }

    void ForgetSelection()
    {
        _returning = null;
        _missingSelection = null;
        SelectionNotice.IsOpen = false;
    }

    void CheckoutChanges_Click(object sender, RoutedEventArgs e)
    {
        AdvancedFlyout.Hide();
        Go(() => new SvnCommitPage(_co) { Checkout = Checkout }, "changes:" + _co.Name);
    }
}
