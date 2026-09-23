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
    readonly ComboBox _files = new() { MinWidth = 240, MaxWidth = 500, PlaceholderText = "Changed files" };
    readonly ComboBox _filter = new() { ItemsSource = new[] { "Open comments", "All comments" }, SelectedIndex = 0 };
    readonly StackPanel _threads = new() { Spacing = 12, Padding = new Thickness(16, 0, 16, 16) };
    readonly StatusStrip _pane = new();
    readonly ReadFeedback _loading = new() { StateId = "CodeReviewLoading", Margin = new Thickness(16) };
    readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    readonly PageReads _reads;
    readonly Grid _workspace = new();
    readonly ScrollViewer _discussion;
    readonly IconButton _comment = new() { Text = "Comment", Glyph = "\uE90A", IsEnabled = false };
    ReviewFile? _file;
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
        toolbar.Children.Add(Button("Refresh", "\uE72C", () => _ = Reload(), "CodeReviewRefresh"));
        toolbar.Children.Add(Button("Copy agent instructions", "\uE8C8", () => _ = CopyHandoff(), "CodeReviewHandoff"));
        toolbar.Children.Add(Button("Readiness", "\uE73E", () => Go(() => new ReviewPage(path), "review:" + path), "CodeReviewReadiness"));
        layout.Children.Add(toolbar);
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
        _files.SelectionChanged += async (_, _) => { if (!_loadingFiles) { _selectedFile = _files.SelectedItem as string; await LoadFile(); } };
        _filter.SelectionChanged += (_, _) => { _visibleThreads = 20; RenderThreads(); };
        _diff.ActionInvoked += async id => { if (id == "comment") await NewComment(selected: true); };
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
    public override void OnHidden() => _reads.Cancel();
    internal override object? CaptureViewState() => _selectedFile;
    internal override void RestoreViewState(object? state) => _selectedFile = state as string;
    void Error(string message) { _notice.Message = message; _notice.Severity = InfoBarSeverity.Error; _notice.IsOpen = true; }
    async Task Reload()
    {
        using var read = _reads.Begin(); var root = Session.Require();
        var result = await read.Run(_pane, () => new Inventory(CodeReview.Files(root, _path), CodeReview.Read(root, _path)), Error);
        if (result == null || !read.Current) return;
        _data = result.Data;
        _loadingFiles = true;
        _files.ItemsSource = result.Files;
        _files.SelectedItem = result.Files.Contains(_selectedFile) ? _selectedFile : result.Files.FirstOrDefault();
        _selectedFile = _files.SelectedItem as string;
        _loadingFiles = false;
        await LoadFile();
    }
    sealed record Inventory(IReadOnlyList<string> Files, CodeReviewData Data);
    async Task LoadFile()
    {
        _file = null; _comment.IsEnabled = false;
        if (_selectedFile == null)
        {
            _diff.ShowText("No changed files or comments yet. Edit a worktree file to start a code review.", "Code review");
            RenderThreads(); return;
        }
        using var read = _reads.Begin(); var root = Session.Require(); var file = _selectedFile;
        _diff.BeginLoading(file);
        var result = await read.Run(_pane, () => CodeReview.ReadFile(root, _path, file), Error);
        if (!read.Current) return;
        _file = result;
        if (result != null)
        {
            _diff.Show(result.Original, result.Modified, DiffView.LanguageFor(file), result.Modified, file);
            _diff.SetActions([new("comment", "Comment on selected lines", "\uE90A", "Leave feedback on the selected modified lines")]);
            _comment.IsEnabled = true;
        }
        else _diff.ShowText("This file cannot be loaded. Saved comments remain available on the right.", file);
        RenderThreads();
    }
    void RenderThreads()
    {
        _threads.Children.Clear();
        var all = _data.Threads.Where(t => t.Anchor.File == _selectedFile).ToArray();
        var open = all.Count(t => t.State == "open");
        _threads.Children.Add(new StatusChip { Text = $"{open} open · {all.Length - open} resolved", Glyph = "\uE90A", Severity = open > 0 ? ChipSeverity.Attention : ChipSeverity.Success });
        _threads.Children.Add(Text("Comments and saved code context are included when this worktree is backed up."));
        var shown = all.Where(t => _filter.SelectedIndex == 1 || t.State == "open").ToArray();
        if (shown.Length == 0) _threads.Children.Add(Text(all.Length == 0 ? "No comments for this file. Select code and choose Comment, or leave feedback on the whole file." : "No open comments. Choose All comments to see resolved feedback."));
        foreach (var thread in shown.Take(_visibleThreads))
        {
            var card = new StackPanel { Spacing = 8 };
            var anchor = thread.Anchor;
            card.Children.Add(new StatusChip { Text = thread.Conflict ? "Concurrent feedback · needs review" : thread.State == "open" ? "Open" : "Resolved", Glyph = thread.State == "open" ? "\uE90A" : "\uE73E", Severity = thread.State == "open" ? ChipSeverity.Attention : ChipSeverity.Success });
            card.Children.Add(Text(anchor.First == 0 ? "Whole file" : $"{anchor.Side} · lines {anchor.First}–{anchor.Last}"));
            foreach (var e in thread.Events)
            {
                card.Children.Add(Text($"{e.Actor} · {e.Action} · {e.At.LocalDateTime:g}"));
                card.Children.Add(Text(e.Body));
            }
            var actions = new WrapRow { Spacing = 8 };
            actions.Children.Add(Button("Context", "\uE8A5", () => _ = ShowContext(thread), "ReviewContext_" + thread.Id));
            actions.Children.Add(Button("Reply", "\uE97A", () => _ = Address(thread, "reply"), "ReviewReply_" + thread.Id));
            var action = thread.State == "open" ? "resolve" : "reopen";
            actions.Children.Add(Button(thread.State == "open" ? "Resolve" : "Reopen", "\uE73E", () => _ = Address(thread, action), "ReviewAddress_" + thread.Id));
            card.Children.Add(actions);
            _threads.Children.Add(new Border { Child = card, Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] });
        }
        if (shown.Length > _visibleThreads) _threads.Children.Add(Button("Show more comments", "\uE70D", () => { _visibleThreads += 20; RenderThreads(); }, "CodeReviewMore"));
    }
    async Task<ReviewContext?> ShowContext(CodeThread thread)
    {
        using var read = _reads.Begin();
        var context = await read.Run(_pane, () => CodeReview.Context(Session.Require(), _path, thread.Id), Error);
        if (context != null && read.Current)
        {
            _file = null; _comment.IsEnabled = false; _diff.SetActions([]);
            _diff.Show(context.Original, context.Current ?? "", DiffView.LanguageFor(thread.Anchor.File), context.Current ?? context.Original, thread.Anchor.File + " · " + context.Location);
        }
        return context;
    }
    async Task NewComment(bool selected = false)
    {
        if (_writing || _file == null) return;
        var file = _file;
        var range = selected ? _diff.Selection : null;
        var side = new ComboBox { Header = "Code version", ItemsSource = new[] { "modified", "original" }, SelectedIndex = 0 };
        var first = new NumberBox { Header = "First line (0 for whole file)", Value = range?.First ?? 0, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var last = new NumberBox { Header = "Last line", Value = range?.Last ?? 0, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var body = new TextBox { Header = "Feedback", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxLength = 32000 };
        AutomationProperties.SetAutomationId(body, "ReviewCommentBody");
        var content = new StackPanel { Spacing = 12 }; content.Children.Add(Text(file.File)); content.Children.Add(side); content.Children.Add(first); content.Children.Add(last); content.Children.Add(body);
        _writing = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Leave code review feedback", Content = content, PrimaryButtonText = "Save comment", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = false };
            body.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(body.Text);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (double.IsNaN(first.Value) || double.IsNaN(last.Value) || first.Value > int.MaxValue || last.Value > int.MaxValue || first.Value != Math.Truncate(first.Value) || last.Value != Math.Truncate(last.Value)) throw new SgException("Enter whole line numbers.");
            var selectedSide = (string)side.SelectedItem; var firstLine = (int)first.Value; var lastLine = (int)last.Value; var feedback = body.Text;
            var root = Session.Require();
            await Task.Run(() => CodeReview.Add(root, _path, file, selectedSide, firstLine, lastLine, feedback));
            await Reload();
        }
        catch (Exception e) { Error(e.Message); }
        finally { _writing = false; }
    }
    async Task Address(CodeThread thread, string action)
    {
        if (_writing) return;
        _writing = true;
        try
        {
            var context = await ShowContext(thread);
            if (context == null) return;
            var body = new TextBox { Header = action == "resolve" ? "What changed or why no change is needed" : "Reply", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxLength = 32000 };
            AutomationProperties.SetAutomationId(body, "ReviewAddressBody");
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = action == "resolve" ? "Resolve feedback" : action == "reopen" ? "Reopen feedback" : "Reply to feedback", Content = body, PrimaryButtonText = action == "resolve" ? "Resolve" : "Save", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = false };
            body.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(body.Text);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var explanation = body.Text;
            await Task.Run(() => CodeReview.Address(Session.Require(), _path, thread.Id, action, explanation, context.Thread.Revision, version: context.Version));
            await Reload();
        }
        catch (Exception e) { Error(e.Message); }
        finally { _writing = false; }
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
