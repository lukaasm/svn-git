using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Sg.App;

/// <summary>Worktree annotations use the same durable store and freshness checks as agent tools.</summary>
public sealed class CodeReviewPage : SgPage
{
    readonly string _path;
    readonly DiffView _diff = new();
    readonly ComboBox _files = new() { MinWidth = 240, MaxWidth = 600, PlaceholderText = "Changed files", DisplayMemberPath = "Label" };
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

    public CodeReviewPage(string path)
    {
        _path = path; Title = "Code review"; Subtitle = path;
        _reads = new(active => { if (active) _loading.Show("Loading review…", _file == null); else _loading.Hide(); });
        var layout = new Grid { RowSpacing = 8, Padding = new Thickness(16) };
        for (var i = 0; i < 3; i++) layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new WrapRow { Spacing = 8 };
        toolbar.Children.Add(_files); toolbar.Children.Add(_filter); toolbar.Children.Add(_comment);
        _backToDiff = Button("Back to diff", "\uE72B", () => _ = LoadFile(), "CodeReviewBackToDiff");
        _backToDiff.Visibility = Visibility.Collapsed; toolbar.Children.Add(_backToDiff);
        toolbar.Children.Add(Button("Refresh", "\uE72C", () => _ = Reload(), "CodeReviewRefresh"));
        toolbar.Children.Add(Button("Copy agent instructions", "\uE8C8", () => _ = CopyHandoff(), "CodeReviewHandoff"));
        toolbar.Children.Add(Button("Readiness", "\uE73E", () => Go(() => new ReviewPage(path), "review:" + path), "CodeReviewReadiness"));
        var navigation = new WrapRow { Spacing = 8 };
        _previous = Button("Previous open", "\uE70E", () => _ = NavigateOpen(false), "ReviewPreviousOpen");
        _next = Button("Next open", "\uE70D", () => _ = NavigateOpen(true), "ReviewNextOpen");
        _previous.IsEnabled = _next.IsEnabled = false;
        navigation.Children.Add(_previous); navigation.Children.Add(_next); navigation.Children.Add(_position);
        AutomationProperties.SetAutomationId(_position, "ReviewPosition");
        var header = new StackPanel { Spacing = 8 }; header.Children.Add(toolbar); header.Children.Add(navigation); layout.Children.Add(header);
        Grid.SetRow(_loading, 1); layout.Children.Add(_loading);
        Grid.SetRow(_notice, 2); layout.Children.Add(_notice);
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _workspace.ColumnDefinitions.Add(new() { Width = new GridLength(380) });
        _workspace.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _workspace.RowDefinitions.Add(new() { Height = new GridLength(0) });
        _discussion = new ScrollViewer { Content = _threads, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(_discussion, 1); _workspace.Children.Add(_discussion);
        // Keep native controls before WebView in the automation tree; its HWND provider can end sibling traversal.
        _workspace.Children.Add(_diff);
        Grid.SetRow(_workspace, 3); layout.Children.Add(_workspace);
        Grid.SetRow(_pane, 4); layout.Children.Add(_pane);
        Content = layout;
        AutomationProperties.SetAutomationId(_files, "CodeReviewFiles");
        AutomationProperties.SetAutomationId(_filter, "CodeReviewFilter");
        AutomationProperties.SetAutomationId(_comment, "CodeReviewComment");
        AutomationProperties.SetAutomationId(_threads, "CodeReviewThreads");
        _comment.Click += async (_, _) => await NewComment();
        _files.SelectionChanged += async (_, _) => { if (!_loadingFiles) { ++_navigationRequest; _currentThread = null; _selectedFile = (_files.SelectedItem as FileChoice)?.Path; _visibleThreads = 20; await LoadFile(); } };
        _filter.SelectionChanged += (_, _) => { _visibleThreads = 20; RenderThreads(); };
        _diff.ActionInvoked += async id => { if (id == "comment") await NewComment(selected: true); };
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
        SizeChanged += (_, _) =>
        {
            var compact = ActualWidth < 1000;
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
    public override void OnShown(bool returning) => _ = Reload();
    public override void OnHidden() { ++_navigationRequest; _reads.Cancel(); }
    internal override object? CaptureViewState() => _selectedFile;
    internal override void RestoreViewState(object? state) => _selectedFile = state as string;
    void Error(string message) { _notice.Message = message; _notice.Severity = InfoBarSeverity.Error; _notice.IsOpen = true; }
    async Task Reload()
    {
        using var read = _reads.Begin(); var root = Session.Require();
        var result = await read.Run(_pane, () =>
        {
            var scope = CodeReview.WorktreeIdentity(root, _path);
            var files = CodeReview.Files(root, _path);
            IReadOnlyDictionary<string, ReviewDraft> drafts = new Dictionary<string, ReviewDraft>();
            string? draftError = null;
            try { drafts = _drafts.List(scope); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SgException) { draftError = "Drafts could not be loaded: " + e.Message; }
            return new Inventory(files.Concat(drafts.Where(d => d.Key.StartsWith("comment:", StringComparison.Ordinal)).Select(d => d.Value.File)).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase).ToArray(), CodeReview.Read(root, _path), scope, drafts, draftError);
        }, Error);
        if (result == null || !read.Current) return;
        _data = result.Data;
        _draftScope = result.Scope;
        _savedDrafts = result.Drafts;
        if (result.DraftError != null) Error(result.DraftError);
        var counts = _data.Threads.GroupBy(t => t.Anchor.File).ToDictionary(g => g.Key, g => (Open: g.Count(t => t.State == "open"), Total: g.Count()));
        var choices = result.Files.Select(f => { var count = counts.GetValueOrDefault(f); return new FileChoice(f, count.Open, count.Total - count.Open, _savedDrafts.Values.Any(d => d.File == f)); }).ToArray();
        _loadingFiles = true;
        _files.ItemsSource = choices;
        _files.SelectedItem = choices.FirstOrDefault(f => f.Path == _selectedFile) ?? choices.FirstOrDefault();
        _selectedFile = (_files.SelectedItem as FileChoice)?.Path;
        _loadingFiles = false;
        await LoadFile();
    }
    sealed record Inventory(IReadOnlyList<string> Files, CodeReviewData Data, string Scope, IReadOnlyDictionary<string, ReviewDraft> Drafts, string? DraftError);
    sealed record FileChoice(string Path, int Open, int Resolved, bool Draft)
    {
        public string Label => $"{Path}   ·   {Open} open · {Resolved} resolved" + (Draft ? " · draft" : "");
    }
    sealed record LoadedFile(ReviewFile File, Dictionary<string, ReviewLocation> Locations);
    async Task LoadFile()
    {
        _file = null; _displayedVersion = null; _comment.IsEnabled = false; _locations.Clear();
        _comment.Text = _savedDrafts.ContainsKey("comment:" + _selectedFile) ? "Resume draft" : "Comment";
        _backToDiff.Visibility = Visibility.Collapsed;
        if (_selectedFile == null)
        {
            _diff.ShowText("No changed files or comments yet. Edit a worktree file to start a code review.", "Code review");
            RenderThreads(); return;
        }
        using var read = _reads.Begin(); var root = Session.Require(); var file = _selectedFile;
        _diff.BeginLoading(file);
        var data = _data;
        var result = await read.Run(_pane, () =>
        {
            var loaded = CodeReview.ReadFile(root, _path, file);
            var locations = data.Threads.Where(t => t.Anchor.File == file).ToDictionary(t => t.Id, t =>
                CodeReview.Locate(t.Anchor, data.Contents[t.Anchor.Content], t.Anchor.Side == "original" ? loaded.Original : loaded.Version == "missing" ? null : loaded.Modified));
            return new LoadedFile(loaded, locations);
        }, Error);
        if (!read.Current) return;
        _file = result?.File;
        _displayedVersion = _file?.Version;
        if (result != null)
        {
            _locations = result.Locations;
            _diff.Show(result.File.Original, result.File.Modified, DiffView.LanguageFor(file), result.File.Modified, file);
            _diff.SetActions([new("comment", "Comment on selected lines", "\uE90A", "Leave feedback on the selected modified lines")]);
            _comment.IsEnabled = true;
        }
        else _diff.ShowText("This file cannot be loaded. Saved comments remain available on the right.", file);
        _comment.IsEnabled = _file != null || _savedDrafts.ContainsKey("comment:" + file);
        RenderThreads();
    }
    void RenderThreads()
    {
        _threads.Children.Clear(); _threadCards.Clear(); UpdateNavigation();
        var all = ReviewNavigation.Ordered(_data.Threads.Where(t => t.Anchor.File == _selectedFile));
        var open = all.Count(t => t.State == "open");
        _threads.Children.Add(new StatusChip { Text = $"{open} open · {all.Length - open} resolved", Glyph = "\uE90A", Severity = open > 0 ? ChipSeverity.Attention : ChipSeverity.Success });
        _threads.Children.Add(Text("Comments and saved code context are included when this worktree is backed up."));
        var shown = all.Where(t => _filter.SelectedIndex == 1 || t.State == "open").ToArray();
        _diff.SetReviewThreads(shown.Where(t => _locations.GetValueOrDefault(t.Id)?.First > 0).Select(t =>
        {
            var location = _locations[t.Id];
            return new DiffView.ReviewAnnotation(t.Id, t.Anchor.Side, location.First!.Value, location.Last!.Value, t.State, t.Conflict,
                t.Events.Select(e => new DiffView.ReviewMessage(e.Actor, e.Action, e.Body, e.At.LocalDateTime.ToString("g"))).ToArray());
        }).ToArray());
        if (shown.Length == 0) _threads.Children.Add(Text(all.Length == 0 ? "No comments for this file. Select code and choose Comment, or leave feedback on the whole file." : "No open comments. Choose All comments to see resolved feedback."));
        foreach (var thread in shown.Take(_visibleThreads))
        {
            var card = new StackPanel { Spacing = 8 };
            var anchor = thread.Anchor;
            card.Children.Add(new StatusChip { Text = thread.Conflict ? "Concurrent feedback · needs review" : thread.State == "open" ? "Open" : "Resolved", Glyph = thread.State == "open" ? "\uE90A" : "\uE73E", Severity = thread.Conflict ? ChipSeverity.Caution : thread.State == "open" ? ChipSeverity.Attention : ChipSeverity.Success });
            card.Children.Add(Text(anchor.First == 0 ? "Whole file" : $"{anchor.Side} · lines {anchor.First}–{anchor.Last}"));
            var location = _locations.GetValueOrDefault(thread.Id);
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
            _threadCards[thread.Id] = border; _threads.Children.Add(border);
        }
        if (shown.Length > _visibleThreads) _threads.Children.Add(Button("Show more comments", "\uE70D", () => { _visibleThreads += 20; RenderThreads(); }, "CodeReviewMore"));
    }
    void UpdateNavigation()
    {
        var open = ReviewNavigation.Ordered(_data.Threads.Where(t => t.State == "open"));
        var index = Array.FindIndex(open, t => t.Id == _currentThread);
        _position.Text = open.Length == 0 ? "No open comments in this worktree" : index >= 0 ? $"Open comment {index + 1} of {open.Length}" : $"{open.Length} open comments across {open.Select(t => t.Anchor.File).Distinct().Count()} files";
        _previous.IsEnabled = _next.IsEnabled = open.Length > 0 && !_writing;
    }
    async Task NavigateOpen(bool forward)
    {
        if (_writing) return;
        var thread = ReviewNavigation.Next(_data.Threads, _currentThread, _selectedFile, forward);
        if (thread == null) return;
        var request = ++_navigationRequest; _currentThread = thread.Id;
        _selectedFile = thread.Anchor.File;
        _loadingFiles = true; _files.SelectedItem = _files.Items.OfType<FileChoice>().FirstOrDefault(f => f.Path == _selectedFile); _loadingFiles = false;
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
        _locations.Clear(); _backToDiff.Visibility = Visibility.Visible;
        _file = null; _displayedVersion = context.Version; _comment.IsEnabled = false; _diff.SetActions([]);
        var file = context.Thread.Anchor.File;
        _diff.Show(context.Original, context.Current ?? "", DiffView.LanguageFor(file), context.Current ?? context.Original, file + " · " + context.Location);
        RenderThreads();
    }
    async Task NewComment(bool selected = false)
    {
        if (_writing || _selectedFile == null || _draftScope == null) return;
        var file = _file; var root = Session.Require();
        _writing = true; UpdateNavigation();
        try
        {
            var version = file == null ? "unavailable" : WorkspaceVersion.Hash(file.Original + "\0" + file.Modified);
            var options = new ReviewComposer.Options("Leave code review feedback", "Save comment", "Feedback", _selectedFile, version, file, selected ? _diff.Selection : null, CanSubmit: file != null);
            await ReviewComposer.Show(XamlRoot, _drafts, _draftScope, "comment:" + _selectedFile, options, draft =>
                Task.Run(() => CodeReview.Add(root, _path, file!, draft.Side, (int)draft.First!.Value, (int)draft.Last!.Value, draft.Body, id: draft.Id)));
            await Reload();
        }
        catch (Exception e) { Error(e.Message); }
        finally { _writing = false; UpdateNavigation(); }
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
            await ReviewComposer.Show(XamlRoot, _drafts, _draftScope, thread.Id + ":" + action, options, draft =>
                Task.Run(() => CodeReview.Address(root, _path, thread.Id, action, draft.Body, context.Thread.Revision, version: context.Version, requestId: draft.Id)));
            await Reload();
        }
        catch (Exception e) { Error(e.Message); }
        finally { _writing = false; UpdateNavigation(); }
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
