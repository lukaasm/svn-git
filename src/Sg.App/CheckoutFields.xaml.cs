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
    public string ReadyReason => _checking ? "Checking the checkout folder…" : Check.IsOpen ? Check.Message : FromUrl ? "Enter the SVN or git URL to check out." : "Choose an SVN working copy or a git clone.";

    void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        var url = FromUrl;
        UrlBox.Visibility = url ? Visibility.Visible : Visibility.Collapsed;
        FolderBox.Header = url ? "Check out into" : "Checkout folder";
        FolderBox.PlaceholderText = url ? "the root folder plus the name" : "D:\\work\\monorepo";
        ToolTipService.SetToolTip(BrowseButton, url
            ? "Pick the folder to check out under. The last part of the URL, or a git URL's branch, is added to it."
            : "Pick the working copy svn checked out, or a git clone whose branch tracks the server.");
        AutoFillFolder();
        Revalidate();
    }

    /// <summary>
    /// The last part of a URL, which is what a checkout of it is called when nothing else says: an SVN
    /// URL's last folder, a git URL's branch, or the repository when it names no branch.
    /// </summary>
    static string LastPart(string url) =>
        GitLocation.KindOfUrl(url) == CheckoutKind.Git
            ? (GitLocation.Parse(url).Branch ?? GitLocation.RepoName(url)).Replace('/', '-')
            : url.TrimEnd('/').Split('/').LastOrDefault() ?? "";

    /// <summary>The name, or the URL's last part, under the root, until the user writes a folder of their own.</summary>
    void AutoFillFolder()
    {
        if (!FromUrl || _folderTyped) return;
        var last = Name.Length > 0 ? Name : LastPart(Url);
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
        var rootPath = Root?.RootPath;
        Ok = false; _checking = true; Check.IsOpen = false; Changed?.Invoke();
        try
        {
            var result = await Task.Run(() => (Validation: Problem(fromUrl, url, folder, name, checkouts, rootPath), Sharing: SharedModeBox.Check(folder, worktreeRoot)));
            if (request != _validation) return;
            var (problem, severity) = result.Validation;
            SharedBox.Apply(result.Sharing);
            Ok = problem == null; _checking = false;
            // A git clone or a git URL is said so, while it is good: the reader learns which server this is before the long step.
            var note = problem == null ? GitNote(fromUrl, url, folder) : null;
            Check.IsOpen = problem != null || note != null;
            Check.Severity = problem != null ? severity : InfoBarSeverity.Success;
            Check.Message = problem ?? note ?? "";
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
    static (string? Problem, InfoBarSeverity Severity) Problem(bool fromUrl, string url, string folder, string name, (string Name, string Path)[] checkouts, string? rootPath)
    {
        if (fromUrl) return UrlProblem(url, folder, name, checkouts);
        if (folder.Length == 0) return (null, InfoBarSeverity.Error);
        if (!Directory.Exists(folder)) return ("No such folder.", InfoBarSeverity.Error);

        var trimmed = folder.TrimEnd('\\', '/');
        var already = checkouts.FirstOrDefault(c =>
            c.Path.TrimEnd('\\', '/').Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (already.Name != null) return ($"Already registered in this root as '{already.Name}'.", InfoBarSeverity.Informational);

        var git = Path.Combine(folder, ".git");
        if (File.Exists(git) || Directory.Exists(git))
        {
            // A .git file that points into an sg store is a worktree of that root, not a clone of a server.
            var owner = OwningRoot(git);
            if (owner != null)
                return ($"Another sg root already holds this checkout: {owner}. Remove it there first, or open that root instead.", InfoBarSeverity.Error);
            var marked = GitDirOf(git) is { } gd ? GitCheckoutVcs.ReadRootMarker(gd) : null;
            if (marked != null && File.Exists(Path.Combine(marked, ".sg", "sg.json"))
                && !marked.TrimEnd('\\', '/').Equals(rootPath?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return ($"Another sg root already holds this git clone: {marked}. Open that root instead.", InfoBarSeverity.Error);
            var tracking = CloneProblem(trimmed);
            if (tracking != null) return (tracking, InfoBarSeverity.Error);
        }
        else if (!Directory.Exists(Path.Combine(folder, ".svn")))
            return ("This is neither an SVN working copy nor a git clone: it has no .svn and no .git. Pick the folder svn checked out, or the clone's own folder.", InfoBarSeverity.Error);

        name = name.Length > 0 ? name : Path.GetFileName(trimmed);
        if (checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return ($"The name '{name}' is taken in this root. Give it another one below.", InfoBarSeverity.Error);
        return (null, InfoBarSeverity.Error);
    }

    /// <summary>Every reason CheckoutFromUrl would refuse before it talks to the server.</summary>
    static (string? Problem, InfoBarSeverity Severity) UrlProblem(string url, string folder, string name, (string Name, string Path)[] checkouts)
    {
        if (url.Length == 0) return (null, InfoBarSeverity.Error);
        var isGit = GitLocation.KindOfUrl(url) == CheckoutKind.Git;
        if (!isGit && !url.Contains("://"))
            return ("Give a full URL, like https://svn.example.com/svn/monorepo/branches/main, or a git one like https://host/repo.git#main.", InfoBarSeverity.Error);
        if (folder.Length == 0) return (null, InfoBarSeverity.Error);
        if (File.Exists(folder)) return ("A file is in the way of that folder.", InfoBarSeverity.Error);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return ("That folder exists and is not empty. A working copy already on disk is registered with the other choice above.", InfoBarSeverity.Error);
        name = name.Length > 0 ? name : LastPart(url);
        if (checkouts.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return ($"The name '{name}' is taken in this root. Give it another one below.", InfoBarSeverity.Error);
        return (null, InfoBarSeverity.Error);
    }

    /// <summary>
    /// Why a git clone cannot be registered, asked of git itself: it must be the clone's own folder, on a
    /// branch, and that branch must track a branch of a remote. Null when it can.
    /// </summary>
    static string? CloneProblem(string folder)
    {
        var git = new GitRepo(Session.Root?.Config.GitExe ?? "git", folder, new NullLog());
        var top = git.Run("rev-parse", "--show-toplevel");
        if (!top.Ok) return "This .git is not one git can read: " + top.StdErr.Trim().Split('\n')[0];
        if (!Path.GetFullPath(top.StdOut.Trim()).TrimEnd('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase))
            return "This folder is inside a git clone. Pick the clone's own folder.";
        var (local, remote, branch) = git.Tracking();
        if (local == null) return "HEAD is detached in this clone. Check out the branch that tracks the server first.";
        if (remote == null || branch == null) return $"Branch {local} tracks no remote branch. Set one with: git branch -u origin/{local}";
        return null;
    }

    /// <summary>The note a good git choice gets: which branch of which remote, and what registering it costs.</summary>
    static string? GitNote(bool fromUrl, string url, string folder)
    {
        if (fromUrl)
        {
            if (url.Length == 0 || GitLocation.KindOfUrl(url) != CheckoutKind.Git) return null;
            var branch = GitLocation.Parse(url).Branch;
            return "A git repository. " + (branch != null ? $"Branch {branch} is cloned" : "Its default branch is cloned")
                   + ", and its history is copied into the store once.";
        }
        var dotGit = Path.Combine(folder, ".git");
        if (folder.Length == 0 || !(Directory.Exists(dotGit) || File.Exists(dotGit))) return null;
        var (local, remote, tracked) = new GitRepo(Session.Root?.Config.GitExe ?? "git", folder, new NullLog()).Tracking();
        return $"A git clone: {local} tracks {remote}/{tracked}. The clone stays yours; sg copies the branch's history into its store once and writes nothing into the folder.";
    }

    /// <summary>The folder a .git names: itself when it is a folder, what it points at when it is a file.</summary>
    static string? GitDirOf(string gitPath)
    {
        if (Directory.Exists(gitPath)) return gitPath;
        try
        {
            var line = File.ReadAllText(gitPath).Trim();
            if (!line.StartsWith("gitdir:")) return null;
            var gd = line[7..].Trim();
            return Path.IsPathRooted(gd) ? gd : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitPath)!, gd));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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
        var last = LastPart(Url);
        FolderBox.Text = FromUrl && last.Length > 0 && !last.Contains(':') ? Path.Combine(path, last) : path;
    }

}
