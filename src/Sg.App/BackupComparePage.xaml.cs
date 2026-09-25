using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

public sealed partial class BackupComparePage : SgPage
{
    readonly string _name;
    BackupCatalog? _catalog;
    BackupComparison? _comparison;
    readonly PageReads _reads = new(), _patchReads = new();
    readonly CommitFilter _filter;
    readonly ListViewport _localViewport, _remoteViewport;
    List<CommitRow> _local = [], _remote = [];
    bool _binding, _hidden, _selectedRemote;
    CommitRow? _selected;
    DiffView? _diff;
    sealed record ViewState(string Query, string? Sha, bool Remote, BrowseScroll.Anchor? Local, BrowseScroll.Anchor? Backup);
    ViewState? _returning;

    public BackupComparePage(string name, BackupCatalog? catalog = null)
    {
        _name = name; _catalog = catalog;
        InitializeComponent();
        Title = "Compare backup"; Branch = name; Subtitle = name;
        _filter = new(Search, this);
        _localViewport = new(LocalCommits, item => ((CommitRow)item).Sha);
        _remoteViewport = new(RemoteCommits, item => ((CommitRow)item).Sha);
        _filter.Changed += () => { if (IsLoaded && _comparison != null) Filter(); };
        Unloaded += (_, _) => OnHidden();
    }
    public override void OnShown(bool returning) { _hidden = false; _ = LoadAsync(); }
    public override void OnHidden() { _hidden = true; _reads.Cancel(); _patchReads.Cancel(); }
    internal override object? CaptureViewState() => CaptureView();
    ViewState CaptureView() => (_returning ?? new ViewState(Search.Text, _selected?.Sha, _selectedRemote,
        _localViewport.Capture(), _remoteViewport.Capture())) with { Query = Search.Text };
    internal override void RestoreViewState(object? state)
    {
        if (state is ViewState view) { _returning = view; Search.Text = view.Query; }
    }
    async void Refresh_Click(object sender, RoutedEventArgs e) { _returning = CaptureView(); _catalog = null; await LoadAsync(); }
    async Task LoadAsync()
    {
        var root = Session.Require();
        using var read = _reads.Begin();
        _comparison = null; ClearPatch();
        LocalHeader.Text = "Local history"; RemoteHeader.Text = "Backup history";
        LocalBase.Text = RemoteBase.Text = "";
        _local = []; _remote = [];
        _binding = true; LocalCommits.ItemsSource = null; RemoteCommits.ItemsSource = null; _binding = false;
        LocalEmpty.Visibility = RemoteEmpty.Visibility = Visibility.Collapsed;
        LocalLoading.Show(); RemoteLoading.Show(); LocalHistory.IsEnabled = false;
        ReadError.IsOpen = false; ComparedAt.Text = "";
        ComparisonReading.Running("Reading the selected histories…", "You can keep browsing. No worktree files are scanned or changed.");
        var data = await read.Run(Pane, () =>
        {
            var catalog = _catalog ?? Backup.Browse(root);
            var item = catalog.Items.FirstOrDefault(i => i.Kind == "branch" && i.Name == _name)
                ?? throw new SgException("This worktree no longer has a remote backup. Return to Backup to choose another item.");
            return Backup.Compare(root, catalog, item);
        }, error => ReadError.Message = error);
        if (!read.Current || _hidden || root != Session.Root) return;
        ComparisonReading.Hide(); LocalLoading.Hide(); RemoteLoading.Hide();
        if (data == null) { ReadError.IsOpen = true; return; }
        _comparison = data;
        _local = Rows(data.Local); _remote = Rows(data.Remote);
        LocalBase.Text = Basis(data.Local); RemoteBase.Text = Basis(data.Remote);
        ToolTipService.SetToolTip(LocalBase, Bases(data.Local)); ToolTipService.SetToolTip(RemoteBase, Bases(data.Remote));
        ComparedAt.Text = $"{_name} · Compared {data.Checked.LocalDateTime:g} · Refresh to check for newer work";
        LocalHistory.IsEnabled = data.LocalPath != null;
        Filter();
        if (_returning is { Sha: { } sha } view)
        {
            var list = view.Remote ? RemoteCommits : LocalCommits;
            var row = ((IEnumerable<CommitRow>)list.ItemsSource).FirstOrDefault(c => c.Sha == sha);
            if (row != null) list.SelectedItem = row;
        }
        _localViewport.Restore(_returning?.Local); _remoteViewport.Restore(_returning?.Backup);
        _returning = null;
    }
    static List<CommitRow> Rows(BackupHistory? history) => history?.Commits.Select(c => CommitRow.From(
        c with { Date = DateTimeOffset.TryParse(c.Date, out var when) ? when.LocalDateTime.ToString("yyyy-MM-dd") : c.Date }, false)).ToList() ?? [];
    static string Basis(BackupHistory? history) => history == null ? "No local branch" :
        $"SVN r{history.Bases.FirstOrDefault(b => b.Rel.Length == 0)?.Revision} · {history.Commits.Count} {(history.Commits.Count == 1 ? "commit" : "commits")} · {history.Tip[..8]}";
    static string Bases(BackupHistory? history) => history == null ? "Restore this backup to create a local worktree." :
        string.Join("\n", history.Bases.Select(b => $"{(b.Rel.Length == 0 ? "Checkout" : b.Rel)} · r{b.Revision} · {b.Url}"));

    void Filter()
    {
        var local = _filter.Apply(_local); var remote = _filter.Apply(_remote);
        _binding = true;
        LocalCommits.ItemsSource = local; RemoteCommits.ItemsSource = remote;
        var selectedList = _selectedRemote ? RemoteCommits : LocalCommits;
        if (_selected != null && (_selectedRemote ? remote : local).Contains(_selected)) selectedList.SelectedItem = _selected;
        else ClearPatch();
        _binding = false;
        LocalHeader.Text = $"Local · {local.Count} of {_local.Count}"; RemoteHeader.Text = $"Backup · {remote.Count} of {_remote.Count}";
        LocalEmpty.Text = _filter.Active ? "No local commits match this search." : _comparison?.Local == null ? "No local branch. Restore options can create one." : "No local commits above the SVN snapshot.";
        RemoteEmpty.Text = _filter.Active ? "No saved commits match this search." : "This backup contains its SVN snapshot marker only.";
        LocalEmpty.Visibility = local.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoteEmpty.Visibility = remote.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    async void Commit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || sender is not ListView list || list.SelectedItem is not CommitRow row) return;
        _binding = true;
        _selectedRemote = ReferenceEquals(list, RemoteCommits);
        (_selectedRemote ? LocalCommits : RemoteCommits).SelectedItem = null;
        _binding = false;
        _selected = row;
        await LoadPatchAsync();
    }
    async void RetryPatch_Click(object sender, RoutedEventArgs e) => await LoadPatchAsync();
    async Task LoadPatchAsync()
    {
        if (_comparison is not { } comparison || _selected is not { } selected || _hidden) return;
        using var read = _patchReads.Begin();
        var root = Session.Require(); var remote = _selectedRemote;
        ChooseCommit.Visibility = Visibility.Collapsed; DiffHost.Visibility = Visibility.Collapsed;
        PatchError.IsOpen = false; PatchLoading.Show();
        PatchTitle.Text = $"{(remote ? "Backup" : "Local")} · {selected.ShortSha} · {selected.Subject}";
        var patch = await read.Run(Pane, () => Backup.ComparisonPatch(root, comparison, remote, selected.Sha), error => PatchError.Message = error);
        if (!read.Current || _hidden || root != Session.Root) return;
        PatchLoading.Hide();
        if (patch == null) { PatchError.IsOpen = true; return; }
        // Creating the editor is deferred until a commit is selected; simply comparing histories stays cheap.
        if (_diff == null) { _diff = new(); DiffHost.Content = _diff; Shortcuts.DiffNavigation(this, _diff); }
        DiffHost.Visibility = Visibility.Visible;
        if (patch.Length == 0) _diff.ShowText("", "This commit changes no files.");
        else _diff.ShowUnified(patch, PatchTitle.Text);
    }
    void ClearPatch()
    {
        _patchReads.Cancel(); _selected = null;
        PatchLoading.Hide(); PatchError.IsOpen = false; DiffHost.Visibility = Visibility.Collapsed;
        ChooseCommit.Visibility = Visibility.Visible; PatchTitle.Text = "Select a commit to inspect its changes";
    }
    void LocalHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_comparison?.LocalPath is { } path) DispatcherQueue.TryEnqueue(() => Go(() => new LogPage(path), "log:" + path));
    }
    void Restore_Click(object sender, RoutedEventArgs e) => Go(() => new BackupPage(_name), "backup:branch:" + _name);
}
