using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace Sg.App;

/// <summary>One step of the path over a page: "monorepo > feature-x > Commit". Go is null on the last one.</summary>
public sealed record Crumb(string Text, Action? Go = null)
{
    /// <summary>The steps behind step back in the quieter colour; the page itself is written in full.</summary>
    public Brush? Brush => Application.Current.Resources[Go == null ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"] as Brush;

    /// <summary>
    /// What a screen reader announces for the step. BreadcrumbBar names its items by what they hand
    /// back here, and a record hands back its own fields: the bar used to read out the brush.
    /// </summary>
    public override string ToString() => Text;
}

/// <summary>
/// A place in the app. The overview, the settings, a commit, a push: each is one of these, hosted by
/// the main window behind its navigation pane or by a window of its own when the Explorer menu opens
/// it straight. The host shows the title, the path and the back button; the page shows only its body.
/// </summary>
public abstract class SgPage : Page
{
    string _title = "";
    string _subtitle = "";
    string? _checkout;
    string? _branch;

    /// <summary>What the host calls this page. The last crumb when there are crumbs.</summary>
    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; HeaderChanged?.Invoke(); }
    }

    /// <summary>The line under the title: a path, a branch, a revision.</summary>
    public string Subtitle
    {
        get => _subtitle;
        set { if (_subtitle == value) return; _subtitle = value; HeaderChanged?.Invoke(); }
    }

    /// <summary>
    /// The checkout this page is about. The host writes the path over the page from it, the way
    /// Windows Settings writes "System > Display". Null on a page about the app itself.
    /// </summary>
    public string? Checkout
    {
        get => _checkout;
        set { if (_checkout == value) return; _checkout = value; HeaderChanged?.Invoke(); }
    }

    /// <summary>The branch this page is about, under the checkout. Null on a page about the checkout itself.</summary>
    public string? Branch
    {
        get => _branch;
        set { if (_branch == value) return; _branch = value; HeaderChanged?.Invoke(); }
    }

    /// <summary>Title, subtitle or crumbs changed. The host redraws its header.</summary>
    public event Action? HeaderChanged;

    /// <summary>The host this page is shown in, set when it is shown.</summary>
    public NavHost? Host { get; internal set; }

    /// <summary>The window this page is on screen in, or null before it is shown.</summary>
    public Window? Window => Host?.Window;

    /// <summary>Called every time the host puts this page on screen. returning is true for back and forward.</summary>
    public virtual void OnShown(bool returning) { }

    /// <summary>Called when the host takes this page off screen. A page that was made for one visit is dropped after this.</summary>
    public virtual void OnHidden() { }

    /// <summary>
    /// Leave this page: back to the one before it, or close the window when this is the only page in
    /// it. Every window used to have a Close button; this is what that button does now.
    /// </summary>
    protected void Close() => Host?.Leave(this);

    /// <summary>The page was left, by Close or by the back button. Whoever opened it reads its answer here.</summary>
    public event Action? Left;

    internal void RaiseLeft() => Left?.Invoke();

    /// <summary>Opens another page over this one, in the same host.</summary>
    protected void Go(Func<SgPage> make, string key) => Host?.Go(make, key);

    /// <summary>Shows a page over this one and runs a callback when the user comes back from it.</summary>
    protected void GoThen(Func<SgPage> make, string key, Action back) => Host?.Go(() =>
    {
        var p = make();
        p.Left += back;
        return p;
    }, key);
}

/// <summary>
/// The place pages are shown, with the back and forward stack Windows Settings has. Entries are
/// factories rather than pages: a page that was left is dropped, and coming back makes it again, so
/// it reads fresh state and nothing keeps a diff viewer alive behind three other pages. A factory may
/// hand back one long lived page every time, which is what the overview does.
/// </summary>
public sealed class NavHost : ContentControl
{
    sealed record Entry(Func<SgPage> Make, string Key);

    readonly List<Entry> _entries = new();
    int _index = -1;
    const int Depth = 30;

    /// <summary>The window this host is in. The host's owner sets it; pages ask for it.</summary>
    public Window? Window { get; set; }

    public NavHost()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    /// <summary>The page on screen, or null before the first Go.</summary>
    public SgPage? Current { get; private set; }

    /// <summary>The key of the page on screen. The main window matches it against its pane.</summary>
    public string? CurrentKey => _index >= 0 && _index < _entries.Count ? _entries[_index].Key : null;

    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    /// <summary>The page on screen changed, or its header did.</summary>
    public event Action? Changed;

    /// <summary>
    /// Shows a new page. Anything that was forward of here is forgotten, the way a browser does it.
    /// A key that equals the current one replaces the current page instead of stacking a copy: the
    /// pane selects the checkout that is already on screen and nothing should move.
    /// </summary>
    public void Go(Func<SgPage> make, string key)
    {
        if (_index >= 0 && _entries[_index].Key == key)
        {
            Show(_index, returning: false);
            return;
        }
        if (_index < _entries.Count - 1) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(new Entry(make, key));
        if (_entries.Count > Depth) _entries.RemoveAt(0);
        Show(_entries.Count - 1, returning: false);
    }

    public void Back()
    {
        if (CanGoBack) Show(_index - 1, returning: true);
    }

    public void Forward()
    {
        if (CanGoForward) Show(_index + 1, returning: true);
    }

    /// <summary>Back to the page a crumb names, when it is behind this one. False when it is not.</summary>
    public bool BackTo(string key)
    {
        for (var i = _index - 1; i >= 0; i--)
            if (_entries[i].Key == key) { Show(i, returning: true); return true; }
        return false;
    }

    /// <summary>A page asks to be left: back when there is a back, otherwise the window goes.</summary>
    public void Leave(SgPage page)
    {
        if (!ReferenceEquals(page, Current)) return;
        if (CanGoBack) Back();
        else Window?.Close();
    }

    void Show(int index, bool returning)
    {
        var previous = Current;
        if (previous != null)
        {
            previous.HeaderChanged -= OnHeaderChanged;
            previous.OnHidden();
            // After this navigation, never inside it. Whoever opened a page answers its Left, and some of
            // those answers navigate: the Incoming changes page asks for a sync, and the sync brings the
            // overview up. Raised here, that re-entered Show and moved the entry list and the index under
            // the call that was already running, which ended the app rather than the page.
            if (index != _index || !returning) DispatcherQueue.TryEnqueue(previous.RaiseLeft);
        }
        _index = index;
        var page = _entries[index].Make();
        page.Host = this;
        page.HeaderChanged += OnHeaderChanged;
        Current = page;
        if (!ReferenceEquals(Content, page)) { Content = page; Motion.Enter(page, returning); }
        page.OnShown(returning);
        Changed?.Invoke();
    }

    void OnHeaderChanged() => Changed?.Invoke();

    /// <summary>
    /// Alt+Left, Alt+Right, and the two thumb buttons on a mouse, on any host. Escape goes back too,
    /// unless a text box has the focus: a half written message is worth more than the shortcut.
    /// </summary>
    public void AttachShortcuts(FrameworkElement root, bool escapeCloses)
    {
        Shortcuts.Add(root, VirtualKey.Left, VirtualKeyModifiers.Menu, Back);
        Shortcuts.Add(root, VirtualKey.Right, VirtualKeyModifiers.Menu, Forward);
        Shortcuts.Add(root, VirtualKey.Escape, () =>
        {
            if (FocusManager.GetFocusedElement(root.XamlRoot) is TextBox) return;
            if (CanGoBack) Back();
            else if (escapeCloses) Window?.Close();
        });
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var p = e.GetCurrentPoint(root).Properties;
            if (p.IsXButton1Pressed) { Back(); e.Handled = true; }
            else if (p.IsXButton2Pressed) { Forward(); e.Handled = true; }
        }), handledEventsToo: true);
    }

    /// <summary>The host an element is shown in, by walking up the tree. Null for an element that is not on a page.</summary>
    public static NavHost? Of(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is NavHost host) return host;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
