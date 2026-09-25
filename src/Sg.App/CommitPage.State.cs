namespace Sg.App;

public sealed partial class CommitPage
{
    sealed class PublishedDraft { public int Version; }
    sealed record ViewState(ListFilter.ViewState Files, Dictionary<string, bool> Checked, string Message,
        bool Amend, string Head, bool Staged, string? SideOf, ReviewLayout.ViewState Layout,
        PublishedDraft Published, int Version);
    ViewState? _returning;
    PublishedDraft _published = new();
    int _draftVersion;
    string _head = "";
    bool _hasStatus, _hidden, _reading = true;
    readonly PageReads _reads = new(), _countReads = new();

    internal override object? CaptureViewState()
    {
        ClearPublishedDraft();
        var view = _returning ?? new ViewState(_filter.CaptureView(),
            _rows.ToDictionary(r => r.Entry.Path, r => r.Checked, StringComparer.Ordinal), Message.Text,
            Amend.IsChecked == true, _head, _showStaged, _sideOf, _layout.Capture(), _published, _draftVersion);
        // Filters and messages can still change while a fresh status read is pending.
        return view with { Files = view.Files with { Query = Filter.Text }, Message = Message.Text,
            Amend = Amend.IsChecked == true, Layout = _layout.Capture() };
    }

    internal override void RestoreViewState(object? state)
    {
        if (state is not ViewState view) return;
        _published = view.Published;
        _draftVersion = _published.Version;
        // A commit can finish after navigation captured this entry. Never bring its sent message back.
        if (view.Version < _published.Version)
            view = view with { Message = "", Amend = false, Checked = new(StringComparer.Ordinal), Version = _draftVersion };
        _returning = view;
        Filter.Text = view.Files.Query;
        Message.Text = view.Message;
        Amend.IsChecked = view.Amend;
        _showStaged = view.Staged;
        _sideOf = view.SideOf;
        _layout.Restore(view.Layout);
        SyncCommitButton();
    }

    public override void OnShown(bool returning) { _hidden = false; _ = LoadAsync(); }
    public override void OnHidden()
    {
        _hidden = true;
        ++_generation;
        ++_diffRequest;
        _reads.Cancel();
        _countReads.Cancel();
    }

    void SyncMessageMode()
    {
        Message.Title = Amend.IsChecked == true ? "Amend the last commit" : "Commit";
        Message.PrimaryButtonText = Amend.IsChecked == true ? "Amend" : "Commit";
    }

    void ClearPublishedDraft()
    {
        if (_draftVersion == _published.Version) return;
        _draftVersion = _published.Version;
        Message.Text = "";
        Amend.IsChecked = false;
        if (_returning != null) _returning = _returning with
            { Message = "", Amend = false, Checked = new(StringComparer.Ordinal), Version = _draftVersion };
        SetAll(false);
        SyncMessageMode();
    }
}
