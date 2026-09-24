using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sg.Core;

/// <summary>Lives in &lt;root&gt;/.sg/sg.json.</summary>
public sealed class SgConfig
{
    public int Version { get; set; } = 1;
    public string Root { get; set; } = "";
    /// <summary>Where new branch worktrees go. Default: the root folder.</summary>
    public string? WorktreeRoot { get; set; }
    public string GitExe { get; set; } = "git";
    public string SvnExe { get; set; } = "svn";
    public string SvnMuccExe { get; set; } = "svnmucc";
    public int MinMessageLength { get; set; } = 10;
    /// <summary>For repositories that break the trunk/branches rule. Key: URL prefix. Value: replacement with {name} in it.</summary>
    public Dictionary<string, string> BranchUrlOverrides { get; set; } = new();
    /// <summary>When false, sg push refuses to run without an interactive terminal.</summary>
    public bool AllowAgentPush { get; set; }
    /// <summary>
    /// The command that settles the files a rebase or an import stops on: it runs in the worktree, gets
    /// the prompt on stdin and the file list in SG_FILES, and edits the files in place. Null runs Claude
    /// Code the way Resolver.DefaultCommand says.
    /// </summary>
    public string? ResolveCommand { get; set; }
    public List<ReviewCheckConfig> ReviewChecks { get; set; } = new();
    public List<CheckoutConfig> Checkouts { get; set; } = new();
    /// <summary>Where the branches, the uncommitted changes and the shelves are copied to, as thin histories. Null when nowhere.</summary>
    public BackupConfig? Backup { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static SgConfig Load(string path) =>
        JsonSerializer.Deserialize<SgConfig>(File.ReadAllText(path), JsonOptions) ?? throw new SgException("bad config: " + path);

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + "\n");
}

/// <summary>
/// One checkout that the tool mirrors: an SVN working copy, or a git clone tracking a branch on its
/// remote. Its snapshot ref is refs/remotes/svn/&lt;Name&gt; either way.
/// </summary>
public sealed class CheckoutConfig
{
    public string Name { get; set; } = "";
    /// <summary>The server behind it. A config written before git checkouts existed has none, and means SVN.</summary>
    public CheckoutKind Kind { get; set; } = CheckoutKind.Svn;
    /// <summary>Git: the remote the checkout's branch tracks, usually origin. Null for SVN.</summary>
    public string? Remote { get; set; }
    /// <summary>Git: the branch on that remote. Null for SVN.</summary>
    public string? Branch { get; set; }
    /// <summary>Managed PNG filename in .sg/icons; empty explicitly selects initials, null has no local choice.</summary>
    public string? Icon { get; set; }
    public string Path { get; set; } = "";
    /// <summary>Where on the server it is. SVN: the URL. Git: the remote's URL and the branch, written url#branch.</summary>
    public string Url { get; set; } = "";
    /// <summary>SVN: the repository root. Git: the remote's URL.</summary>
    public string ReposRoot { get; set; } = "";
    /// <summary>Relative paths that never enter git.</summary>
    public List<string> Skip { get; set; } = new();
    /// <summary>Skipped paths that every new worktree gets from this checkout, the way Shared says.</summary>
    public List<string> Junctions { get; set; } = new();
    /// <summary>How a worktree gets the Junctions folders. Older configs have no field and mean a junction.</summary>
    public SharedMode Shared { get; set; } = SharedMode.Junction;
    /// <summary>Tracked paths that a worktree may leave out with --minimal.</summary>
    public List<string> Optional { get; set; } = new();
    /// <summary>Working copies (relative paths, "" is the root) in the order push commits them. Others follow alphabetically.</summary>
    public List<string> PushOrder { get; set; } = new();

    [JsonIgnore] public bool IsGit => Kind == CheckoutKind.Git;
}

/// <summary>How a worktree gets a folder it shares with its checkout, like libs/prebuilt.</summary>
public enum SharedMode
{
    /// <summary>A junction into the checkout's folder. One folder on disk; every worktree sees the checkout's writes.</summary>
    Junction,
    /// <summary>A ReFS block clone: a private folder that shares disk with the checkout until one side writes.</summary>
    Clone,
    /// <summary>A full copy. Works on any volume, and costs the folder's size again.</summary>
    Copy,
}

/// <summary>
/// The git repository backups go to. It is a URL and never a git remote, on purpose: a "git push" typed
/// in a worktree keeps having nowhere to go, where a remote named backup would have sent the real
/// branch, snapshot and all, the first time someone typed "git push backup".
/// </summary>
public sealed class BackupConfig
{
    public string Url { get; set; } = "";
    /// <summary>A folder every ref sits under on the remote, for two machines that back up into one repository. Empty is none.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>The changes not yet committed go too, as one commit above the branch, and the local edits of a checkout above its snapshot.</summary>
    public bool Uncommitted { get; set; } = true;
    /// <summary>A file bigger than this many MB stays out of the backup, named. GitHub refuses a file over 100 MB outright. 0 is no limit.</summary>
    public int MaxFileMb { get; set; } = 100;
    /// <summary>
    /// The most one push carries, in MB. A backup bigger than this goes as several pushes, and one thing
    /// bigger on its own stays here and is named. GitHub drops a push past 2 GB with an HTTP 500. 0 is no limit.
    /// </summary>
    public int MaxPushMb { get; set; } = 1024;
    /// <summary>
    /// Worktrees left out, by branch. Nothing of one goes: not the branch, not its uncommitted changes, not
    /// its shelves. For a branch too big for the remote, or work that must not leave this machine.
    /// </summary>
    public List<string> Excluded { get; set; } = new();

    [JsonIgnore] public long MaxFileBytes => MaxFileMb > 0 ? MaxFileMb * (1L << 20) : 0;
    [JsonIgnore] public long MaxPushBytes => MaxPushMb > 0 ? MaxPushMb * (1L << 20) : 0;
}
