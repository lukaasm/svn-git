using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

/// <summary>A catalog row whose metadata arrives when selected.</summary>
public sealed class BackupRow : System.ComponentModel.INotifyPropertyChanged
{
    public required BackupReference Reference { get; init; }
    public BackupEntry? Entry { get; private set; }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public void Loaded(BackupEntry entry) { Entry = entry; PropertyChanged?.Invoke(this, new(null)); }
    public string Name => Reference.Name;

    public string What => Entry == null ? Reference.Kind switch { "branch" => Reference.HasWip ? "Branch + edits" : "Branch", "wip" => "Uncommitted edits", "edits" => "Checkout edits", _ => "Shelf" }
        : Entry.Unreadable != null ? "cannot be read"
        : Entry.Kind == "branch" ? (Entry.Commits == 1 ? "1 commit" : Entry.Commits + " commits") + (Entry.HasWip ? " + wip" : "")
        : Entry.Kind == "wip" ? "uncommitted changes of " + Entry.Branch
        : Entry.Kind == "edits" ? "local edits of checkout " + Entry.Branch
        : "shelf \"" + Entry.Title + "\"" + (Entry.Branch.Length > 0 ? " of " + Entry.Branch : "");

    public string From => Entry == null ? "Select to load preview" : Entry.Unreadable ?? $"from {(Entry.Checkout.Length > 0 ? Entry.Checkout : Entry.Url)} r{Entry.Revision}"
                                             + (Entry.Last is { } t ? $"   {t.LocalDateTime:yyyy-MM-dd HH:mm}" : "");

    /// <summary>What stands between this and a restore, in two words: it is here already, or the checkout moved on.</summary>
    public string Note => Entry == null ? Reference.Excluded ? "excluded here" : Reference.ExistsHere ? "here" : ""
        : Entry.Unreadable != null ? "" : Entry.Excluded ? "excluded here" : Entry.ExistsHere ? "here" : Entry.Drift.Count > 0 ? "checkout moved on" : Entry.Checkout.Length == 0 ? "no checkout matches" : "";

    public Brush NoteBrush => (Brush)Application.Current.Resources[
        (Entry?.ExistsHere ?? Reference.ExistsHere) || (Entry?.Excluded ?? Reference.Excluded) || Entry?.Unreadable != null ? "TextFillColorTertiaryBrush" : Note.Length > 0 ? "StatusModifiedBrush" : "TextFillColorSecondaryBrush"];

    public string Tip => Entry == null ? Reference.Name + " · Select to read its saved version." : Entry.Unreadable
        ?? (Entry.Excluded ? "The worktree is excluded from the backup on this machine, so this is what it sent before, or another machine's copy. No backup writes over it; Prune lists it.\n" : "")
         + (Entry.Url.Length > 0 ? Entry.Url + " r" + Entry.Revision : Entry.Name);
}

/// <summary>
/// What the backup repository holds, read before anything is made. Pick a branch, read what it was cut
/// from beside what this checkout is at, and only then is Restore offered. Back up now and Prune live
/// here too, because they are about the same place.
/// </summary>
public sealed partial class BackupPage : SgPage
{
    readonly OperationForm<RestoreRequest> _form;
    readonly string? _itemName;
    readonly string _itemKind;
    readonly bool _separate;
    bool Overview => _itemName == null;
    string? Worktree => _itemKind == "branch" ? _itemName : null;
    IReadOnlyList<BackupWorktree> _worktrees = [];
    const int WorktreePageSize = 20;
    List<BackupWorktree> _matchingWorktrees = [];
    int _shownWorktrees = WorktreePageSize;
    int _otherMatches;
    readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    List<BackupRow> _entries = new();
    BackupCatalog? _catalog;
    BackupPreview? _preview;
    BackupRow? _selectedRow;
    BackupEntry? _picked;
    bool _binding;
    readonly PageReads _reads = new();
    readonly PageReads _previewReads = new();
    readonly UiRefresh _searchRefresh;
    bool _hidden;
    readonly BranchTargetValidation _targetValidation = new();
    sealed record RestoreDraft(DestinationDraft Destination, bool WithEdits, bool Replace);
    sealed record ViewState(string Destination, string Query, int Filter, int Shown, double Offset, string[] Expanded,
        RestoreDraft? Restore, bool Advanced);
    RestoreDraft? _draft;
    ViewState? _returning;
    bool _restoring;

    public BackupPage(string? itemName = null, string itemKind = "branch", bool separate = false)
    {
        _itemName = itemName; _itemKind = itemKind; _separate = separate;
        InitializeComponent();
        _searchRefresh = new(DispatcherQueue, () => { if (IsLoaded) FilterEntries(); }, TimeSpan.FromMilliseconds(150));
        _form = new(NameBox, IntoBox, ForceBox, WipBox);
        RevisionPreview.Changed += SyncButton;
        Unloaded += (_, _) => OnHidden();
        Title = "Backup";
        Branch = Worktree;
        var overview = Overview ? Visibility.Visible : Visibility.Collapsed;
        ScheduleOverview.Visibility = BackupToolbar.Visibility = BackupMatches.Visibility = BranchesHeader.Visibility = Branches.Visibility = BackupActions.Visibility = overview;
        // The overview is a place, like the checkout page: the back button leaves it. Close stays on a
        // worktree's page, where it is the other half of Restore.
        LeaveButton.Visibility = Overview ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => { Session.Tasks.StateChanged += GateAll; GateAll(); };
        Unloaded += (_, _) => Session.Tasks.StateChanged -= GateAll;
        BackupNowButton.Visibility = PruneButton.Visibility = Worktree != null ? Visibility.Visible : Visibility.Collapsed;
        AdvancedOptions.Visibility = Worktree != null ? Visibility.Visible : Visibility.Collapsed;
        Session.Log.Sink = Pane;
    }
    public override void OnShown(bool returning) { _hidden = false; _ = LoadAsync(); }

    bool _configured;

    /// <summary>
    /// The menu's items cannot sit in a task gate, so they take its rule here: off while a task holds the
    /// root, and saying which one, as the checkout's own menu does.
    /// </summary>
    void GateAll()
    {
        if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(GateAll); return; }
        var busy = Session.Tasks.Blocking(Session.Root?.RootPath ?? "");
        BackupAllButton.IsEnabled = PruneAllButton.IsEnabled = _configured && busy == null;
        TaskGate.SetHelp(BackupAllButton, busy?.BlockingExplanation
            ?? "Send every included worktree: its branch, its shelves, and its uncommitted changes when Settings allow.");
        TaskGate.SetHelp(PruneAllButton, busy?.BlockingExplanation
            ?? "List the backup refs no local work answers to. Nothing is deleted before you confirm the list.");
    }
    string Destination => Session.Root?.Config.Backup is { } cfg ? cfg.Url + "\n" + cfg.Prefix : "";
    internal override object? CaptureViewState() => new ViewState(Destination, BackupSearch.Text, BackupFilter.SelectedIndex,
        _shownWorktrees, _returning?.Offset ?? ContentScroll.VerticalOffset, _expanded.ToArray(), CurrentDraft(), AdvancedOptions.IsExpanded);
    RestoreDraft? CurrentDraft() => _picked is { Kind: "branch", Unreadable: null }
        ? new(DestinationDraft.Capture(NameBox.Text, Into), WipBox.IsChecked == true, !_form.Running && ForceBox.IsChecked == true)
        : _draft;
    internal override void RestoreViewState(object? state)
    {
        if (state is not ViewState view || view.Destination != Destination) return;
        _returning = view; _restoring = true;
        _draft = view.Restore;
        AdvancedOptions.IsExpanded = view.Advanced;
        BackupSearch.Text = view.Query;
        BackupFilter.SelectedIndex = view.Filter;
        _shownWorktrees = view.Shown;
        _expanded.UnionWith(view.Expanded);
        _restoring = false;
    }

    CheckoutConfig? Into => IntoBox.SelectedItem is string name ? Session.Root?.Checkout(name) : null;

    async Task LoadAsync()
    {
        if (_hidden) return;
        _draft = CurrentDraft();
        using var read = _reads.Begin();
        _previewReads.Cancel();
        _catalog = null; _entries.Clear(); _preview = null; _selectedRow = null;
        AppearancePreview.Hide();
        RevisionPreview.Clear();
        PreviewLoading.Hide(); PreviewError.IsOpen = false; PreviewTitle.Visibility = Visibility.Collapsed;
        ReadingBackup.Hide();
        ReadError.IsOpen = false;
        _targetValidation.Invalidate();
        ExistingDestination.Update(null, null);
        _picked = null;
        Summary.Text = "";
        RestoreButton.Visibility = Visibility.Collapsed;
        RestoreButton.IsEnabled = false;
        var root = Session.Require();
        var cfg = root.Config.Backup;
        var configured = Backup.Configured(root);
        NoBackup.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        Filled.Visibility = Nothing.Visibility = Visibility.Collapsed;
        RestoreRow.Visibility = Visibility.Collapsed;
        RestoreOptions.Visibility = Visibility.Collapsed;
        AdvancedOptions.Visibility = configured && Worktree != null ? Visibility.Visible : Visibility.Collapsed;
        BackupNowButton.IsEnabled = false;
        PruneButton.IsEnabled = _configured = configured;
        GateAll();
        RestoreButton.IsEnabled = false;
        if (!configured)
        {
            LastReport.Hide();
            Subtitle = "no backup repository";
            Summary.Text = "Set the URL in Settings first.";
            return;
        }
        Subtitle = cfg!.Url;
        // The URL is under the page title already. The card says only what the title does not: the prefix, when
        // there is one, and the worktrees left out, so a branch missing from the list is not read as lost.
        var notes = new List<string>();
        if (cfg.Prefix.Length > 0) notes.Add("everything under " + cfg.Prefix + "/");
        if (cfg.Excluded.Count > 0) notes.Add("left out here: " + string.Join(", ", cfg.Excluded));
        UrlText.Text = string.Join(". ", notes);
        UrlText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Before the remote is read: how the last run went is known here, and is worth seeing even when the remote cannot be reached now.
        ShowPageReport(root);
        // The overview shows what the backup held when it was last read in this session at once, with a
        // bar along the top while it is read again: the read is a round trip to the remote, a second or
        // more. A worktree's page reads it fresh, since restoring works from what is there now.
        var key = root.RootPath + "\n" + Destination;
        var seen = Overview && Seen.TryGetValue(key, out var last) ? last : null;
        if (seen != null)
        {
            await Render(root, seen.Catalog, seen.Worktrees);
            SetRefreshing(true);
        }
        else ReadingBackup.Running("Reading the backup repository…", "You can keep browsing. The saved report above remains available.");

        var data = await read.Run(Pane, () => { var catalog = Backup.Browse(root); return new BackupList(catalog, Backup.Worktrees(root, catalog)); },
            loading: seen == null);
        if (seen != null) SetRefreshing(false);
        if (!read.Current || root.RootPath != Session.Root?.RootPath) return;
        ReadingBackup.Hide();
        if (data == null)
        {
            ReadError.IsOpen = true;
            return;
        }
        Seen[key] = data;
        var offset = ContentScroll.VerticalOffset;
        await Render(root, data.Catalog, data.Worktrees);
        if (seen != null) BrowseScroll.Restore(ContentScroll, offset);
    }

    /// <summary>What a read of the backup repository found, as the overview lists it.</summary>
    sealed record BackupList(BackupCatalog Catalog, IReadOnlyList<BackupWorktree> Worktrees);

    /// <summary>The last list read from each root's backup repository in this session.</summary>
    static readonly Dictionary<string, BackupList> Seen = new(StringComparer.OrdinalIgnoreCase);

    int _refreshing;

    void SetRefreshing(bool on)
    {
        _refreshing += on ? 1 : -1;
        RefreshBar.IsIndeterminate = _refreshing > 0;
        Motion.FadeTo(RefreshBar, _refreshing > 0 ? 1 : 0);
    }

    async Task Render(SgRoot root, BackupCatalog catalog, IReadOnlyList<BackupWorktree> worktrees)
    {
        _catalog = catalog;
        _worktrees = worktrees;
        _entries = catalog.Items.Select(e => new BackupRow { Reference = e }).ToList();
        var others = _entries.Where(e => e.Reference.Kind != "branch").ToList();
        if (Overview && _entries.Count == 0 && _worktrees.Count == 0)
        {
            Nothing.Visibility = Visibility.Visible;
            Summary.Text = "";
            return;
        }
        Filled.Visibility = Visibility.Visible;
        var local = _worktrees.FirstOrDefault(w => w.Name == Worktree);
        BackupNowButton.IsEnabled = local?.CanBackUp == true;
        TaskGate.SetHelp(BackupNowButton, local?.CanBackUp == true ? $"Back up only {Worktree}, including its shelves and enabled uncommitted changes."
            : local?.Excluded == true ? "This worktree is excluded from backup. Include it in its worktree settings first." : "No local worktree folder is available to back up.");
        // What needs doing, not a census: the counts of everything are in the list's own line below.
        var attention = _worktrees.Count(w => w.NeedsAttention);
        Headline.Text = !Overview ? _itemName!
            : attention == 0 ? $"Every worktree is backed up ({_worktrees.Count})"
            : $"{attention} of {_worktrees.Count} worktrees need a backup";

        _binding = true;
        IntoBox.ItemsSource = root.Config.Checkouts.Select(c => c.Name).ToList();
        _binding = false;

        Show(null);
        if (Overview) FilterEntries(reset: false);
        else
        {
            var selected = _entries.FirstOrDefault(e => e.Reference.Kind == _itemKind && e.Name == _itemName);
            if (selected != null) { await PreviewAsync(selected); if (_separate && _draft == null) await SuggestSeparateNameAsync(); }
            else SelectionHint.Text = "No remote backup of this worktree is available. Back it up to create the first copy.";
        }
        if (_returning is { } view) { BrowseScroll.Restore(ContentScroll, view.Offset); _returning = null; }
    }

    public override void OnHidden() { _hidden = true; _reads.Cancel(); _previewReads.Cancel(); RevisionPreview.Clear(); _targetValidation.Invalidate(); }
    async void RetryRead_Click(object sender, RoutedEventArgs e) => await LoadAsync();
    void ReadLog_Click(object sender, RoutedEventArgs e) => OutputWindow.Show();

    void Search_Changed(object sender, TextChangedEventArgs e) { if (!_restoring) _searchRefresh?.Request(); }
    void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!_restoring) _searchRefresh?.Request(); }
    void FilterEntries(bool reset = true)
    {
        if (!Overview) return;
        var query = BackupSearch.Text.Trim();
        _previewReads.Cancel(); _preview = null; _selectedRow = null;
        PreviewLoading.Hide(); PreviewError.IsOpen = false; PreviewTitle.Visibility = Visibility.Collapsed;
        var matches = _entries.Where(e => query.Length == 0 || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _matchingWorktrees = _worktrees.Where(w => (BackupFilter.SelectedIndex switch
            { 1 => w.NeedsAttention, 2 => w.Remote == null, 3 => w.Path == null, _ => true })
            && (query.Length == 0 || w.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || w.Checkout?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)).ToList();
        var branchNames = _worktrees.Where(w => w.Remote != null).Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
        var others = matches.Where(e => e.Reference.Kind != "branch" && !(e.Reference.Kind == "wip" && branchNames.Contains(e.Name))).ToList();
        BranchesHeader.Text = "Worktrees";
        var take = reset ? WorktreePageSize : Math.Max(WorktreePageSize, _shownWorktrees);
        Branches.Children.Clear();
        _shownWorktrees = 0;
        _otherMatches = others.Count;
        AppendWorktrees(take);
        Others.ItemsSource = others;
        OthersCard.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Show(null);
    }

    void MoreWorktrees_Click(object sender, RoutedEventArgs e) => AppendWorktrees(WorktreePageSize);
    void AppendWorktrees(int count)
    {
        RenderWorktrees(_matchingWorktrees.Skip(_shownWorktrees).Take(count));
        _shownWorktrees = Branches.Children.Count;
        var remaining = _matchingWorktrees.Count - _shownWorktrees;
        MoreWorktrees.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreWorktrees.Text = $"Show {Math.Min(WorktreePageSize, remaining)} more";
        BackupMatches.Text = _matchingWorktrees.Count + _otherMatches == 0 ? "No matching backup items. Change the search or filter."
            : $"{_matchingWorktrees.Count} of {_worktrees.Count} worktrees · {_otherMatches} saved edits and shelves"
                + (remaining > 0 ? $" · Showing {_shownWorktrees}" : "");
    }

    void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        var row = (sender as ListView)?.SelectedItem as BackupRow;
        if (row == null) return;
        Go(() => new BackupPage(row.Name, row.Reference.Kind), "backup:" + row.Reference.Kind + ":" + row.Name);
    }
    async void RetryPreview_Click(object sender, RoutedEventArgs e) { if (_selectedRow is { } row) await PreviewAsync(row); }
    async Task PreviewAsync(BackupRow row)
    {
        if (_catalog is not { } catalog || _hidden) return;
        _draft = CurrentDraft();
        var root = Session.Require();
        using var read = _previewReads.Begin();
        _selectedRow = row; _preview = null;
        _targetValidation.Invalidate();
        Show(null);
        SelectionHint.Visibility = Visibility.Collapsed;
        PreviewTitle.Text = row.Name;
        PreviewTitle.Visibility = Overview ? Visibility.Visible : Visibility.Collapsed;
        PreviewError.IsOpen = false;
        PreviewLoading.Running("Loading saved version…", "Commits and SVN revisions are read only for the item you select.");
        var preview = await read.Run(Pane, () => Backup.Preview(root, catalog, row.Reference),
            error => PreviewError.Message = error);
        if (!read.Current || _hidden || !ReferenceEquals(_catalog, catalog) || root.RootPath != Session.Root?.RootPath) return;
        PreviewLoading.Hide();
        if (preview == null) { PreviewError.IsOpen = true; return; }
        _preview = preview;
        row.Loaded(preview.Entry);
        if (preview.Entry.Unreadable != null) { PreviewError.Message = preview.Entry.Unreadable; PreviewError.IsOpen = true; }
        Show(preview.Entry);
    }

    /// <summary>One branch on screen: its commits, its bases beside the ones here, and the restore controls filled for it.</summary>
    void Show(BackupEntry? entry)
    {
        _picked = entry;
        var has = entry != null && entry.Unreadable == null;
        RestoreRow.Visibility = has && entry!.Kind == "branch" ? Visibility.Visible : Visibility.Collapsed;
        RestoreOptions.Visibility = RestoreRow.Visibility;
        RestoreButton.Visibility = RestoreRow.Visibility;
        SelectionHint.Visibility = entry == null && SelectionHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CommitsHeader.Visibility = CommitsCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            ShowOptionSummary();
            AppearancePreview.Hide();
            RevisionPreview.Clear();
            SyncButton();
            return;
        }
        CommitsHeader.Text = entry!.Commits == 1 ? "Commit" : $"Commits ({entry.Commits})";
        Subjects.ItemsSource = entry.Commits == 0 ? new List<string> { "None: the branch equals its snapshot. Restoring it makes the worktree again, empty." } : entry.Subjects;
        _binding = true;
        NameBox.Text = _draft?.Destination.Branch ?? entry.Name;
        IntoBox.SelectedItem = _draft == null ? (entry.Checkout.Length > 0 ? entry.Checkout : null) : _draft.Destination.Resolve(Session.Require())?.Name;
        WipBox.IsChecked = entry.HasWip && (_draft?.WithEdits ?? true);
        WipBox.IsEnabled = entry.HasWip;
        TaskGate.SetHelp(WipBox, entry.HasWip ? "Restore the saved uncommitted changes after replaying commits."
            : "This backup has no saved uncommitted changes.");
        ForceBox.IsChecked = _draft?.Replace ?? false;
        _binding = false;
        ShowOptionSummary();
        ShowBases();
        SyncButton();
    }

    void ShowBases()
    {
        var entry = _picked;
        var co = Into;
        if (entry == null) return;
        AppearancePreview.Show(entry.HasAppearance, _preview?.AppearanceIcon, entry.Checkout, co);
        RevisionPreview.Show(Session.Require(), MetaOf(entry), co, Pane);
    }

    /// <summary>The entry as the export reader sees an export, so the drift is read by the same code.</summary>
    static ExportMeta MetaOf(BackupEntry e) => new() { Branch = e.Name, Bases = e.Bases };

    async void SyncButton()
    {
        if (_form == null || _form.Running || _hidden) return;
        var entry = _picked?.Kind == "branch" ? _picked : null;
        var name = NameBox.Text.Trim();
        var root = Session.Root;
        ExistingDestination.Update(null, null);
        RestoreButton.IsEnabled = false;
        if (name.Length > 0 && root != null && entry != null)
            ExplainTarget("Checking branch name and destination…");
        var check = await _targetValidation.CheckAsync(entry == null ? null : root, name);
        if (check == null) return;
        ExistingDestination.Update(root, check.Existing);
        var taken = check.Taken;
        var force = ForceBox.IsChecked == true;
        RestoreButton.IsEnabled = entry != null && entry.Unreadable == null && name.Length > 0 && Into != null && (!taken || force) && check.Error == null && RevisionPreview.Ready;
        var retry = entry != null && Into is { } checkout && _form.IsRetry(
            new RestoreRequest(entry.Name, name, checkout.Name, WipBox.IsChecked == true && entry.HasWip, force, _preview?.ExpectedRefs));
        RestoreLabel.Text = entry == null ? "Restore"
            : force && taken ? $"{(retry ? "Retry overwrite" : "Overwrite")} with {entry.Commits} commit(s)"
            : retry ? "Retry restore" : $"Restore {entry.Commits} commit(s)";
        ExplainTarget(entry == null ? ""
            : entry.Unreadable != null ? entry.Unreadable
            : Into == null && entry.Checkout.Length == 0 ? $"No checkout here points at {entry.Url}. Pick one only if you know it is the same repository."
            : Into == null ? "Pick the checkout to build it on."
            : name.Length == 0 ? "Give the branch a name."
            : check.Error != null ? check.Error
            : !RevisionPreview.Ready ? RevisionPreview.Reason
            : taken && !force ? $"{name} is already a branch here. Choose another name, or open Advanced to replace it."
            : taken ? $"{name} here is written over with the backup version. The original branch and worktree, including uncommitted changes, are preserved under a recovery name first."
            : $"{name} will be made on {Into.Name}, and its worktree with it.");
    }

    void ExplainTarget(string message)
    {
        Summary.Text = message;
        TaskGate.SetHelp(RestoreButton, message);
        AutomationProperties.SetHelpText(NameBox, message);
    }

    void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_binding) SyncButton();
    }

    void Force_Changed(object sender, RoutedEventArgs e)
    {
        if (_binding) return;
        ShowOptionSummary();
        SyncButton();
    }

    void ShowOptionSummary()
    {
        if (ReplaceNotice == null) return;
        ReplaceNotice.Visibility = _picked != null && ForceBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EditsNotice.Visibility = _picked?.HasWip == true ? Visibility.Visible : Visibility.Collapsed;
        EditsNotice.Text = WipBox.IsChecked == true ? "Saved edits included" : "Saved edits excluded";
    }

    void Into_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        ShowBases();
        SyncButton();
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_form.Running || !RevisionPreview.Ready) return;
        var entry = _picked;
        var co = Into;
        if (entry?.Kind != "branch" || entry.Unreadable != null || co == null || _preview == null) return;
        var name = NameBox.Text.Trim();
        var wip = WipBox.IsChecked == true && entry.HasWip;
        var root = Session.Require();
        var force = ForceBox.IsChecked == true;
        var request = new RestoreRequest(entry.Name, name, co.Name, wip, force, _preview?.ExpectedRefs);
        var overwrite = force && root.Git.RefSha("refs/heads/" + name) != null;
        var appearance = entry.HasAppearance ? " " + CheckoutIcons.RestoreDescription(true, co) : "";

        var confirmed = overwrite
            ? await Dialogs.Confirm(this, "Overwrite " + name,
                $"A branch {name} is already here. Write over it with the backup version?\n\n"
                + $"The existing branch and its entire worktree are kept under a recovery name before the backup is restored. "
                + $"{entry.Commits} commit(s) are merged onto the snapshot this checkout has now" + (wip ? ", and the uncommitted changes come back after them" : "")
                + "." + appearance + " Nothing goes to SVN.", "Overwrite")
            : await Dialogs.Confirm(this, "Restore " + name,
                $"Make the branch {name} on {co.Name}, and a worktree folder for it at {root.WorktreePathFor(name)}?\n\n"
                + $"{entry.Commits} commit(s) are merged onto the snapshot this checkout has now"
                + (wip ? ", and the uncommitted changes come back after them" : "") + "." + appearance + " Nothing goes to SVN.", "Restore");
        if (!confirmed) return;

        _targetValidation.Invalidate();
        ExistingDestination.Update(null, null);
        ResultBar.IsOpen = false;
        var res = await _form.Run(request, submitted => Runner.Run(Pane, "restore " + submitted.Name,
            () => Backup.Restore(root, submitted.Source, submitted.Name, submitted.Checkout, submitted.WithEdits, submitted.Replace, expectedRefs: submitted.ExpectedRefs),
            worktree: new(submitted.Checkout, submitted.Name, root.WorktreePathFor(submitted.Name))), sender);
        _targetValidation.Invalidate(); // A late name check must not replace the operation result.
        if (res == null) { SyncButton(); return; }

        // Replacement is a choice for one submission, never consent to repeat it after navigation.
        _binding = true;
        ForceBox.IsChecked = false;
        _binding = false;
        ShowOptionSummary();

        var outcome = TaskResults.Describe(res);
        ResultBar.Severity = outcome.State == TaskState.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = outcome.Detail;
        ResultBar.IsOpen = true;
        AppearancePreview.ShowResult(res.CheckoutAppearanceRestored, res.CheckoutAppearanceWarning);
        SetResumeAction(res);

        var conflicts = res.Conflicted.Concat(res.WipConflicted).ToList();
        ConflictsHeader.Visibility = ConflictsCard.Visibility = conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Conflicts.ItemsSource = conflicts;

        entry.ExistsHere = true;
        _selectedRow?.Loaded(entry);
        RestoreButton.IsEnabled = false;
        ExplainTarget(outcome.State == TaskState.Succeeded ? "Done. Close this and the worktree is on the checkout's card."
            : res.Waiting ? "Resume the operation to finish, skip a commit, or cancel." : "Review the result above for saved work or edits that need attention.");
    }

    async void BackupNow_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => BackUpAsync(Worktree));

    /// <summary>
    /// A backup as a report: a chip per outcome with its count, and a row for every item that did not simply
    /// find the remote already holding it - sent, left files out, failed, diverged, or newer over there.
    /// </summary>
    /// <summary>
    /// The buttons a row of the report gets: pull a copy that is newer; and for one that differs, pull
    /// the remote's commits on top of this branch or keep this machine's over it. Both, because which is
    /// right is the reader's call, and the row is where the reason is.
    /// </summary>
    RowActions? ActFor(BackupItem item) =>
        // A pull already paused here is finished, not started again: the row sends it where it waits.
        item.Behind && PausedIn(item.Name) is { } paused ? new RowActions("Resolve", () =>
        {
            Go(() => new ConflictPage(paused), "resolve:" + paused);
            return Task.CompletedTask;
        })
        : item.Behind ? new RowActions("Pull", () => PullAsync(item))
        : item.Rejected && item.Kind is "branch" or "wip" ? new RowActions("Restore separately", () => RestoreSeparatelyAsync(item), "Keep this machine's", () => KeepAsync(item))
        : item.Rejected ? new RowActions("Keep this machine's", () => KeepAsync(item))
        : null;

    /// <summary>The worktree of that name, when a replay is paused in it.</summary>
    string? PausedIn(string name)
    {
        var path = _worktrees.FirstOrDefault(w => w.Name == name)?.Path;
        return path != null && Directory.Exists(path) && Session.Root?.Git.ReplayInProgress(path) is { } replay and not Replay.None ? path : null;
    }

    /// <summary>What a report row can do: one thing, or a first and a second.</summary>
    public sealed record RowActions(string Text, Func<Task> Run, string OtherText = "", Func<Task>? Other = null);

    async Task RestoreSeparatelyAsync(BackupItem item)
    {
        if (Overview) { OpenWorktree(item.Name, separate: true); return; }
        await LoadAsync();
        if (_hidden) return;
        var row = _entries.FirstOrDefault(e => e.Reference.Kind == "branch" && e.Name == item.Name);
        if (row == null) return;
        await PreviewAsync(row);
        await SuggestSeparateNameAsync();
    }

    void SetResumeAction(RestoreResult result)
    {
        ResultBar.ActionButton = null;
        if (!result.Waiting) return;
        var button = new Button { Content = "Resolve" };
        button.Click += (_, _) => Go(() => new ConflictPage(result.Path) { Checkout = result.Checkout, Branch = result.Branch }, "resolve:" + result.Path);
        ResultBar.ActionButton = button;
    }

    /// <summary>What another machine sent, onto the branch here and into its folder, without making the worktree again.</summary>
    async Task PullAsync(BackupItem item)
    {
        var root = Session.Require();
        var r = await Runner.Run(Pane, "pull " + item.Name, () => Backup.Pull(root, item.Name));
        if (r == null) return;
        ResultBar.ActionButton = null;
        var outcome = TaskResults.Describe(r);
        ResultBar.Severity = outcome.State == TaskState.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = outcome.Detail;
        SetResumeAction(r);
        ResultBar.IsOpen = true;
        await LoadAsync();
    }

    /// <summary>This machine's copy over the one the remote holds, for this item alone, after saying what it writes over.</summary>
    async Task KeepAsync(BackupItem item)
    {
        if (!await Dialogs.Confirm(this, "Keep this machine's " + item.Name,
                $"The backup holds a different version of {item.Kind} {item.Name}:\n\n{item.Why}\n\n"
                + "Write this machine's over it? Only this one item is sent. The version it replaces stays readable in this store until the next backup fetches over it.",
                "Write over it"))
            return;
        var root = Session.Require();
        LastReport.Running("Backing up " + item.Name + "...");
        var res = await Runner.Run(Pane, "backup " + item.Name, () => Backup.Run(root, force: true, only: [item.Kind + "/" + item.Name], worktree: item.Kind is "branch" or "wip" ? item.Name : null));
        ShowPageReport(root);
        if (res != null) await LoadAsync();
    }

    public static void ShowReport(ReportCard card, BackupResult? res, Func<BackupItem, RowActions?>? act = null)
    {
        if (res == null)
        {
            card.Hide();
            return;
        }
        // When, in the quiet line under the sentence. The URL is not repeated: the page says it under its title.
        var when = res.When is { } t ? $"{t.LocalDateTime:yyyy-MM-dd HH:mm},{WorktreeRow.Ago(t)}" : "";
        if (res.Error != null)
        {
            card.Show(ChipSeverity.Critical, "", "The last backup could not run" + (res.Worktree == null ? "" : " · " + res.Worktree), res.Error + (when.Length > 0 ? "\n" + when : ""));
            return;
        }
        var failed = res.Items.Count(i => i.Failed);
        var leftOut = res.Items.Count(i => i.LeftOut.Count > 0);
        var bad = failed + res.Rejected;
        var severity = bad > 0 ? ChipSeverity.Critical : res.Behind + leftOut > 0 ? ChipSeverity.Caution : ChipSeverity.Success;
        var headline = res.Items.Count == 0 ? "The last backup found nothing to send"
            : bad == 0 ? "Backed up"
            : $"The last backup did not send {bad} of {res.Items.Count}";
        if (res.Worktree != null) headline += " · " + res.Worktree;
        var detail = when + (res.RemoteOnly.Count > 0 ? $"   {res.RemoteOnly.Count} ref(s) there answer to nothing here: Prune" : "");
        ReportCount[] counts =
        [
            new(ChipSeverity.Success, "", res.Pushed, $"{res.Pushed} sent: the remote holds them now."),
            new(ChipSeverity.Neutral, "", res.Items.Count(i => i.State == "up to date"), "Already there: the remote held these as they are here, so nothing went."),
            new(ChipSeverity.Caution, "", leftOut, $"{leftOut} went without files too big for the backup. The files are named on the rows."),
            new(ChipSeverity.Caution, "", res.Behind, $"{res.Behind} newer on the remote than here. Restore brings them here."),
            new(ChipSeverity.Critical, "", res.Rejected, $"{res.Rejected} diverged: another machine has different work under the name."),
            new(ChipSeverity.Critical, "", failed, $"{failed} failed. The reason is on each row."),
        ];
        card.Show(severity, "", headline, detail, counts,
            res.Items.Where(i => i.State != "up to date" || i.LeftOut.Count > 0).Select(i => RowOf(i, act)));
    }

    static ReportRow RowOf(BackupItem i, Func<BackupItem, RowActions?>? act)
    {
        var row = RowOf(i);
        return act?.Invoke(i) is { } a
            ? new ReportRow(row.Severity, row.Glyph, row.Name, row.What, row.Detail, row.Tip) { ActionText = a.Text, Action = a.Run, OtherText = a.OtherText, Other = a.Other }
            : row;
    }

    static ReportRow RowOf(BackupItem i)
    {
        var kind = i.Kind switch { "branch" => "branch", "wip" => "uncommitted changes", "edits" => "local edits", "review" => "code review", "appearance" => "checkout appearance", _ => "shelf" };
        var (severity, glyph, state) = i.State switch
        {
            "failed" => (ChipSeverity.Critical, "", "failed"),
            "rejected" => (ChipSeverity.Critical, "", "diverged"),
            "behind" => (ChipSeverity.Caution, "", "newer on the remote"),
            "up to date" => (ChipSeverity.Caution, "", "already there, without some files"),
            "pushed" when i.LeftOut.Count > 0 => (ChipSeverity.Caution, "", "sent without some files"),
            "pushed" => (ChipSeverity.Success, "", i.Reconciled ? "sent over an older copy" : "sent"),
            _ => (ChipSeverity.Neutral, "", i.State),
        };
        var leftOut = i.LeftOut.Count > 0 ? "left out, too big: " + string.Join(", ", i.LeftOut) : "";
        var detail = i.Why ?? leftOut;
        var tip = string.Join("\n", new[] { i.RemoteRef, i.Why ?? "", leftOut }.Where(s => s.Length > 0));
        return new ReportRow(severity, glyph, i.Name, kind + ", " + state, detail, tip);
    }

    /// <summary>One line about a backup that ran: what went, what was there already, what was refused.</summary>
    public static string Sentence(BackupResult res)
    {
        var up = res.Items.Count(i => i.State == "up to date");
        var reconciled = res.Items.Count(i => i.Pushed && i.Reconciled);
        var behind = res.Items.Where(i => i.Behind).Select(i => i.Name).ToList();
        var rejected = res.Items.Where(i => i.Rejected).Select(i => i.Name).ToList();
        var failed = res.Items.Where(i => i.Failed).Select(i => i.Name).ToList();
        var parts = new List<string>();
        if (res.Pushed > 0) parts.Add(res.Pushed + " sent" + (reconciled > 0 ? $" ({reconciled} over an older copy from another machine)" : ""));
        if (up > 0) parts.Add(up + " already there");
        if (behind.Count > 0) parts.Add("newer on the remote for " + string.Join(", ", behind) + " (restore to bring it here)");
        if (rejected.Count > 0) parts.Add("diverged for " + string.Join(", ", rejected) + " (another machine has different work under this name)");
        if (failed.Count > 0) parts.Add("failed for " + string.Join(", ", failed));
        if (parts.Count == 0) parts.Add("nothing here to back up");
        return "Backup: " + string.Join(", ", parts) + ".";
    }

    async void Prune_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => PruneAsync(Worktree));

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Host?.Window is MainWindow main) main.ShowSettings();
        else Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
