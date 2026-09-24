namespace Sg.Core;

/// <summary>What a checkout's server speaks. Older configs have no field and mean SVN.</summary>
public enum CheckoutKind
{
    Svn,
    Git,
}

/// <summary>
/// One local change in a checkout, in svn's words: modified, added, deleted, unversioned, missing,
/// conflicted, replaced, obstructed. A git checkout reports its changes in the same words, so the
/// changes window, the shelf and the push checks read one vocabulary whichever server is behind them.
/// </summary>
public sealed class CheckoutChange
{
    public string Path = "";
    public string Item = "";
    public string Props = "";
    /// <summary>Working copy the change belongs to. "" is the root.</summary>
    public string Wc = "";
    public bool Versioned => Item is not ("unversioned" or "ignored");

    public string Code => Item switch
    {
        "modified" => "M",
        "added" => "A",
        "deleted" => "D",
        "unversioned" => "?",
        "missing" => "!",
        "conflicted" => "C",
        "replaced" => "R",
        "obstructed" => "~",
        "normal" => Props != "none" ? "P" : " ",
        _ => Item.Length > 0 ? Item[..1].ToUpperInvariant() : " ",
    };
}

public sealed record ChangedPath(string Action, string Path, string Kind, string? CopyFrom);

/// <summary>
/// One revision of a server's history. SVN names it by number. Git names it by commit, and Revision
/// is then its height on the branch's first-parent line: the count of commits up to and including it,
/// so newer is larger on a branch the way a revision number is.
/// </summary>
public sealed class LogRevision
{
    public long Revision;
    /// <summary>The commit, for a git server. Empty for SVN.</summary>
    public string Commit = "";
    public string Author = "";
    public string Date = "";
    public string Message = "";
    public List<ChangedPath> Paths = new();

    /// <summary>What a reader calls it: r266, or the short commit.</summary>
    public string Label => Rev.Label(Revision, Commit);
}

/// <summary>One line as the server blames it. A line the server has never seen has no revision and no commit.</summary>
public sealed record ServerBlameLine(int Number, long Revision, string Author, string Date, string Commit = "")
{
    public bool Known => Revision > 0 || Commit.Length > 0;
}

/// <summary>A revision number or a commit, as the server names what it just made.</summary>
public readonly record struct CommitId(long? Revision, string Commit)
{
    public string Label => Commit.Length > 0 ? Rev.Short(Commit) : Revision.HasValue ? "r" + Revision.Value : "";
}

/// <summary>How a revision or a commit is written for a reader.</summary>
public static class Rev
{
    public static string Label(long revision, string? commit) =>
        !string.IsNullOrEmpty(commit) ? Short(commit) : "r" + revision;

    public static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;
}

/// <summary>What bringing a checkout up to date with its server did.</summary>
public sealed class UpstreamUpdate
{
    public long? Revision;
    /// <summary>The commit a git checkout is now on. Empty for SVN.</summary>
    public string Commit = "";
    public int Conflicts;
    public string Output = "";
    /// <summary>Externals the update kept pointed somewhere other than svn:externals declares.</summary>
    public List<string> KeptSwitched = new();
    public List<string> Warnings = new();
}

/// <summary>What a working copy on disk is, read before it is registered.</summary>
public sealed class CheckoutIdentity
{
    public CheckoutKind Kind;
    /// <summary>Where on the server it is. SVN: the URL. Git: the remote's URL and the branch, as url#branch.</summary>
    public string Url = "";
    /// <summary>SVN: the repository root. Git: the remote's URL.</summary>
    public string ReposRoot = "";
    /// <summary>Git: the remote the branch tracks, usually origin.</summary>
    public string Remote = "";
    /// <summary>Git: the branch on that remote.</summary>
    public string Branch = "";
}

/// <summary>What push needs from a whole checkout, read in one walk: its local edits, and its externals.</summary>
public sealed record CheckoutScan(HashSet<string> LocalEdits, List<string> Externals);

/// <summary>
/// One history the log page reads: the checkout root, or one external. The paths in its log are the
/// repository's own; Prefix is the part of them that is this working copy's folder, so the rest is
/// the path inside the checkout.
/// </summary>
public sealed record HistorySource(string Wc, string Url, string ReposRoot, long WcRevision, string WcCommit, long? SnapshotRevision)
{
    public string Prefix { get; init; } = "";
    public string Label => Wc.Length == 0 ? "root" : Wc;
    public string WcLabel => Rev.Label(WcRevision, WcCommit);
}

/// <summary>
/// Everything sg asks of the server behind a checkout. The workflow above it - sync, snapshot, branch,
/// rebase, push, shelve, blame, log, merge, new server branch - is one workflow, and this is the seam
/// it goes through: SVN answers with svn, svnmucc and the snapshot builder, git answers with the
/// checkout's own clone and a fetch into the store.
///
/// Paths are relative to the checkout with forward slashes. "Working copy" means the root or one
/// external; a git checkout has only the root, "".
/// </summary>
public interface ICheckoutVcs
{
    CheckoutKind Kind { get; }

    /// <summary>What the server is called in a sentence: "SVN" or "Git".</summary>
    string ServerName { get; }

    // ---- registering ----

    /// <summary>What the folder is. Throws when it is not a working copy this server can register.</summary>
    CheckoutIdentity Describe(SgRoot root, string folder);

    /// <summary>A fresh working copy of a URL in folder, which is made if it is not there.</summary>
    void CheckoutUrl(SgRoot root, string url, string folder);

    bool UrlExists(SgRoot root, string url);

    /// <summary>The name a checkout of this URL gets when none is given.</summary>
    string NameFromUrl(string url);

    /// <summary>Lets the store see the checkout. Runs before the first snapshot; Detach undoes it when that fails.</summary>
    void Attach(SgRoot root, CheckoutConfig co);

    void Detach(SgRoot root, CheckoutConfig co);

    /// <summary>The checkout was renamed. Anything the store keeps under the old name moves.</summary>
    void Renamed(SgRoot root, CheckoutConfig co, string oldName);

    // ---- sync ----

    /// <summary>Brings the working copy up to the server's newest, keeping local edits. svn update; git fetch and fast-forward.</summary>
    UpstreamUpdate Update(SgRoot root, CheckoutConfig co);

    /// <summary>A snapshot of the checkout as the server has it: local edits and skipped paths left out.</summary>
    SnapshotInfo BuildSnapshot(SgRoot root, CheckoutConfig co, string? parentSha, Func<SnapshotInfo, string>? extraMessage);

    /// <summary>The server's log messages since the last snapshot, for the new snapshot's message.</summary>
    string LogsSince(SgRoot root, CheckoutConfig co, SnapshotMeta? prev, SnapshotInfo info);

    /// <summary>Whether the server moved past the snapshot, per working copy.</summary>
    RemoteCheckResult RemoteCheck(SgRoot root, CheckoutConfig co, SnapshotMeta snapshot, bool countCommits);

    /// <summary>gitignore lines the server's own ignore rules make, on top of the skip list.</summary>
    List<string> IgnoreLines(SgRoot root, CheckoutConfig co);

    // ---- local changes ----

    List<CheckoutChange> Changes(SgRoot root, CheckoutConfig co);

    int LocalEditCount(SgRoot root, CheckoutConfig co);

    CheckoutScan Scan(SgRoot root, CheckoutConfig co);

    /// <summary>Which of these paths have a local edit. Asked about a handful of paths, not the whole checkout.</summary>
    HashSet<string> LocalEditsOn(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths);

    bool HasConflicts(SgRoot root, CheckoutConfig co);

    /// <summary>Why the checkout cannot take a commit right now: nothing for SVN, which always can.</summary>
    List<string> WriteBlockers(SgRoot root, CheckoutConfig co);

    /// <summary>Schedules files the server does not have yet.</summary>
    void Add(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths);

    /// <summary>Schedules versioned files for deletion, and deletes them from disk.</summary>
    void Remove(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths);

    /// <summary>Puts versioned paths back the way the server has them. Returns what the tool complained about, or null.</summary>
    string? Revert(SgRoot root, CheckoutConfig co, IReadOnlyList<string> paths);

    /// <summary>Unified diff of the local changes under a path: "." for everything.</summary>
    string DiffLocal(SgRoot root, CheckoutConfig co, string path);

    /// <summary>A file as the server has it, which is what a local edit started from. Empty when there is none.</summary>
    string BaseText(SgRoot root, CheckoutConfig co, string path);

    /// <summary>Stops listing these names in a folder: svn:ignore, or the folder's .gitignore.</summary>
    void Ignore(SgRoot root, CheckoutConfig co, string folder, IEnumerable<string> names);

    /// <summary>Sends one working copy's share of chosen local changes to the server, as one commit.</summary>
    CommitId CommitChanges(SgRoot root, CheckoutConfig co, string wc, IReadOnlyList<CheckoutChange> changes, string message);

    // ---- push ----

    /// <summary>The repository each push group commits to, for the report.</summary>
    void FillRepositories(SgRoot root, CheckoutConfig co, List<PushGroup> groups);

    /// <summary>
    /// Puts one working copy's share of a branch into the checkout and schedules it: files written, adds
    /// added, deletes deleted, renames moved. It stops there, which is all a push that leaves the change
    /// in the checkout does.
    /// </summary>
    void WriteInto(SgRoot root, CheckoutConfig co, PushGroup g, string tip);

    /// <summary>Commits what WriteInto left, to the server.</summary>
    CommitId CommitWritten(SgRoot root, CheckoutConfig co, PushGroup g, string message);

    /// <summary>Puts a working copy back the way the failed step found it; restoreFrom is that step's start in the store.</summary>
    void Rollback(SgRoot root, CheckoutConfig co, PushGroup g, string restoreFrom, List<string> warnings);

    // ---- history ----

    List<HistorySource> HistorySources(SgRoot root, CheckoutConfig co);

    /// <summary>The server's history of one working copy, newest first, with the paths each revision touched.</summary>
    List<LogRevision> Log(SgRoot root, CheckoutConfig co, HistorySource source, int limit);

    /// <summary>What one revision changed, as a patch. folder narrows it to one folder of the log's paths.</summary>
    string RevisionDiff(SgRoot root, CheckoutConfig co, HistorySource source, LogRevision rev, string? folder);

    /// <summary>One file of the log before or after the revision. Empty when it did not exist.</summary>
    string FileAt(SgRoot root, CheckoutConfig co, HistorySource source, LogRevision rev, ChangedPath path, bool before);

    /// <summary>
    /// Who last changed each line of a file. asInSnapshot blames the version the snapshot holds, so its
    /// lines match a worktree's lines through git's original line numbers; otherwise the file on disk.
    /// </summary>
    List<ServerBlameLine> Blame(SgRoot root, CheckoutConfig co, string path, bool asInSnapshot);

    /// <summary>The revision a blamed line came from: its log entry, and what it did to the file.</summary>
    (LogRevision? Log, string Diff) BlameDetails(SgRoot root, CheckoutConfig co, string path, ServerBlameLine line);

    // ---- externals ----

    List<Ops.ExternalState> Externals(SgRoot root, CheckoutConfig co);

    UpstreamUpdate SwitchExternal(SgRoot root, CheckoutConfig co, string rel, string url);

    /// <summary>The branches a URL could be pointed at: its siblings on the server.</summary>
    List<string> BranchNames(SgRoot root, CheckoutConfig co, string url);

    string UrlForBranch(SgRoot root, CheckoutConfig co, string url, string branch);

    // ---- server branches and checkouts ----

    List<BranchPart> Parts(SgRoot root, CheckoutConfig co);

    ServerBranchPlan PlanBranch(SgRoot root, CheckoutConfig co, string name, string? message, IReadOnlyList<BranchPart>? parts);

    void ExecuteBranch(SgRoot root, CheckoutConfig co, ServerBranchPlan plan);

    /// <summary>A new checkout of a server branch, made from the nearest one on disk so only differences travel.</summary>
    CheckoutResult ServerCheckout(SgRoot root, CheckoutConfig near, string target, string? name);

    // ---- merging between server branches ----

    List<MergeTarget> MergeTargets(SgRoot root, CheckoutConfig co);

    List<MergeSource> MergeSources(SgRoot root, CheckoutConfig co, MergeTarget target);

    List<MergePair> MergePairs(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl);

    List<MergeRevision> MergeOffered(SgRoot root, CheckoutConfig co, IReadOnlyList<MergePair> pairs, int limit);

    List<string> MergeProblems(SgRoot root, CheckoutConfig co, MergeTarget target, string sourceUrl);

    /// <summary>One working copy's merge. picked null or empty takes everything the source has that this has not.</summary>
    MergeResult MergeRun(SgRoot root, CheckoutConfig co, MergePair pair, IReadOnlyList<LogRevision>? picked, bool dryRun, bool reverse);

    // ---- identity ----

    /// <summary>
    /// Which repository and which place in it, without how to reach it: SVN's repository id and path,
    /// or a git repository's first commit and the branch. Null when the working copy cannot say.
    /// </summary>
    (string Uuid, string Path)? Identity(SgRoot root, CheckoutConfig co);
}
