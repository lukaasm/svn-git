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
        // Debounce edits; disk and volume reads run on captured values off the UI thread.
        _check.Tick += (_, _) => { _check.Stop(); Revalidate(); };
        FolderBox.TextChanged += (_, _) =>
        {
            if (!_filling) _folderTyped = FolderBox.Text.Trim().Length > 0;
            Later();
        };
        UrlBox.TextChanged += (_, _) => { AutoFillFolder(); Later(); };
        NameBox.TextChanged += (_, _) => { AutoFillFolder(); Later(); };
        SharedBox.Changed += () => Changed?.Invoke();
        Loaded += (_, _) => { if (base.IsEnabled) Revalidate(); };
        Unloaded += (_, _) => { _check.Stop(); ++_validation; };
    }

    readonly Microsoft.UI.Xaml.DispatcherTimer _check = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>Check once the typing stops. Every other caller wants the answer now and calls Revalidate.</summary>
    void Later()
    {
        _check.Stop();
        ++_validation; Ok = false; _checking = true; Check.IsOpen = false;
        if (Folder != _offered) { SkipPick.ClearFolders(); JunctionPick.ClearFolders(); OptionalPick.ClearFolders(); _offered = ""; }
        Changed?.Invoke();
        if (base.IsEnabled) _check.Start();
    }

    /// <summary>The checkout is still to be made, from the URL.</summary>
    public bool FromUrl => Source.SelectedIndex == 1;

    public string Url => UrlBox.Text.Trim();
    public string Folder => FolderBox.Text.Trim();

    /// <summary>A folder handed in from outside, dropped on the window say, as if it had been typed.</summary>
    public void SetFolder(string path)
    {
        FolderBox.Text = path;
        _folderTyped = true;
        Revalidate();
    }
    public new string Name => NameBox.Text.Trim();
    public List<string> Skip => SkipPick.Paths;
    public List<string> Junctions => JunctionPick.Paths;
    public SharedMode Shared => SharedBox.Mode;
    public List<string> Optional => OptionalPick.Paths;

    /// <summary>Nothing stands in the way of registering this folder.</summary>
    public bool Ok { get; private set; }

    /// <summary>Ok, and the field that names the checkout is filled in. The host's button follows this.</summary>
    public bool Ready => Ok && (FromUrl ? Url.Length > 0 : Folder.Length > 0);
    public string ReadyReason => _checking ? "Checking the checkout folder…" : Check.IsOpen ? Check.Message : FromUrl ? "Enter the SVN URL to check out." : "Choose an SVN working-copy folder.";

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
            base.IsEnabled = value;
            if (!value) { _check.Stop(); ++_validation; _checking = false; }
            foreach (var c in new Control[] { Source, UrlBox, FolderBox, BrowseButton, NameBox })
                c.IsEnabled = value;
            SkipPick.IsEnabled = value;
            JunctionPick.IsEnabled = value;
            SharedBox.IsEnabled = value;
            OptionalPick.IsEnabled = value;
        }
    }

    int _validation;
    bool _checking;
    public async void Revalidate()
    {
        _check.Stop();
        var request = ++_validation;
        var folder = Folder; var url = Url; var name = Name; var fromUrl = FromUrl;
        var checkouts = Root?.Config.Checkouts.Select(c => (c.Name, c.Path)).ToArray() ?? [];
        var worktreeRoot = Root?.Config.WorktreeRoot ?? Root?.RootPath;
        Ok = false; _checking = true; Check.IsOpen = false; Changed?.Invoke();
        try
        {
            var result = await Task.Run(() => (Validation: Problem(fromUrl, url, folder, name, checkouts), Sharing: SharedModeBox.Check(folder, worktreeRoot)));
            if (request != _validation) return;
            var (problem, severity) = result.Validation;
            SharedBox.Apply(result.Sharing);
            Ok = problem == null; _checking = false;
            Check.IsOpen = problem != null; Check.Severity = severity; Check.Message = problem ?? "";
            Changed?.Invoke();
            if (!fromUrl && Ok && folder.Length > 0 && folder != _offered)
            {
                _offered = folder;
                await PathPicker.OfferFoldersOf(folder, SkipPick, JunctionPick, OptionalPick);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or SgException)
        {
            if (request != _validation) return;
            Ok = false; _checking = false;
            Check.IsOpen = true; Check.Severity = InfoBarSeverity.Error; Check.Message = "Could not check this folder: " + e.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Every reason CheckoutAdd would refuse, checked here from the disk and the config alone. The
    /// order matters: say the first thing that is wrong, not all of them.
    /// </summary>
    static (string? Problem, InfoBarSeverity Severity) Problem(bool fromUrl, string url, string folder, string name, (string Name, string Path)[] checkouts)
    {
        if (fromUrl) return UrlProblem(url, folder, name, checkouts);
        if (folder.Length == 0) return (null, InfoBarSeverity.Error);
        if (!Directory.Exists(folder)) return ("No such folder.", InfoBarSeverity.Error);

        if (!Directory.Exists(Path.Combine(folder, ".svn")))
            return ("This is not an SVN working copy: it has no .svn folder. Pick the folder svn checked out.", InfoBarSeverity.Error);

        var trimmed = folder.TrimEnd('\\', '/');
        var already = checkouts.FirstOrDefault(c =>
            c.Path.TrimEnd('\\', '/').Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (already.Name != null) return ($"Already registered in this root as '{already.Name}'.", InfoBarSeverity.Informational);

        var git = Path.Combine(folder, ".git");
        if (File.Exists(git) || Directory.Exists(git))
        {
            var owner = OwningRoot(git);
            return (owner != null
                ? $"Another sg root already holds this checkout: {owner}. Remove it there first, or open that root instead."
                : "This folder already has a .git. sg cannot register a folder that is already a git worktree.",
                InfoBarSeverity.Error);
        }

        name = name.Length > 0 ? name : Path.GetFileName(trimmed);
        if (checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return ($"The name '{name}' is taken in this root. Give it another one below.", InfoBarSeverity.Error);
        return (null, InfoBarSeverity.Error);
    }

    /// <summary>Every reason CheckoutFromUrl would refuse before it talks to the server.</summary>
    static (string? Problem, InfoBarSeverity Severity) UrlProblem(string url, string folder, string name, (string Name, string Path)[] checkouts)
    {
        if (url.Length == 0) return (null, InfoBarSeverity.Error);
        if (!url.Contains("://")) return ("Give a full URL, like https://svn.example.com/svn/monorepo/branches/main.", InfoBarSeverity.Error);
        if (folder.Length == 0) return (null, InfoBarSeverity.Error);
        if (File.Exists(folder)) return ("A file is in the way of that folder.", InfoBarSeverity.Error);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return ("That folder exists and is not empty. A working copy already on disk is registered with the other choice above.", InfoBarSeverity.Error);
        name = name.Length > 0 ? name : url.TrimEnd('/').Split('/').Last();
        if (checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return ($"The name '{name}' is taken in this root. Give it another one below.", InfoBarSeverity.Error);
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
