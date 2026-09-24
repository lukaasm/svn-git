using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Sg.App;

/// <summary>Worktree annotations use the same durable store and freshness checks as agent tools.</summary>
public sealed partial class CodeReviewPage : SgPage
{
    readonly string _path;
    readonly DiffView _diff = new();
    readonly RowTreeView _files = new() { ItemTemplate = (DataTemplate)Application.Current.Resources["FileTreeTemplate"] };
    readonly TextBox _fileSearch = new() { PlaceholderText = "Filter files (Ctrl+F)", FontSize = 12 };
    readonly TextBlock _fileHeader = Text("Files");
    readonly TextBlock _fileEmpty = Text("Loading files…");
    readonly ListFilter _fileList;
    string[] _filePaths = [];
    readonly ComboBox _filter = new() { ItemsSource = new[] { "Open comments", "All comments" }, SelectedIndex = 0 };
    readonly StackPanel _threads = new() { Spacing = 12, Padding = new Thickness(16, 0, 16, 16) };
    readonly StatusStrip _pane = new();
    readonly ReadFeedback _loading = new() { StateId = "CodeReviewLoading", Margin = new Thickness(16) };
    readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    readonly PageReads _reads;
    readonly Grid _workspace = new();
    readonly ScrollViewer _discussion;
    readonly IconButton _comment = new() { Text = "Comment", Glyph = "\uE90A", IsEnabled = false };
    readonly IconButton _backToDiff;
    readonly IconButton _previous, _next;
    readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly ReviewDrafts _drafts = new(DebugTestRun.UserFile("review-drafts"));
    readonly Dictionary<string, FrameworkElement> _threadCards = [];
    readonly Dictionary<string, (string Revision, CodeAnchor Anchor, ReviewLocation? Location)> _threadVersions = [];
    readonly StatusChip _threadSummary = new() { Glyph = "\uE90A" };
    readonly TextBlock _threadHint = Text("Comments and saved code context are included when this worktree is backed up.");
    string? _draftScope, _currentThread;
    IReadOnlyDictionary<string, ReviewDraft> _savedDrafts = new Dictionary<string, ReviewDraft>();
    int _navigationRequest;
    Dictionary<string, ReviewLocation> _locations = [];
    ReviewFile? _file;
    string? _displayedVersion;
    CodeReviewData _data = new();
    bool _loadingFiles, _writing;
    int _visibleThreads = 20;
    string? _selectedFile;
    string? _initialThread;

    public CodeReviewPage(string path, string? threadId = null)
    {
        _path = path; _initialThread = threadId; Title = "Code review"; Subtitle = path;
        _reads = new(active => { _reading = active; UpdateSourceAction(); if (active) _loading.Show("Loading review…", _file == null); else { _loading.Hide(); ScheduleFeedback(); } });
        InitializeSourceUpdates();
        _liveTimer.Tick += async (_, _) => { _liveTimer.Stop(); await RefreshFeedback(); };
        var layout = new Grid { RowSpacing = 8, Padding = new Thickness(16) };
        for (var i = 0; i < 3; i++) layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new WrapRow { Spacing = 8 };
        toolbar.Children.Add(_filter); toolbar.Children.Add(new ActionHint { Content = _comment });
        _backToDiff = Button("Back to diff", "\uE72B", () => _ = LoadFile(), "CodeReviewBackToDiff");
        _backToDiff.Visibility = Visibility.Collapsed; toolbar.Children.Add(_backToDiff);
        toolbar.Children.Add(Button("Refresh", "\uE72C", () => _ = Reload(), "CodeReviewRefresh"));
        toolbar.Children.Add(Button("Copy agent instructions", "\uE8C8", () => _ = CopyHandoff(), "CodeReviewHandoff"));
        toolbar.Children.Add(Button("Readiness", "\uE73E", () => Go(() => new ReviewPage(path), "review:" + path), "CodeReviewReadiness"));
        var navigation = new WrapRow { Spacing = 8 };
        _previous = Button("Previous open", "\uE70E", () => _ = NavigateOpen(false), "ReviewPreviousOpen");
        _next = Button("Next open", "\uE70D", () => _ = NavigateOpen(true), "ReviewNextOpen");
        _previous.IsEnabled = _next.IsEnabled = false;
        navigation.Children.Add(new ActionHint { Content = _previous }); navigation.Children.Add(new ActionHint { Content = _next }); navigation.Children.Add(_position);
        ActionHint.SetHelp(_comment, "Select a readable text file to leave a comment.");
        ActionHint.SetHelp(_previous, "There are no open comments to navigate to.");
        ActionHint.SetHelp(_next, "There are no open comments to navigate to.");
        navigation.Children.Add(_liveState);
        AutomationProperties.SetAutomationId(_position, "ReviewPosition");
        AutomationProperties.SetAutomationId(_liveState, "CodeReviewLiveState");
        var header = new StackPanel { Spacing = 8 }; header.Children.Add(toolbar); header.Children.Add(navigation); layout.Children.Add(header);
        Grid.SetRow(_loading, 1); layout.Children.Add(_loading);
        var notices = new StackPanel { Spacing = 4 }; notices.Children.Add(_notice); notices.Children.Add(_sourceNotice);
        Grid.SetRow(notices, 2); layout.Children.Add(notices);
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(380) });
        _workspace.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _workspace.RowDefinitions.Add(new() { Height = new GridLength(0) });
        _discussion = new ScrollViewer { Content = _threads, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(_discussion, 1); _workspace.Children.Add(_discussion);
        // Keep native controls before WebView in the automation tree; its HWND provider can end sibling traversal.
        _workspace.Children.Add(_diff);
        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(320), MinWidth = 220 });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var filePane = new Grid { RowSpacing = 8 };
        filePane.RowDefinitions.Add(new() { Height = GridLength.Auto });
        filePane.RowDefinitions.Add(new() { Height = GridLength.Auto });
        filePane.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        filePane.Children.Add(_fileHeader); Grid.SetRow(_fileSearch, 1); filePane.Children.Add(_fileSearch);
        var fileContent = new Grid(); fileContent.Children.Add(_files);
        _fileEmpty.Margin = new Thickness(12); _fileEmpty.VerticalAlignment = VerticalAlignment.Top;
        _fileEmpty.IsHitTestVisible = false; fileContent.Children.Add(_fileEmpty);
        var fileCard = new Border { Style = (Style)Application.Current.Resources["Card"], Padding = new Thickness(4), Child = fileContent };
        Grid.SetRow(fileCard, 2); filePane.Children.Add(fileCard); body.Children.Add(filePane);
        var splitter = new Thumb { Style = (Style)Application.Current.Resources["Splitter"] };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter); ColumnSplitter.Attach(splitter, minLeft: 220, minRight: 400);
        Grid.SetColumn(_workspace, 2); body.Children.Add(_workspace);
        Grid.SetRow(body, 3); layout.Children.Add(body);
        Grid.SetRow(_pane, 4); layout.Children.Add(_pane);
        Content = layout;
        AutomationProperties.SetAutomationId(_files, "CodeReviewFiles");
        AutomationProperties.SetAutomationId(_fileSearch, "CodeReviewFileSearch");
        AutomationProperties.SetAutomationId(_fileHeader, "CodeReviewFileCount");
        AutomationProperties.SetName(_files, "Review files");
        AutomationProperties.SetAutomationId(_filter, "CodeReviewFilter");
        AutomationProperties.SetAutomationId(_comment, "CodeReviewComment");
        AutomationProperties.SetAutomationId(_threads, "CodeReviewThreads");
        AutomationProperties.SetAutomationId(_discussion, "CodeReviewDiscussion");
        _comment.Click += async (_, _) => await NewComment();
        _fileList = new ListFilter(_fileSearch, _files, _fileHeader, row => ((FileRow)row).Path);
        _fileList.Changed += () =>
        {
            _fileEmpty.Text = _fileList.Count == 0 ? "No changed files or comments yet." : "No matching files. Clear the filter to see all files.";
            _fileEmpty.Visibility = _fileList.ShownCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        _fileList.Picked += async node =>
        {
            if (_loadingFiles || node.Row is not FileRow row || row.Path == _selectedFile) return;
            ++_navigationRequest; _currentThread = null; _selectedFile = row.Path; _visibleThreads = 20; await LoadFile();
        };
        FileActions.Attach(_files, node => PathUtil.Join(_path, node.FullPath));
        _filter.SelectionChanged += (_, _) => { _currentThread = null; _visibleThreads = 20; RenderThreads(); };
        _filter.DropDownClosed += (_, _) => ScheduleFeedback();
        _diff.ActionInvoked += async id => { if (id == "comment") await NewComment(selected: true); };
        _diff.ReviewCommentInvoked += async (side, selection) => { if (_file != null) await NewComment(selection: selection, side: side); };
        _diff.SelectionChanged += () => _diff.SetActionState("comment", _file != null && _diff.Selection != null);
        _diff.ReviewActionInvoked += async (id, action) =>
        {
            if (_data.Threads.FirstOrDefault(t => t.Id == id) is not { } thread) return;
            _currentThread = id; UpdateNavigation();
            if (action == "select") return;
            await Address(thread, action);
            if (_file != null && _selectedFile == thread.Anchor.File) _diff.RevealReviewThread(id);
        };
        Shortcuts.Add(this, Windows.System.VirtualKey.F8, () => _ = NavigateOpen(true));
        Shortcuts.Add(this, Windows.System.VirtualKey.F8, Windows.System.VirtualKeyModifiers.Shift, () => _ = NavigateOpen(false));
        ToolTipService.SetToolTip(_next, "Next unresolved comment across this worktree (F8)");
        ToolTipService.SetToolTip(_previous, "Previous unresolved comment across this worktree (Shift+F8)");
        _workspace.SizeChanged += (_, _) =>
        {
            var compact = _workspace.ActualWidth < 1000;
            _workspace.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 380);
            _workspace.RowDefinitions[1].Height = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(_discussion, compact ? 0 : 1); Grid.SetRow(_discussion, compact ? 1 : 0);
        };
        Unloaded += (_, _) => OnHidden();
    }
    static IconButton Button(string text, string glyph, Action click, string id)
    {
        var b = new IconButton { Text = text, Glyph = glyph };
        AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => click(); return b;
    }
    static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    public override void OnShown(bool returning) { StartLiveUpdates(); _ = Reload(); }
    public override void OnHidden() { StopLiveUpdates(); ++_navigationRequest; _reads.Cancel(); }
    internal override object? CaptureViewState() => _selectedFile;
    internal override void RestoreViewState(object? state) => _selectedFile = state as string;
    void Error(string message) { _notice.Message = message; _notice.Severity = InfoBarSeverity.Error; _notice.IsOpen = true; }
    async Task Reload()
    {
        using var read = _reads.Begin(); var root = Session.Require();
        var feed = await EnsureFeed();
        if (feed == null || !read.Current) return;
        var result = await read.Run(_pane, () =>
        {
            feed.Reconnect();
            var scope = feed.Identity;
            var files = CodeReview.Files(root, _path);
            IReadOnlyDictionary<string, ReviewDraft> drafts = new Dictionary<string, ReviewDraft>();
            string? draftError = null;
            try { drafts = _drafts.List(scope); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SgException) { draftError = "Drafts could not be loaded: " + e.Message; }
            return new Inventory(files, feed.Read(), scope, drafts, draftError);
        }, Error);
        if (result == null || !read.Current) return;
        if (_notice.Message == _liveError) _notice.IsOpen = false;
        _liveError = null;
        _data = result.Snapshot.Data; _feedbackRevision = result.Snapshot.Revision;
        _knownFiles = result.Files;
        _draftScope = result.Scope;
        _savedDrafts = result.Drafts;
        if (result.DraftError != null) Error(result.DraftError);
        UpdateFiles(); SetLiveStatus();
        if (_initialThread is { } id)
        {
            _initialThread = null;
            var thread = _data.Threads.FirstOrDefault(t => t.Id == id);
            if (thread != null) { await OpenThread(thread); return; }
            Error("This comment is no longer available in this worktree. The current feedback is shown below.");
        }
        await LoadFile(preserve: true);
    }
    sealed record Inventory(IReadOnlyList<string> Files, ReviewSnapshot Snapshot, string Scope, IReadOnlyDictionary<string, ReviewDraft> Drafts, string? DraftError);
    sealed record LoadedFile(ReviewFile File, Dictionary<string, ReviewLocation> Locations);
    static Dictionary<string, ReviewLocation> LocateThreads(CodeReviewData data, ReviewFile? file) => file == null ? []
        : data.Threads.Where(t => t.Anchor.File == file.File).ToDictionary(t => t.Id, t =>
            CodeReview.Locate(t.Anchor, data.Contents[t.Anchor.Content], t.Anchor.Side == "original" ? file.Original : file.Version == "missing" ? null : file.Modified));
    async Task LoadFile(bool preserve = false)
    {
        preserve &= _file != null && _file.File == _selectedFile;
        if (!preserve)
        {
            StopSourceUpdates();
            _file = null; _displayedVersion = null; _locations.Clear();
        }
        _comment.IsEnabled = false;
        _comment.Text = _savedDrafts.ContainsKey("comment:" + _selectedFile) ? "Resume draft" : "Comment";
        _backToDiff.Visibility = Visibility.Collapsed;
        if (_selectedFile == null)
        {
            _diff.ShowText("No changed files or comments yet. Edit a worktree file to start a code review.", "Code review");
            RenderThreads(); return;
        }
        using var read = _reads.Begin(); var root = Session.Require(); var file = _selectedFile;
        if (!preserve) _diff.BeginLoading(file);
        var data = _data;
        var result = await read.Run(_pane, () =>
        {
            var loaded = CodeReview.ReadFile(root, _path, file);
            return new LoadedFile(loaded, LocateThreads(data, loaded));
        }, preserve ? SourceProblem : Error);
        if (!read.Current) return;
        if (result == null && preserve)
        {
            _locations = LocateThreads(_data, _file); RenderThreads(preserve: true);
            _comment.IsEnabled = true; return;
        }
        _file = result?.File;
        _displayedVersion = _file?.Version;
        if (result != null)
        {
            _locations = result.Locations;
            _diff.Show(result.File.Original, result.File.Modified, DiffView.LanguageFor(file), result.File.Modified, file, preserveView: preserve);
            _diff.SetActions([new("comment", "Comment on selected lines", "\uE90A", "Leave feedback on the selected modified lines")]);
            _comment.IsEnabled = true;
        }
        else _diff.ShowText("This file cannot be loaded. Saved comments remain available on the right.", file);
        _comment.IsEnabled = _file != null || _savedDrafts.ContainsKey("comment:" + file);
        RenderThreads(preserve);
        if (_file != null) await FollowDisplayedSource(_file);
    }
    void RenderThreads(bool preserve = false)
    {
        var scroll = preserve ? BrowseScroll.Capture(_discussion, _threadCards) : null;
        var generation = _navigationRequest; var file = _selectedFile;
        var previousCards = _threadCards.ToDictionary();
        var previousVersions = _threadVersions.ToDictionary();
        _threadCards.Clear(); _threadVersions.Clear(); UpdateNavigation();
        var children = new List<UIElement>();
        var all = ReviewNavigation.Ordered(_data.Threads.Where(t => t.Anchor.File == _selectedFile));
        var open = all.Count(t => t.State == "open");
        _threadSummary.Text = $"{open} open · {all.Length - open} resolved";
        _threadSummary.Severity = open > 0 ? ChipSeverity.Attention : ChipSeverity.Success;
        children.Add(_threadSummary); children.Add(_threadHint);
        // Keep a selected thread visible when an agent resolves it, so the result can be read in place.
        var shown = all.Where(t => _filter.SelectedIndex == 1 || t.State == "open" || _keepResolved && t.Id == _currentThread).ToArray();
        _diff.SetReviewThreads(shown.Where(t => _locations.GetValueOrDefault(t.Id)?.First > 0).Select(t =>
        {
            var location = _locations[t.Id];
            return new DiffView.ReviewAnnotation(t.Id, t.Anchor.Side, location.First!.Value, location.Last!.Value, t.State, t.Conflict,
                t.Events.Select(e => new DiffView.ReviewMessage(e.Actor, e.Action, e.Body, e.At.LocalDateTime.ToString("g"))).ToArray());
        }).ToArray());
        if (shown.Length == 0) children.Add(Text(all.Length == 0 ? "No comments for this file. Select code and choose Comment, or leave feedback on the whole file." : "No open comments. Choose All comments to see resolved feedback."));
        foreach (var thread in shown.Take(_visibleThreads))
        {
            var location = _locations.GetValueOrDefault(thread.Id);
            var version = (thread.Revision, thread.Anchor, location);
            _threadVersions[thread.Id] = version;
            if (preserve && previousVersions.GetValueOrDefault(thread.Id) == version && previousCards.TryGetValue(thread.Id, out var existing))
            { _threadCards[thread.Id] = existing; children.Add(existing); continue; }
            var card = new StackPanel { Spacing = 8 };
            var anchor = thread.Anchor;
            card.Children.Add(new StatusChip { Text = thread.Conflict ? "Concurrent feedback · needs review" : thread.State == "open" ? "Open" : "Resolved", Glyph = thread.State == "open" ? "\uE90A" : "\uE73E", Severity = thread.Conflict ? ChipSeverity.Caution : thread.State == "open" ? ChipSeverity.Attention : ChipSeverity.Success });
            card.Children.Add(Text(anchor.First == 0 ? "Whole file" : $"{anchor.Side} · lines {anchor.First}–{anchor.Last}"));
            if (anchor.First > 0 && location?.State != "current")
                card.Children.Add(Text(location?.First > 0 ? $"Now at line {location.First}" : "Saved anchor · open context to compare with current code"));
            foreach (var e in thread.Events)
            {
                var author = Text(""); UserColors.Header(author, "", e.Actor, $" · {e.Action} · {e.At.LocalDateTime:g}"); card.Children.Add(author);
                card.Children.Add(Text(e.Body));
            }
            var actions = new WrapRow { Spacing = 8 };
            if (location?.First > 0) actions.Children.Add(Button("Show in code", "\uE8A5", () => { _currentThread = thread.Id; UpdateNavigation(); _diff.RevealReviewThread(thread.Id); }, "ReviewShow_" + thread.Id));
            actions.Children.Add(Button("Context", "\uE8A5", () => { _currentThread = thread.Id; UpdateNavigation(); _ = ShowContext(thread); }, "ReviewContext_" + thread.Id));
            actions.Children.Add(Button("Reply", "\uE97A", () => _ = Address(thread, "reply"), "ReviewReply_" + thread.Id));
            var action = thread.State == "open" ? "resolve" : "reopen";
            actions.Children.Add(Button(thread.State == "open" ? "Resolve" : "Reopen", "\uE73E", () => _ = Address(thread, action), "ReviewAddress_" + thread.Id));
            card.Children.Add(actions);
            var border = new Border { Child = card, Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] };
            AutomationProperties.SetAutomationId(border, "ReviewThread_" + thread.Id);
            _threadCards[thread.Id] = border; children.Add(border);
        }
        if (shown.Length > _visibleThreads) children.Add(Button("Show more comments", "\uE70D", () => { _visibleThreads += 20; RenderThreads(); }, "CodeReviewMore"));
        foreach (var old in _threads.Children.Where(c => !children.Contains(c)).ToArray()) _threads.Children.Remove(old);
        for (var i = 0; i < children.Count; i++)
        {
            if (i < _threads.Children.Count && ReferenceEquals(_threads.Children[i], children[i])) continue;
            _threads.Children.Remove(children[i]); _threads.Children.Insert(i, children[i]);
        }
        if (scroll != null) BrowseScroll.Restore(_discussion, scroll, _threadCards, () => !_hidden && generation == _navigationRequest && file == _selectedFile);
    }
    void UpdateNavigation()
    {
        UpdateSourceAction();
        var open = ReviewNavigation.Ordered(_data.Threads.Where(t => t.State == "open"));
        var index = Array.FindIndex(open, t => t.Id == _currentThread);
        _position.Text = open.Length == 0 ? "No open comments in this worktree" : index >= 0 ? $"Open comment {index + 1} of {open.Length}" : $"{open.Length} open comments across {open.Select(t => t.Anchor.File).Distinct().Count()} files";
        _previous.IsEnabled = _next.IsEnabled = open.Length > 0 && !_writing;
        var reason = _writing ? "Wait for the current comment to be saved." : open.Length == 0 ? "There are no open comments to navigate to." : null;
        ActionHint.SetHelp(_previous, reason ?? "Open the previous unresolved comment across files (Shift+F8).");
        ActionHint.SetHelp(_next, reason ?? "Open the next unresolved comment across files (F8).");
        ActionHint.SetHelp(_comment, _reading ? "Reading the selected file…" : _comment.IsEnabled ? "Comment on this file or resume its saved draft. Select code and use the context menu to comment on specific lines." : "Select a readable text file. Binary or unavailable files cannot receive inline comments.");
    }
    async Task NavigateOpen(bool forward)
    {
        if (_writing) return;
        var thread = ReviewNavigation.Next(_data.Threads, _currentThread, _selectedFile, forward);
        if (thread == null) return;
        await OpenThread(thread);
    }
    async Task OpenThread(CodeThread thread)
    {
        if (thread.State == "resolved") _filter.SelectedIndex = 1;
        var request = ++_navigationRequest; _currentThread = thread.Id;
        _selectedFile = thread.Anchor.File;
        _loadingFiles = true; _fileList.Select(row => row.TreePath == _selectedFile, clearFilter: true); _loadingFiles = false;
        _visibleThreads = Math.Max(20, Array.FindIndex(ReviewNavigation.Ordered(_data.Threads.Where(t => t.Anchor.File == _selectedFile && (_filter.SelectedIndex == 1 || t.State == "open"))), t => t.Id == thread.Id) + 1);
        UpdateNavigation(); await LoadFile();
        if (request != _navigationRequest) return;
        if (_locations.GetValueOrDefault(thread.Id)?.First > 0) _diff.RevealReviewThread(thread.Id);
        else if (thread.Anchor.First > 0) await ShowContext(thread);
        if (request == _navigationRequest && _threadCards.TryGetValue(thread.Id, out var card)) BrowseScroll.Reveal(card);
    }
    async Task<ReviewContext?> ShowContext(CodeThread thread, bool display = true)
    {
        using var read = _reads.Begin();
        var context = await read.Run(_pane, () => CodeReview.Context(Session.Require(), _path, thread.Id), Error);
        if (!read.Current) return null;
        if (context != null && display) DisplayContext(context);
        return context;
    }
    void DisplayContext(ReviewContext context)
    {
        StopSourceUpdates();
        _locations.Clear(); _backToDiff.Visibility = Visibility.Visible;
        _file = null; _displayedVersion = context.Version; _comment.IsEnabled = false; _diff.SetActions([]);
        var file = context.Thread.Anchor.File;
        _diff.Show(context.Original, context.Current ?? "", DiffView.LanguageFor(file), context.Current ?? context.Original, file + " · " + context.Location);
        RenderThreads();
    }
    async Task NewComment(bool selected = false, DiffView.LineRange? selection = null, string side = "modified")
    {
        if (_writing || _reading || _selectedFile == null || _draftScope == null) return;
        var file = _file; var root = Session.Require();
        _writing = true; UpdateNavigation();
        try
        {
            var version = file == null ? "unavailable" : WorkspaceVersion.Hash(file.Original + "\0" + file.Modified);
            var options = new ReviewComposer.Options("Leave code review feedback", "Save comment", "Feedback", _selectedFile, version, file, selection ?? (selected ? _diff.Selection : null), CanSubmit: file != null, Side: side);
            await ReviewComposer.Show(XamlRoot, _drafts, _draftScope, "comment:" + _selectedFile, options, draft =>
                Task.Run(() => CodeReview.Add(root, _path, file!, draft.Side, (int)draft.First!.Value, (int)draft.Last!.Value, draft.Body, id: draft.Id)));
        }
        catch (Exception e) { Error(e.Message); }
        finally { _writing = false; UpdateNavigation(); QueueFeedback(refreshDrafts: true); }
    }
    async Task Address(CodeThread thread, string action)
    {
        if (_writing || _draftScope == null) return;
        _writing = true; _currentThread = thread.Id; UpdateNavigation();
        try
        {
            var context = await ShowContext(thread, display: false);
            if (context == null) return;
            if (action == "resolve" && context.Version != _displayedVersion)
            {
                DisplayContext(context);
                _notice.Message = "Code changed since you opened this view. Review the refreshed context, then resolve.";
                _notice.Severity = InfoBarSeverity.Warning; _notice.IsOpen = true;
                return;
            }
            var root = Session.Require();
            var options = new ReviewComposer.Options(action == "resolve" ? "Resolve feedback" : action == "reopen" ? "Reopen feedback" : "Reply to feedback",
                action == "resolve" ? "Resolve" : "Save", action == "resolve" ? "What changed or why no change is needed" : "Reply", thread.Anchor.File, context.Version);
            var submitted = await ReviewComposer.Show(XamlRoot, _drafts, _draftScope, thread.Id + ":" + action, options, draft =>
                Task.Run(() => CodeReview.Address(root, _path, thread.Id, action, draft.Body, context.Thread.Revision, version: context.Version, requestId: draft.Id)));
            if (submitted && action == "resolve") _resolvedLocally = thread.Id;
        }
        catch (Exception e) { Error(e.Message); }
        finally { _writing = false; UpdateNavigation(); QueueFeedback(refreshDrafts: true); }
    }
    async Task CopyHandoff()
    {
        try
        {
            var text = await Task.Run(() => CodeReview.Handoff(Session.Require(), _path));
            var package = new DataPackage(); package.SetText(text); Clipboard.SetContent(package);
            _notice.Message = "Agent instructions copied."; _notice.Severity = InfoBarSeverity.Success; _notice.IsOpen = true;
        }
        catch (Exception e) { Error(e.Message); }
    }
}
