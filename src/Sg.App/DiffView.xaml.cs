using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Sg.App;

/// <summary>Side-by-side or inline diff in the Monaco editor inside a WebView2. Falls back to plain unified diff text when Monaco cannot load.</summary>
public sealed partial class DiffView : UserControl
{
    /// <summary>A run of lines on the modified side: where the cursor is, or what it has selected. 1-based, inclusive.</summary>
    public readonly record struct LineRange(int First, int Last);

    /// <summary>
    /// One button over the diff. The same list goes into the editor's own right click menu, so the
    /// keyboard reaches every one of them. Key is "ctrl+s", "ctrl+shift+s", "ctrl+r" or "ctrl+u".
    /// </summary>
    public sealed record DiffAction(string Id, string Label, string Glyph, string Tooltip, string? Key = null);

    /// <summary>A button or its menu entry was pressed. The id is the one the window gave.</summary>
    public event Action<string>? ActionInvoked;

    /// <summary>The cursor moved on the modified side, or the text under it changed. The window re-reads Selection.</summary>
    public event Action? SelectionChanged;

    /// <summary>The text in the editor stopped matching the file on disk, or matches it again.</summary>
    public event Action? DirtyChanged;

    bool _initialized, _ready, _failed, _editable, _dirty;
    /// <summary>The plain or unified view is on show, not the two sided diff of one file.</summary>
    bool _textView;
    string? _pendingJson;
    string _pendingText = "";
    LineRange? _selection;
    readonly List<DiffAction> _actions = new();
    readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    /// <summary>Started when a diff is asked for. The header says how long the read and the drawing took.</summary>
    readonly System.Diagnostics.Stopwatch _clock = new();
    long _readMs;

    public DiffView()
    {
        InitializeComponent();
        InlineToggle.IsChecked = Session.Settings.DiffInline;
        CollapsedToggle.IsChecked = Session.Settings.DiffCollapsed;
        WhitespaceToggle.IsChecked = Session.Settings.DiffIgnoreWhitespace;
        Loaded += OnLoaded;
        // A theme picked in Settings repaints every diff already on screen, not only the next one opened.
        Themes.Changed += SendTheme;
        // A page that was left is dropped, and the browser inside it must go too: a WebView2 is a
        // process, and three pages back would otherwise still hold three of them. Unloaded also
        // fires on a re-parent, so the check waits a beat and only closes what stayed unloaded.
        Unloaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsLoaded) return;
            Themes.Changed -= SendTheme;
            // Before Close, not after: _ready is what every send is guarded on, and the moment the
            // browser is closed there is nothing to send to. A page that is still holding this control
            // can go on calling it - a list selection that fires on the way out is exactly that.
            _ready = false;
            try { Web.Close(); }
            catch (Exception) { /* already gone */ }
        });
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            await Web.EnsureCoreWebView2Async();
            var core = Web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("sg.app", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, a) =>
            {
                string msg;
                try { msg = a.TryGetWebMessageAsString(); }
                catch { return; }
                if (msg == "ready")
                {
                    _ready = true;
                    SetMode("Monaco");
                    SyncToggles();
                    // Before the layout and before anything is flushed: the first diff of a run should
                    // arrive already wearing the colours, not repaint a moment after it appears.
                    SendTheme();
                    SendLayout();
                    SendActions();
                    Flush();
                }
                else if (msg.StartsWith("error:")) UseFallback(msg[6..]);
                else if (msg.StartsWith("sel:")) OnSelection(msg[4..]);
                else if (msg.StartsWith("act:")) ActionInvoked?.Invoke(msg[4..]);
                else if (msg == "dirty") SetDirty(true);
                else if (msg == "shown")
                {
                    // Where the time went: the git or svn read, then Monaco laying the text out.
                    var drawn = _clock.ElapsedMilliseconds - _readMs;
                    // The numbers are for whoever turned the verbose log on. Every other reader gets the
                    // tooltip, because a diff header is for the path, not for a stopwatch.
                    SetMode(Session.Settings.Verbose ? $"Monaco  {_readMs} + {drawn} ms" : "Monaco");
                    ToolTipService.SetToolTip(ModeText, $"The read took {_readMs} ms, drawing it took {drawn} ms.");
                    Session.Log.Cmd($"diff: read {_readMs} ms, drawn {drawn} ms  {TitleText.Text}");
                }
            };
            core.Navigate("https://sg.app/monaco.html?vs=" + Uri.EscapeDataString(MonacoUrl()));
        }
        catch (Exception ex)
        {
            UseFallback("diff viewer unavailable: " + ex.Message);
        }
    }

    /// <summary>
    /// The copy shipped next to the exe wins, so diffs are syntax coloured with no internet.
    /// The setting is the fallback, for a build without the bundle or to pin another version.
    /// </summary>
    static string MonacoUrl()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "monaco", "vs", "loader.js");
        return File.Exists(bundled) ? "https://sg.app/monaco/vs" : Session.Settings.MonacoUrl;
    }

    /// <summary>
    /// What the viewer is, when that is worth saying. Monaco doing its job is not: the word took the
    /// width the path needed, and in a narrow pane the path was trimmed to five letters. Loading,
    /// falling back to plain text, and the timings behind the verbose setting all still say it.
    /// </summary>
    void SetMode(string text)
    {
        ModeText.Text = text;
        ModeText.Visibility = text == "Monaco" ? Visibility.Collapsed : Visibility.Visible;
    }

    void UseFallback(string why)
    {
        _failed = true;
        // Plain text has no side by side, so the toggle would do nothing. Say so instead of pretending.
        SetMode("plain text");
        ToolTipService.SetToolTip(ModeText, why);
        SyncToggles();
        Web.Visibility = Visibility.Collapsed;
        FallbackScroll.Visibility = Visibility.Visible;
        Fallback.Text = why + "\n\n" + _pendingText;
        SetLoading(false);
        SyncActions();
    }

    /// <summary>The window is reading the two sides. The bar under the header runs until Show arrives.</summary>
    public void BeginLoading(string? title = null)
    {
        if (title != null) TitleText.Text = title;
        _clock.Restart();
        SetLoading(true);
    }

    void SetLoading(bool on)
    {
        LoadingBar.IsIndeterminate = on;
        Motion.FadeTo(LoadingBar, on ? 1 : 0);
    }

    /// <summary>
    /// Shows two versions of a file. The unified text is what the fallback shows. editable lets the
    /// reader type in the modified side, which is the file on disk; the window writes it back.
    /// </summary>
    public void Show(string original, string modified, string language, string unifiedFallback, string? title = null, bool editable = false)
    {
        TitleText.Text = title ?? "";
        _textView = false;
        _editable = editable;
        _pendingJson = JsonSerializer.Serialize(new { original, modified, language, editable });
        _pendingText = unifiedFallback;
        Present();
    }

    /// <summary>Two versions of one file, and the unified patch between them where there is one.</summary>
    public readonly record struct Sides(string Original, string Modified, string Unified = "");

    /// <summary>
    /// The reads that make the two sides, given separately so they run at once. On the SVN pages
    /// each one is a round trip to the server, and run one after another they were the whole wait.
    /// </summary>
    public readonly record struct Reads(Func<string> Original, Func<string> Modified, Func<string>? Unified = null);

    /// <summary>The same as below, with the reads run in parallel.</summary>
    public async Task<Sides?> ShowFileAsync(string path, string title, Reads reads,
        Func<bool> stillWanted, bool editable = false, string binaryNote = "binary file: ")
    {
        BeginLoading(title);
        var original = Task.Run(reads.Original);
        var modified = Task.Run(reads.Modified);
        var unified = reads.Unified == null ? Task.FromResult("") : Task.Run(reads.Unified);
        await Task.WhenAll(original, modified, unified);
        if (!stillWanted()) return null;
        var sides = new Sides(original.Result, modified.Result, unified.Result);
        if (Session.LooksBinary(sides.Original) || Session.LooksBinary(sides.Modified))
        {
            ShowText(binaryNote + path, title);
            return null;
        }
        Show(sides.Original, sides.Modified, LanguageFor(path), sides.Unified, title, editable);
        return sides;
    }

    /// <summary>
    /// The shape every window that lists files repeats: read the two versions off the UI thread,
    /// throw the answer away if the user moved on while it was being read, and show it. A binary file
    /// is the one thing no diff view can help with, so it says so instead. Null back means nothing
    /// was shown, either because the answer was stale or because it was binary.
    /// </summary>
    public async Task<Sides?> ShowFileAsync(string path, string title, Func<Sides> read,
        Func<bool> stillWanted, bool editable = false, string binaryNote = "binary file: ")
    {
        BeginLoading(title);
        var sides = await Task.Run(read);
        if (!stillWanted()) return null;
        if (Session.LooksBinary(sides.Original) || Session.LooksBinary(sides.Modified))
        {
            ShowText(binaryNote + path, title);
            return null;
        }
        Show(sides.Original, sides.Modified, LanguageFor(path), sides.Unified, title, editable);
        return sides;
    }

    public void ShowText(string text, string? title = null)
    {
        TitleText.Text = title ?? "";
        _textView = true;
        _editable = false;
        _pendingJson = JsonSerializer.Serialize(new { text, language = "plaintext" });
        _pendingText = text;
        Present();
    }

    const int MaxUnified = 1_500_000;

    /// <summary>A unified diff over many files, colored line by line.</summary>
    public void ShowUnified(string diff, string? title = null)
    {
        if (diff.Length > MaxUnified) diff = diff[..MaxUnified] + "\n\n... cut here, the diff is too long. Pick single files.\n";
        if (diff.Trim().Length == 0) diff = "(no text changes)";
        TitleText.Text = title ?? "";
        _textView = true;
        _editable = false;
        _pendingJson = JsonSerializer.Serialize(new { text = diff, language = "unified-diff" });
        _pendingText = diff;
        Present();
    }

    void Present()
    {
        if (!_clock.IsRunning) _clock.Restart();
        _readMs = _clock.ElapsedMilliseconds;
        _selection = null;
        SetDirty(false);
        // A new file: every button goes back to its plain verb, so none of them still names the last file's lines.
        foreach (var a in _actions) SetActionState(a.Id, enabled: false, a.Label);
        SyncToggles();
        SyncActions();
        SetLoading(false);
        if (_failed) Fallback.Text = _pendingText;
        else Flush();
    }

    /// <summary>
    /// The one way anything reaches the page. _ready is not enough on its own: a WebView2 that has been
    /// closed leaves CoreWebView2 null behind it, and the control outlives that by however long the page
    /// holding it takes to go. A list that raises SelectionChanged while its page is being left called
    /// straight through to a browser that was not there any more, and the app went with it.
    /// </summary>
    bool Post(object message)
    {
        var core = _ready ? Web.CoreWebView2 : null;
        if (core == null) return false;
        core.PostWebMessageAsJson(message as string ?? JsonSerializer.Serialize(message));
        return true;
    }

    void Flush()
    {
        if (_pendingJson != null) Post(_pendingJson);
    }

    /// <summary>Jump to the next or previous change in the open diff.</summary>
    public void GoToDiff(bool next) => Post(new { command = "goto", next });

    /// <summary>
    /// The left end of the header, for whatever the window needs beside the diff itself. The commit
    /// window puts the switch between what is staged and what is not there.
    /// </summary>
    public Panel HeaderExtras => Extras;

    /// <summary>Where the cursor is on the modified side, or null when a patch or plain text is on show.</summary>
    public LineRange? Selection => _selection;

    /// <summary>The editor holds text the file on disk does not have yet.</summary>
    public bool IsDirty => _dirty;

    /// <summary>The reader may type in the modified side of what is on show.</summary>
    public bool IsEditable => _editable && !_textView && !_failed;

    /// <summary>Monaco says where the cursor is, or "null" when there is no two sided diff on show.</summary>
    void OnSelection(string json)
    {
        var before = _selection;
        _selection = null;
        if (json != "null")
        {
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (d != null && d.TryGetValue("s", out var s) && d.TryGetValue("e", out var e)) _selection = new LineRange(s, e);
            }
            catch (Exception) { /* not a selection */ }
        }
        SyncActions();
        if (!Equals(before, _selection)) SelectionChanged?.Invoke();
    }

    void SetDirty(bool on)
    {
        if (_dirty == on) return;
        _dirty = on;
        DirtyChip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SyncActions();
        DirtyChanged?.Invoke();
    }

    /// <summary>
    /// The buttons over the diff, and the same entries inside the editor's right click menu. The window
    /// sets them once; which of them are on and what they say comes from SetActionState as the cursor moves.
    /// </summary>
    public void SetActions(IEnumerable<DiffAction> actions)
    {
        var list = actions.ToList();
        if (_actions.SequenceEqual(list)) return;
        _actions.Clear();
        _actions.AddRange(list);
        _buttons.Clear();
        Actions.Children.Clear();
        foreach (var a in _actions)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new FontIcon { Glyph = a.Glyph, FontSize = 12 });
            var label = new TextBlock { Text = a.Label };
            panel.Children.Add(label);
            var button = new Button { Content = panel, FontSize = 12, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0, IsEnabled = false };
            button.Tag = label;
            AutomationProperties.SetName(button, a.Label);
            ToolTipService.SetToolTip(button, a.Tooltip + (a.Key == null ? "" : "  (" + a.Key.Replace("ctrl", "Ctrl").Replace("shift", "Shift") + ")"));
            var id = a.Id;
            button.Click += (_, _) => ActionInvoked?.Invoke(id);
            _buttons[id] = button;
            Actions.Children.Add(button);
        }
        SendActions();
        SyncActions();
    }

    /// <summary>Turns one button on or off, and gives it the words for what it would act on right now.</summary>
    public void SetActionState(string id, bool enabled, string? label = null)
    {
        if (!_buttons.TryGetValue(id, out var button)) return;
        button.IsEnabled = enabled && _ready && !_failed;
        if (label != null && button.Tag is TextBlock text) text.Text = label;
    }

    /// <summary>Everything the reader typed into the modified side, for the window to write to disk.</summary>
    public async Task<string> ModifiedTextAsync()
    {
        var core = _ready && !_failed ? Web.CoreWebView2 : null;
        if (core == null) return "";
        var json = await core.ExecuteScriptAsync("window.sgGetModified()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    /// <summary>The file on disk holds what the editor holds again.</summary>
    public void MarkSaved() => SetDirty(false);

    /// <summary>Puts the caret back in the diff, so the keyboard shortcuts over it work straight away.</summary>
    public void FocusText()
    {
        if (!_failed) Post(new { command = "focus" });
    }

    void SyncActions() => ActionRow.Visibility = _actions.Count > 0 && !_textView && !_failed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The palette, sent before anything is drawn in it. Every open diff gets it, so changing the theme
    /// in Settings repaints what is already on screen rather than only the next file opened.
    /// </summary>
    public void SendTheme()
    {
        var t = Themes.Current;
        Post(new
        {
            command = "theme",
            theme = new
            {
                dark = t.Dark,
                background = t.Background, foreground = t.Foreground,
                lineNumber = t.LineNumber, selection = t.Selection,
                keyword = t.Keyword, literal = t.Literal, comment = t.Comment,
                number = t.Number, type = t.Type, function = t.Function,
                added = t.Added, removed = t.Removed, modified = t.Modified,
                renamed = t.Renamed, conflict = t.Conflict, untracked = t.Untracked,
            },
        });
    }

    void SendActions() => Post(new
    {
        command = "actions",
        actions = _actions.Select(a => new { id = a.Id, label = a.Label, key = a.Key }).ToArray(),
    });

    /// <summary>
    /// The two switches over the diff. Collapsed is only offered for one file's diff: a patch over many
    /// files is already only the changes and the lines around them, so there would be nothing to hide.
    /// </summary>
    void SyncToggles()
    {
        InlineToggle.IsEnabled = _ready && !_failed;
        CollapsedToggle.IsEnabled = _ready && !_failed && !_textView;
        WhitespaceToggle.IsEnabled = _ready && !_failed && !_textView;
    }

    void SendLayout()
    {
        Post(new
        {
            command = "layout",
            sideBySide = InlineToggle.IsChecked != true,
            collapsed = CollapsedToggle.IsChecked == true,
            ignoreWhitespace = WhitespaceToggle.IsChecked == true,
        });
    }

    void WhitespaceToggle_Click(object sender, RoutedEventArgs e)
    {
        Session.Settings.DiffIgnoreWhitespace = WhitespaceToggle.IsChecked == true;
        Session.Settings.Save();
        SendLayout();
    }

    void InlineToggle_Click(object sender, RoutedEventArgs e)
    {
        Session.Settings.DiffInline = InlineToggle.IsChecked == true;
        Session.Settings.Save();
        SendLayout();
    }

    void CollapsedToggle_Click(object sender, RoutedEventArgs e)
    {
        Session.Settings.DiffCollapsed = CollapsedToggle.IsChecked == true;
        Session.Settings.Save();
        SendLayout();
    }

    public static string LanguageFor(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".cmake", StringComparison.OrdinalIgnoreCase)) return "cmake";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".inl" or ".hlsl" or ".glsl" or ".fx" => "cpp",
            ".cs" => "csharp",
            ".py" => "python",
            ".json" => "json",
            ".xml" or ".xaml" or ".vcxproj" or ".csproj" or ".props" or ".targets" => "xml",
            ".lua" => "lua",
            ".js" => "javascript",
            ".ts" => "typescript",
            ".md" => "markdown",
            ".yaml" or ".yml" => "yaml",
            ".bat" or ".cmd" => "bat",
            ".ps1" => "powershell",
            ".sh" => "shell",
            ".ini" or ".cfg" or ".toml" => "ini",
            ".html" or ".htm" => "html",
            ".css" => "css",
            _ => "plaintext",
        };
    }
}
