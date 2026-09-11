using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// What 'sg checkout add' needs, asked once and used by both the places that ask it: the first
/// checkout of a new root, and one added to a root already open. It answers whether the folder can
/// be registered while the path is being typed, because the alternative is finding out after the
/// step that hashes sixteen gigabytes.
/// </summary>
public sealed partial class CheckoutFields : UserControl
{
    /// <summary>The window or page the folder picker belongs to. A picker needs a real window handle.</summary>
    public object? Owner { get; set; }

    SgRoot? _root;

    /// <summary>The root the folder would join, so "already registered" can be answered, and where a URL checks out into.</summary>
    public SgRoot? Root
    {
        get => _root;
        set { _root = value; AutoFillFolder(); }
    }

    /// <summary>The fields changed, or the verdict did. Hosts use it to enable their own button.</summary>
    public event Action? Changed;

    /// <summary>The folder box is being written by the URL, not by the user.</summary>
    bool _filling;
    /// <summary>The user typed a folder of their own; the URL stops suggesting one.</summary>
    bool _folderTyped;

    public CheckoutFields()
    {
        InitializeComponent();
        // Revalidate reads the disk: two Directory.Exists, a directory listing to see whether a folder is
        // empty, a config read, and a volume query for each of two paths to answer whether a ReFS clone
        // could work. Running all of that per letter typed made the form stutter on a network path, so it
        // runs once the typing pauses instead. It stays on the UI thread on purpose: it reads and writes
        // the controls, and touching those from a pool thread throws.
        _check.Tick += (_, _) => { _check.Stop(); Revalidate(); };
        FolderBox.TextChanged += (_, _) =>
        {
            if (!_filling) _folderTyped = FolderBox.Text.Trim().Length > 0;
            Later();
        };
        UrlBox.TextChanged += (_, _) => { AutoFillFolder(); Later(); };
        NameBox.TextChanged += (_, _) => { AutoFillFolder(); Later(); };
        SharedBox.Changed += () => Changed?.Invoke();
    }

    readonly Microsoft.UI.Xaml.DispatcherTimer _check = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>Check once the typing stops. Every other caller wants the answer now and calls Revalidate.</summary>
    void Later()
    {
        _check.Stop();
        _check.Start();
    }

    /// <summary>The checkout is still to be made, from the URL.</summary>
    public bool FromUrl => Source.SelectedIndex == 1;

    public string Url => UrlBox.Text.Trim();
    public string Folder => FolderBox.Text.Trim();
    public string Name => NameBox.Text.Trim();
    public List<string> Skip => SkipPick.Paths;
    public List<string> Junctions => JunctionPick.Paths;
    public SharedMode Shared => SharedBox.Mode;
    public List<string> Optional => OptionalPick.Paths;

    /// <summary>Nothing stands in the way of registering this folder.</summary>
    public bool Ok { get; private set; }

    /// <summary>Ok, and the field that names the checkout is filled in. The host's button follows this.</summary>
    public bool Ready => Ok && (FromUrl ? Url.Length > 0 : Folder.Length > 0);

    void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        var url = FromUrl;
        UrlBox.Visibility = url ? Visibility.Visible : Visibility.Collapsed;
        FolderBox.Header = url ? "Check out into" : "Checkout folder";
        FolderBox.PlaceholderText = url ? "the root folder plus the name" : "D:\\work\\monorepo";
        ToolTipService.SetToolTip(BrowseButton, url
            ? "Pick the folder to check out under. The last part of the URL is added to it."
            : "Pick the working copy that svn checked out. It must not already have a .git in it.");
        AutoFillFolder();
        Revalidate();
    }

    /// <summary>The name, or the URL's last part, under the root, until the user writes a folder of their own.</summary>
    void AutoFillFolder()
    {
        if (!FromUrl || _folderTyped) return;
        var last = Name.Length > 0 ? Name : Url.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        var suggested = last.Length == 0 || last.Contains(':') ? "" : Path.Combine(Root?.RootPath ?? "", last);
        _filling = true;
        try { FolderBox.Text = suggested; }
        finally { _filling = false; }
    }

    /// <summary>The folder whose contents the three lists are currently offering.</summary>
    string _offered = "";

    public new bool IsEnabled
    {
        set
        {
            foreach (var c in new Control[] { Source, UrlBox, FolderBox, BrowseButton, NameBox })
                c.IsEnabled = value;
            SkipPick.IsEnabled = value;
            JunctionPick.IsEnabled = value;
            SharedBox.IsEnabled = value;
            OptionalPick.IsEnabled = value;
        }
    }

    public void Revalidate()
    {
        _check.Stop();
        // A folder that is a real checkout can offer its own folders to the three lists below.
        var folder = FolderBox.Text.Trim();
        if (!FromUrl && folder != _offered && Directory.Exists(folder))
        {
            _offered = folder;
            _ = SkipPick.OfferFoldersOf(folder);
            _ = JunctionPick.OfferFoldersOf(folder);
            _ = OptionalPick.OfferFoldersOf(folder);
        }
        // The volumes answer whether a clone can work here. For a URL the folder is still to be made,
        // and its nearest existing parent answers for it.
        SharedBox.Detect(folder, Root?.Config.WorktreeRoot ?? Root?.RootPath);

        var (problem, severity) = Problem();
        Ok = problem == null;
        Check.IsOpen = problem != null;
        Check.Severity = severity;
        Check.Message = problem ?? "";
        Changed?.Invoke();
    }

    /// <summary>
    /// Every reason CheckoutAdd would refuse, checked here from the disk and the config alone. The
    /// order matters: say the first thing that is wrong, not all of them.
    /// </summary>
    (string? Problem, InfoBarSeverity Severity) Problem()
    {
        if (FromUrl) return UrlProblem();
        var folder = Folder;
        if (folder.Length == 0) return (null, InfoBarSeverity.Error);
        if (!Directory.Exists(folder)) return ("No such folder.", InfoBarSeverity.Error);

        if (!Directory.Exists(Path.Combine(folder, ".svn")))
            return ("This is not an SVN working copy: it has no .svn folder. Pick the folder svn checked out.", InfoBarSeverity.Error);

        var git = Path.Combine(folder, ".git");
        if (File.Exists(git) || Directory.Exists(git))
        {
            var owner = OwningRoot(git);
            return (owner != null
                ? $"Another sg root already holds this checkout: {owner}. Remove it there first, or open that root instead."
                : "This folder already has a .git. sg cannot register a folder that is already a git worktree.",
                InfoBarSeverity.Error);
        }

        var root = Root;
        if (root != null)
        {
            var trimmed = folder.TrimEnd('\\', '/');
            var already = root.Config.Checkouts.FirstOrDefault(c =>
                c.Path.TrimEnd('\\', '/').Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (already != null) return ($"Already registered in this root as '{already.Name}'.", InfoBarSeverity.Informational);

            var name = Name.Length > 0 ? Name : Path.GetFileName(trimmed);
            if (root.Config.Checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return ($"The name '{name}' is taken in this root. Give it another one below.", InfoBarSeverity.Error);
        }
        return (null, InfoBarSeverity.Error);
    }

    /// <summary>Every reason CheckoutFromUrl would refuse before it talks to the server.</summary>
    (string? Problem, InfoBarSeverity Severity) UrlProblem()
    {
        var url = Url;
        if (url.Length == 0) return (null, InfoBarSeverity.Error);
        if (!url.Contains("://")) return ("Give a full URL, like https://svn.example.com/svn/monorepo/branches/main.", InfoBarSeverity.Error);
        var folder = Folder;
        if (folder.Length == 0) return (null, InfoBarSeverity.Error);
        if (File.Exists(folder)) return ("A file is in the way of that folder.", InfoBarSeverity.Error);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return ("That folder exists and is not empty. A working copy already on disk is registered with the other choice above.", InfoBarSeverity.Error);
        var root = Root;
        if (root != null)
        {
            var name = Name.Length > 0 ? Name : url.TrimEnd('/').Split('/').Last();
            if (root.Config.Checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return ($"The name '{name}' is taken in this root. Give it another one below.", InfoBarSeverity.Error);
        }
        return (null, InfoBarSeverity.Error);
    }

    /// <summary>Which root a .git file points back at, so the refusal can name it instead of only stating it.</summary>
    static string? OwningRoot(string gitPath)
    {
        try
        {
            if (!File.Exists(gitPath)) return null;
            var line = File.ReadAllText(gitPath).Trim();
            if (!line.StartsWith("gitdir:")) return null;
            var gd = line[7..].Trim();
            if (!Path.IsPathRooted(gd)) gd = Path.Combine(Path.GetDirectoryName(gitPath)!, gd);
            // <root>/.sg/worktrees/<name> -> <root>
            var store = Path.GetFullPath(Path.Combine(gd, "..", ".."));
            return File.Exists(Path.Combine(store, "sg.json")) ? Path.GetDirectoryName(store) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Owner == null) return;
        var path = await WindowHelper.PickFolder(Owner);
        if (path == null) return;
        // For a URL the picker names the parent: the checkout itself does not exist yet.
        var last = Url.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        FolderBox.Text = FromUrl && last.Length > 0 && !last.Contains(':') ? Path.Combine(path, last) : path;
    }

}
