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

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + "\n");
}

/// <summary>One SVN checkout that the tool mirrors. Its snapshot ref is refs/remotes/svn/&lt;Name&gt;.</summary>
public sealed class CheckoutConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
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
}
