namespace Sg.Core;

public sealed class PushGroup
{
    /// <summary>Working copy, relative to the checkout. "" is the root.</summary>
    public string Wc = "";
    public string ReposRoot = "";
    /// <summary>pending, committed, failed, skipped</summary>
    public string State = "pending";
    public long? Revision;
    /// <summary>The commit a git checkout pushed. Empty for SVN.</summary>
    public string Commit = "";
    public string? Error;
    public List<string> Files = new();
    public List<DiffEntry> Entries = new();
    internal List<string> Targets = new();
    internal List<string> AddedDirs = new();

    /// <summary>What the server calls what this group made: r266, or the short commit. Empty before it made one.</summary>
    public string Label => new CommitId(Revision, Commit).Label;
}

/// <summary>
/// One pre-check, as the Push window shows it. Detail names the offending paths. Id says which check
/// this is, so a caller can offer the thing that clears it without matching on the display name.
/// </summary>
public sealed record PushCheck(string Id, string Name, bool Ok, string Detail)
{
    /// <summary>
    /// The paths this check is unhappy about. Detail is written for a reader and cuts the list off at
    /// three; a window that offers to clear the check needs all of them, exactly as they are.
    /// </summary>
    public List<string> Paths { get; init; } = new();
}

/// <summary>The checks push runs, by id. These are what a caller keys an offered fix off.</summary>
public static class PushChecks
{
    public const string Clean = "clean";
    public const string Rebasing = "rebasing";
    public const string Empty = "empty";
    public const string Types = "types";
    public const string Paths = "paths";
    public const string Collisions = "collisions";
    public const string Checkout = "checkout";
}

public sealed class PushPreview
{
    public string Branch = "";
    public string Checkout = "";
    public string Worktree = "";
    public string Base = "";

    /// <summary>The last commit this push would send. The branch tip for a whole push, an earlier one for a partial.</summary>
    public string Tip = "";

    /// <summary>Where the branch actually is. The same as Tip unless only part of the branch is going.</summary>
    public string BranchTip = "";

    public bool Dirty;
    public bool NeedsRebase;

    /// <summary>Every commit on the branch, newest first, whether it is going now or staying.</summary>
    public List<LogEntry> Commits = new();

    /// <summary>How many of them go now, counting up from the snapshot. The rest stay on the branch.</summary>
    public int Sending;

    /// <summary>Only part of the branch is going, so a commit will be left holding the rest.</summary>
    public bool Partial => Sending < Commits.Count;
    public List<PushGroup> Groups = new();
    public string DefaultMessage = "";
    public List<string> Problems = new();
    /// <summary>The checks push runs before it writes anything, answered up front.</summary>
    public List<PushCheck> Checks = new();
    public bool Ready => Checks.All(c => c.Ok);
    public IEnumerable<DiffEntry> Entries => Groups.SelectMany(g => g.Entries);
}

/// <summary>
/// One contiguous run of branch commits and the message it goes to SVN under. Through is the commit
/// that ends the run; the batches are applied oldest first, because SVN history is a line.
/// </summary>
public sealed record PushBatch(string Through, string Message);

/// <summary>What a push does once it has the change in the checkout.</summary>
public enum PushFinish
{
    /// <summary>Commit it, one SVN commit per working copy, and put the branch back on the new snapshot.</summary>
    Commit,

    /// <summary>
    /// Stop there. The change sits in the checkout as local changes, to read, edit, or commit by hand
    /// with the changes window or with TortoiseSVN. Nothing reaches the server and the branch does not
    /// move, so the same push can be made again once those changes are dealt with.
    /// </summary>
    LeaveInCheckout,
}

/// <summary>
/// How much of the branch a push sends. The whole of it, or the oldest N commits, leaving the rest on
/// the branch for the next push. It counts rather than naming a commit on purpose: a push rebases the
/// branch onto a fresh snapshot before it sends anything, and that gives every commit a new sha. A
/// count says the same thing before and after, as long as the rebase is clean, which a push demands.
/// </summary>
public readonly record struct PushScope(int? FirstCommits)
{
    /// <summary>Everything between the snapshot and the branch tip. What a push has always sent.</summary>
    public static PushScope Whole => new((int?)null);

    /// <summary>The oldest n commits of the branch, counting up from the snapshot.</summary>
    public static PushScope First(int n) => new(n);

    public bool Partial => FirstCommits.HasValue;
}

/// <summary>What one batch did: one SVN commit per working copy it touched.</summary>
public sealed class PushBatchResult
{
    public string Message = "";
    /// <summary>The commit this batch starts after, and the one it ends at.</summary>
    public string From = "";
    public string To = "";
    public List<PushGroup> Groups = new();
    /// <summary>False for a batch that was never tried, because an earlier one stopped the push.</summary>
    public bool Attempted;
    public bool AllCommitted => Attempted && Groups.All(g => g.State == "committed");
}

public sealed class PushResult
{
    public string Branch = "";
    public string Checkout = "";
    public string Message = "";
    public List<PushBatchResult> Batches = new();
    /// <summary>Every working copy commit of every batch, in the order they were made.</summary>
    public List<PushGroup> Groups => Batches.SelectMany(b => b.Groups).ToList();
    public bool AllCommitted;

    /// <summary>The change was written into the checkout and left there. Nothing reached the server.</summary>
    public bool AppliedOnly;

    public string BranchState = "";
    public long Revision;
    /// <summary>The server commit the new snapshot holds, for a git checkout. Empty for SVN.</summary>
    public string Commit = "";
    public string Label => Rev.Label(Revision, Commit);
    public List<string> Warnings = new();
}

/// <summary>
/// Push = sync, rebase, copy only the changed files into the checkout, one svn commit per working copy.
/// Not atomic across repositories. A failure stops the loop. What got in stays in. The rest stays on the branch.
/// </summary>
public static class Push
{
    public static PushResult Run(SgRoot root, string worktree, string? message, bool interactive, Func<string, string?>? editMessage = null,
        IReadOnlyDictionary<string, string>? messageFor = null, PushScope scope = default, PushFinish finish = PushFinish.Commit) =>
        Run(root, worktree, null, message, interactive, editMessage, messageFor, scope, finish);

    /// <summary>
    /// Sends the branch to SVN. With no batches that is one SVN commit per working copy for the whole
    /// branch, as it always was. With batches it is that once per batch, oldest first, each under its
    /// own message: six commits can go out as three and three. A batch that ends before the branch tip
    /// leaves the rest on the branch, which is how a partial push is asked for on purpose.
    /// messageFor gives a working copy a message of its own, keyed by its path relative to the
    /// checkout with "" for the root; every other working copy commits under the batch message.
    /// </summary>
    public static PushResult Run(SgRoot root, string worktree, IReadOnlyList<PushBatch>? batches, string? message,
        bool interactive, Func<string, string?>? editMessage = null, IReadOnlyDictionary<string, string>? messageFor = null,
        PushScope scope = default, PushFinish finish = PushFinish.Commit)
    {
        var git = root.Git;
        var log = root.Log;
        worktree = git.Toplevel(worktree);
        var branch = git.CurrentBranch(worktree);
        var co = Ops.BaseCheckout(root, branch);
        var vcs = root.Vcs(co);
        var server = vcs.ServerName;
        var snapRef = root.SnapshotRef(co);

        if (!interactive && !root.Config.AllowAgentPush)
            throw new SgException("sg push is for humans. Run it in an interactive terminal or from the GUI. Set allowAgentPush in .sg/sg.json to change that.");
        if (git.RebaseInProgress(worktree)) throw new SgException(Conflicts.Note(git, worktree) + ". " + Conflicts.Where);
        // Push sends the commits between the snapshot and the branch tip, and puts the branch back on the
        // new snapshot when it is done. Uncommitted work is not in those commits, so it would never reach
        // SVN, and the reset at the end would drop it. Say both, or the refusal reads as arbitrary.
        if (!git.IsClean(worktree))
            throw new SgException("worktree has uncommitted changes: " + worktree
                + $"\nPush sends commits, so these would not reach {server}, and the reset it ends with would drop them."
                + "\nCommit them on the branch first, or discard them, then push again.");
        // A message of a working copy's own is checked here, before anything is written: a refusal
        // after the sync and the rebase would come with the checkout already moved.
        var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (wc, text) in messageFor ?? new Dictionary<string, string>())
        {
            var clean = CleanMessage(text);
            if (clean.Length < root.Config.MinMessageLength)
                throw new SgException($"message for {(wc.Length == 0 ? "root" : wc)} too short: {clean.Length} chars, the minimum is {root.Config.MinMessageLength}");
            own[PathUtil.Rel(wc)] = clean;
        }
        using var _ = root.Lock();

        // 1. Fresh base, branch on top of it.
        Ops.Sync(root, co);
        var rb = Ops.Rebase(root, worktree, abortOnConflict: true);
        if (rb.Conflict)
            throw new SgException("the branch does not rebase cleanly on the new snapshot. Run 'sg rebase', then 'sg resolve auto --all' to have the resolver settle it"
                                  + " or 'sg resolve' to pick a version per file, then push again.\n" + rb.Output);
        var tip = git.HeadSha(worktree);
        var whole = git.DiffNameStatus(worktree, snapRef, tip);
        if (whole.Count == 0) throw new SgException("nothing to push: the branch equals " + snapRef);

        // 2. Refusals, over the whole push rather than per batch. A path this tool must not write is a
        // reason to send nothing at all, not a reason to stop after the second of five SVN commits.
        // "The whole push" is what is actually going: a commit further up a branch that is only partly
        // being sent has no say in whether the part below it can go.
        var lastSent = LastSent(git, worktree, snapRef, tip, batches, scope);
        var going = lastSent == tip ? whole : git.DiffNameStatus(worktree, snapRef, lastSent);
        if (going.Count == 0) throw new SgException("nothing to push: the commits picked change no files");
        var scan = vcs.Scan(root, co);
        var bad = Refusals(going, co, scan.LocalEdits);
        bad.AddRange(vcs.WriteBlockers(root, co));
        if (bad.Count > 0) throw new SgException("push refused:\n  " + string.Join("\n  ", bad));

        var wcs = WorkingCopies(scan.Externals);

        // 3. Or stop before the server: write the change into the checkout and leave it there. No
        // message is asked for, because nothing is being committed, and the branch does not move,
        // because nothing has left the machine.
        if (finish == PushFinish.LeaveInCheckout)
            return LeaveInCheckout(root, co, vcs, log, branch, lastSent, going, wcs);

        // 3. The plan: what goes out, in how many pieces, under which messages.
        var plan = ResolveBatches(root, git, worktree, snapRef, tip, batches, message, interactive, editMessage, scope);

        var result = new PushResult { Branch = branch, Checkout = co.Name, Message = plan[0].Message };

        // From here on the tool writes to SVN, and stopping half way through a four repository
        // commit leaves the checkout in a state nothing can reason about. Cancelling is honoured
        // up to this line, in sync and rebase, which are safe to run again. Past it, the sequence
        // finishes and reports, including the rollback of a group that fails.
        using var uncancellable = Cancellation.Use(CancellationToken.None);

        // 4. Each batch in turn: its own diff, its own groups, its own commits.
        var from = snapRef;
        string? stoppedAfter = null;
        for (var i = 0; i < plan.Count; i++)
        {
            var b = plan[i];
            var br = new PushBatchResult { Message = b.Message, From = from, To = b.Through };
            result.Batches.Add(br);
            if (stoppedAfter != null) continue;   // an earlier batch failed; this one was never tried

            var entries = git.DiffNameStatus(worktree, from, b.Through);
            if (entries.Count == 0)
            {
                // A run of commits that cancel out, or a merge of nothing. Say so and move on.
                br.Attempted = true;
                log.Info($"batch {i + 1}: no file changes, nothing sent");
                from = b.Through;
                continue;
            }

            br.Groups = GroupsFor(entries, wcs, co);
            vcs.FillRepositories(root, co, br.Groups);
            br.Attempted = true;
            if (plan.Count > 1) log.Info($"batch {i + 1} of {plan.Count}: {entries.Count} change(s) in {br.Groups.Count} working cop" + (br.Groups.Count == 1 ? "y" : "ies"));

            foreach (var g in br.Groups)
            {
                var label = g.Wc.Length == 0 ? "root" : g.Wc;
                log.Info($"committing {g.Entries.Count} change(s) in {label} ({g.ReposRoot})" + (own.ContainsKey(g.Wc) ? ", under its own message" : ""));
                try
                {
                    Operations.Receipt(root, server + " publication started", "", ["Branch: " + branch, "Working copy: " + g.Wc, "Outcome unknown until a revision receipt follows. Do not retry based solely on this record."], required: true);
                    vcs.WriteInto(root, co, g, b.Through);
                    var made = vcs.CommitWritten(root, co, g, own.TryGetValue(g.Wc, out var mine) ? mine : b.Message);
                    g.Revision = made.Revision;
                    g.Commit = made.Commit;
                    g.State = "committed";
                    Operations.Receipt(root, server + " revision published", "", ["Branch: " + branch, "Working copy: " + g.Wc, "Repository: " + g.ReposRoot, "Revision: " + g.Label]);
                    log.Info($"  {label}: {g.Label}");
                }
                catch (Exception ex) when (ex is SgException or IOException or UnauthorizedAccessException)
                {
                    g.State = "failed";
                    g.Error = ex.Message;
                    log.Warn($"  {label} failed: " + ex.Message);
                    // Put this working copy back to where this batch started, not to the snapshot:
                    // an earlier batch may already be in SVN and on disk.
                    vcs.Rollback(root, co, g, from, result.Warnings);
                    foreach (var rest in br.Groups.Where(x => x.State == "pending")) rest.State = "skipped";
                    stoppedAfter = from;
                    break;
                }
            }
            if (stoppedAfter == null) from = b.Through;
        }

        // 5. New snapshot, then put the branch where it belongs.
        var sync = Ops.Sync(root, co);
        result.Revision = sync.Revision;
        result.Commit = sync.Commit;

        // Everything from here on did not reach SVN: the batch that failed, or the tail of a push that
        // was asked to stop early. Both are the same shape, and both are rebuilt onto the new snapshot.
        var sent = stoppedAfter ?? plan[^1].Through;
        var sentAll = stoppedAfter == null && git.ResolveCommit(worktree, plan[^1].Through) == tip;
        if (sentAll)
        {
            git.ResetHard(worktree, snapRef);
            result.AllCommitted = true;
            result.BranchState = "reset to " + snapRef;
        }
        else if (stoppedAfter == null && ReplayRest(git, worktree, branch, snapRef, sync.Sha, sent, result))
        {
            // Said everything it needs to inside ReplayRest.
        }
        else
        {
            var restPaths = git.DiffNameStatus(worktree, sent, tip)
                .SelectMany(e => e.OldPath != null ? new[] { e.Path, e.OldPath } : new[] { e.Path })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var why = stoppedAfter != null
                ? "not pushed yet: " + string.Join(", ", result.Batches.SelectMany(x => x.Groups).Where(g => g.State != "committed").Select(g => g.Wc.Length == 0 ? "root" : g.Wc).Distinct())
                : "not pushed yet: the rest of the branch";
            var sha = PendingCommit(root, co, sync.Sha, tip, restPaths, why + "\n\n" + plan[^1].Message + "\n");
            git.ResetHard(worktree, sha);
            result.BranchState = "pending commit " + sha[..10] + " holds what did not go";
        }
        Operations.Receipt(root, "Push to " + server, worktree, result.Batches.SelectMany(x => x.Groups).Select(g => g.Wc + ": " + g.State + (g.Label.Length == 0 ? "" : " " + g.Label)).Append(result.BranchState));
        return result;
    }

    /// <summary>
    /// Writes the change into the checkout and stops. What lands is the same bytes a push would have
    /// committed, sitting as local changes: read them in the changes window, edit them, commit them
    /// there or with TortoiseSVN, or revert them and it is as if this never ran.
    ///
    /// The branch is left exactly where it was. Until those changes are committed or reverted, a real
    /// push refuses to touch the same files, which is the rule that stops the two ways of sending the
    /// same work from writing over each other.
    /// </summary>
    static PushResult LeaveInCheckout(SgRoot root, CheckoutConfig co, ICheckoutVcs vcs, ILog log,
        string branch, string tip, List<DiffEntry> entries, List<string> wcs)
    {
        var result = new PushResult { Branch = branch, Checkout = co.Name, AppliedOnly = true };
        var groups = GroupsFor(entries, wcs, co);
        vcs.FillRepositories(root, co, groups);
        result.Batches.Add(new PushBatchResult { From = "", To = tip, Groups = groups, Attempted = true });

        // Past here files are being written. Cancelling half way would leave the checkout in a state
        // nothing can reason about, the same as it would for a commit.
        using var uncancellable = Cancellation.Use(CancellationToken.None);
        var done = new List<PushGroup>();
        foreach (var g in groups)
        {
            var label = g.Wc.Length == 0 ? "root" : g.Wc;
            log.Info($"writing {g.Entries.Count} change(s) into {label}, without committing");
            try
            {
                vcs.WriteInto(root, co, g, tip);
                g.State = "applied";
                done.Add(g);
            }
            catch (Exception ex) when (ex is SgException or IOException or UnauthorizedAccessException)
            {
                g.State = "failed";
                g.Error = ex.Message;
                log.Warn($"  {label} failed: " + ex.Message);
                // Nothing was committed, so everything already written goes back: a half applied
                // checkout is worse than one that was never touched.
                foreach (var back in done) vcs.Rollback(root, co, back, tip, result.Warnings);
                vcs.Rollback(root, co, g, tip, result.Warnings);
                foreach (var rest in groups.Where(x => x.State == "pending")) rest.State = "skipped";
                result.BranchState = "nothing was applied";
                return result;
            }
        }

        var files = groups.Sum(g => g.Entries.Count);
        result.BranchState = $"{branch} is unchanged; {files} file(s) are waiting in the checkout";
        log.Info("nothing was committed. The changes are in " + co.Path + ", to read and commit there.");
        return result;
    }

    /// <summary>
    /// A push that stopped where it was asked to stop leaves proper commits behind, so they stay proper
    /// commits: replayed one for one onto the new snapshot. That is what makes "send the first two" a
    /// thing you can do again tomorrow for the next two, instead of a thing that flattens the rest into
    /// one blob. Their changes are already in the snapshot up to what went, so the replay is normally
    /// empty of conflict; if it is not, it is undone and the caller falls back to the one pending commit.
    /// </summary>
    static bool ReplayRest(Git git, string worktree, string branch, string snapRef, string newSnap, string sent, PushResult result)
    {
        var replay = git.RebaseOnto(worktree, newSnap, sent, branch);
        if (!replay.Ok)
        {
            git.RebaseAbort(worktree);
            result.Warnings.Add("the commits that were not pushed do not replay onto the new snapshot cleanly, "
                                + "so they were kept as one commit instead: " + (replay.StdErr.Trim() + " " + replay.StdOut.Trim()).Trim());
            return false;
        }
        var left = git.CountCommits(snapRef, "refs/heads/" + branch);
        result.BranchState = left == 1 ? "1 commit still on the branch" : $"{left} commits still on the branch";
        return true;
    }

    /// <summary>
    /// The last commit this push will send, worked out before any message is asked for so the refusals
    /// can be scoped to it. Counted from the snapshot up, on the branch as it stands right now.
    /// </summary>
    static string LastSent(Git git, string worktree, string snapRef, string tip, IReadOnlyList<PushBatch>? batches, PushScope scope)
    {
        if (batches is { Count: > 0 })
            return git.ResolveCommit(worktree, batches[^1].Through)
                   ?? throw new SgException("no such commit: " + batches[^1].Through);
        var own = git.RevList(worktree, snapRef + ".." + tip);
        var sending = Sending(scope, own.Count);
        return sending >= own.Count ? tip : own[own.Count - sending];
    }

    /// <summary>
    /// How many commits a scope asks for, clamped to what the branch has. Asking for more than there is
    /// means the whole branch, which is the same answer as asking for all of it.
    /// </summary>
    static int Sending(PushScope scope, int onBranch)
    {
        if (!scope.Partial) return onBranch;
        var n = scope.FirstCommits!.Value;
        if (n < 1) throw new SgException("a push of the first commits needs at least one of them");
        return Math.Min(n, onBranch);
    }

    /// <summary>
    /// Turns what the caller asked for into a list this can run: real commits, in order, each with a
    /// message the server will take. One batch through the branch tip is the plain push, and a scope
    /// narrows that one batch to the oldest few commits.
    /// </summary>
    static List<PushBatch> ResolveBatches(SgRoot root, Git git, string worktree, string snapRef, string tip,
        IReadOnlyList<PushBatch>? batches, string? message, bool interactive, Func<string, string?>? editMessage,
        PushScope scope)
    {
        if (batches == null || batches.Count == 0)
        {
            var through = LastSent(git, worktree, snapRef, tip, null, scope);
            var msg = CleanMessage(message ?? git.LogBodies(snapRef, through));
            if (message == null && interactive && editMessage != null)
                msg = CleanMessage(editMessage(msg) ?? throw new SgException("push aborted: empty message"));
            CheckMessage(root, msg, 1, 1);
            return [new PushBatch(through, msg)];
        }

        var plan = new List<PushBatch>();
        var previous = snapRef;
        for (var i = 0; i < batches.Count; i++)
        {
            var through = git.ResolveCommit(worktree, batches[i].Through)
                          ?? throw new SgException($"batch {i + 1}: no such commit: {batches[i].Through}");
            if (!git.IsAncestor(through, tip))
                throw new SgException($"batch {i + 1}: {batches[i].Through} is not on this branch");
            if (!git.IsAncestor(previous, through) || git.ResolveCommit(worktree, previous) == through)
                throw new SgException($"batch {i + 1}: {batches[i].Through} does not come after the batch before it");
            var msg = CleanMessage(batches[i].Message);
            CheckMessage(root, msg, i + 1, batches.Count);
            plan.Add(new PushBatch(through, msg));
            previous = through;
        }
        return plan;
    }

    static void CheckMessage(SgRoot root, string msg, int index, int count)
    {
        if (msg.Length >= root.Config.MinMessageLength) return;
        var which = count > 1 ? $"batch {index}: " : "";
        throw new SgException($"{which}message too short: {msg.Length} chars, the minimum is {root.Config.MinMessageLength}");
    }

    /// <summary>One group per working copy. A rename across working copies becomes delete plus add.</summary>
    static List<PushGroup> GroupsFor(List<DiffEntry> entries, List<string> wcs, CheckoutConfig co)
    {
        string WcOf(string p) => InnermostWc(wcs, p);
        var work = new List<DiffEntry>();
        foreach (var e in entries)
        {
            if (e.Status is 'R' or 'C' && e.OldPath != null && !WcOf(e.OldPath).Equals(WcOf(e.Path), StringComparison.OrdinalIgnoreCase))
            {
                if (e.Status == 'R') work.Add(new DiffEntry('D', e.OldPath, null));
                work.Add(new DiffEntry('A', e.Path, null));
            }
            else if (e.Status == 'C') work.Add(new DiffEntry('A', e.Path, null));
            else work.Add(e);
        }
        return work.GroupBy(e => WcOf(e.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => new PushGroup { Wc = g.Key, Entries = g.ToList(), Files = g.Select(x => x.Path).ToList() })
            .OrderBy(g => g.Wc.Length == 0 ? 0 : 1)
            .ThenBy(g =>
            {
                var i = co.PushOrder.FindIndex(x => PathUtil.Rel(x).Equals(g.Wc, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? int.MaxValue : i;
            })
            .ThenBy(g => g.Wc, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A commit on top of the new snapshot that carries the paths of the failed groups as they were on the old tip.</summary>
    static string PendingCommit(SgRoot root, CheckoutConfig co, string newSnap, string tip, List<string> paths, string message)
    {
        var git = root.Git;
        var idx = root.NewTempFile(".index");
        try
        {
            git.ReadTree(co.Path, newSnap, idx);
            var existing = git.LsTree(tip, paths).Where(e => e.Type == "blob")
                .ToDictionary(e => e.Path, e => e, StringComparer.OrdinalIgnoreCase);
            var infos = paths.Select(p => existing.TryGetValue(p, out var e) ? (e.Mode, e.Sha, p) : ("0", new string('0', 40), p));
            git.UpdateIndexInfo(co.Path, infos, idx);
            var tree = git.WriteTree(co.Path, idx);
            return git.CommitTree(tree, newSnap, message);
        }
        finally
        {
            if (File.Exists(idx)) File.Delete(idx);
        }
    }

    /// <summary>What a push would send, from the current snapshot, without syncing. For the push dialog.</summary>
    /// <summary>
    /// What a push would send, without sending it. scope narrows it to the oldest few commits; the list
    /// of commits is the whole branch either way, so a window can show what stays as well as what goes.
    /// </summary>
    public static PushPreview Preview(SgRoot root, string worktree, PushScope scope = default)
    {
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var branch = git.CurrentBranch(worktree);
        var co = Ops.BaseCheckout(root, branch);
        var snapRef = root.SnapshotRef(co);
        var snap = git.RefSha(snapRef) ?? throw new SgException("no snapshot for " + co.Name);
        var branchTip = git.HeadSha(worktree);
        var commits = git.Log(worktree, snap + ".." + branchTip, 200);
        var sending = Sending(scope, commits.Count);
        // The oldest `sending` commits, so the boundary is the one that many up from the snapshot.
        var tip = sending >= commits.Count ? branchTip : commits[commits.Count - sending].Sha;
        var p = new PushPreview
        {
            Branch = branch, Checkout = co.Name, Worktree = worktree, Base = snap, Tip = tip, BranchTip = branchTip,
            Dirty = !git.IsClean(worktree),
            NeedsRebase = !git.IsAncestor(snap, branchTip),
            Commits = commits,
            Sending = sending,
            DefaultMessage = CleanMessage(git.LogBodies(snap, tip)),
        };
        var meta = SnapshotMeta.Parse(git.Body(snap));
        var wcs = WorkingCopies(meta.Externals.Keys);
        string WcOf(string path) => InnermostWc(wcs, path);
        var entries = git.DiffNameStatus(worktree, snap, tip);
        p.Groups = entries.GroupBy(e => WcOf(e.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => new PushGroup { Wc = g.Key, Entries = g.ToList(), Files = g.Select(x => x.Path).ToList() })
            .OrderBy(g => g.Wc.Length == 0 ? 0 : 1).ThenBy(g => g.Wc, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var g in p.Groups)
            foreach (var e in g.Entries)
                if (co.Skip.Any(s => PathUtil.IsUnder(e.Path, s))) p.Problems.Add(e.Path + " is a skipped path");

        var rebasing = git.RebaseInProgress(worktree);
        var paths = entries.SelectMany(e => e.OldPath != null ? new[] { e.Path, e.OldPath } : new[] { e.Path }).Distinct().ToList();
        var vcs = root.Vcs(co);
        var localEdits = vcs.LocalEditsOn(root, co, paths);
        var unsupported = entries.Where(e => e.Status is not ('A' or 'M' or 'D' or 'R' or 'T' or 'C'))
            .Select(e => $"{e.Path} ({e.Status})").ToList();
        var unwritable = paths.Where(x => co.Skip.Any(sk => PathUtil.IsUnder(x, sk)) || PathUtil.HasReservedName(x)).ToList();
        var collisions = paths.Where(localEdits.Contains).ToList();

        p.Checks =
        [
            new PushCheck(PushChecks.Clean, "Worktree is clean", !p.Dirty,
                p.Dirty ? "commit or discard what is uncommitted in " + worktree : "nothing uncommitted"),
            new PushCheck(PushChecks.Rebasing, "No rebase in progress", !rebasing,
                rebasing ? "finish or abort the rebase first" : "none"),
            new PushCheck(PushChecks.Empty, "Something to push", entries.Count > 0,
                entries.Count > 0
                    ? $"{entries.Count} changed path(s) across {p.Groups.Count} working cop" + (p.Groups.Count == 1 ? "y" : "ies")
                    : "the branch is the same as svn/" + co.Name),
            Check(PushChecks.Types, $"Change types {vcs.ServerName} takes", unsupported, "add, modify, delete and rename only"),
            Check(PushChecks.Paths, "Paths sg may write", unwritable, "none skipped, no reserved names"),
            Check(PushChecks.Collisions, "No local edit in the checkout on the same file", collisions, "no collisions"),
        ];
        // A checkout that cannot take a commit at all says why. SVN always can, so it shows no row for it.
        var blockers = vcs.WriteBlockers(root, co);
        if (blockers.Count > 0)
            p.Checks.Add(new PushCheck(PushChecks.Checkout, "Checkout can take a commit", false, string.Join("; ", blockers)));
        return p;
    }

    /// <summary>
    /// The working copies a path can belong to, longest first, with the root last. Sorted once here
    /// rather than once per path: a push groups thousands of changed paths against every external.
    /// </summary>
    static List<string> WorkingCopies(IEnumerable<string> externals) =>
        externals.OrderByDescending(w => w.Length).Append("").ToList();

    /// <summary>The innermost working copy the path sits in. "" is the root, and it always matches.</summary>
    static string InnermostWc(List<string> wcs, string path)
    {
        foreach (var w in wcs)
            if (PathUtil.IsUnder(path, w)) return w;
        return "";
    }

    /// <summary>Every reason this set of changes cannot go to SVN. Empty means it can.</summary>
    static List<string> Refusals(IEnumerable<DiffEntry> entries, CheckoutConfig co, HashSet<string> localEdits)
    {
        var bad = new List<string>();
        foreach (var e in entries)
        {
            if (e.Status is not ('A' or 'M' or 'D' or 'R' or 'T' or 'C'))
                bad.Add(e.Path + " (unsupported change type " + e.Status + ")");
            foreach (var path in new[] { e.Path, e.OldPath })
            {
                if (path == null) continue;
                if (co.Skip.Any(sk => PathUtil.IsUnder(path, sk))) bad.Add(path + " (skipped path, sg never writes it to SVN)");
                if (PathUtil.HasReservedName(path)) bad.Add(path + " (reserved Windows name)");
                if (localEdits.Contains(path)) bad.Add(path + " (has a local edit in the checkout, commit or revert it there first)");
            }
        }
        return bad.Distinct().ToList();
    }

    static PushCheck Check(string id, string name, IReadOnlyList<string> offenders, string clear)
    {
        if (offenders.Count == 0) return new PushCheck(id, name, true, clear);
        var shown = string.Join(", ", offenders.Take(3));
        return new PushCheck(id, name, false, offenders.Count > 3 ? $"{shown}, and {offenders.Count - 3} more" : shown)
        {
            Paths = offenders.ToList(),
        };
    }

    public static string CleanMessage(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith('#')).Select(l => l.TrimEnd());
        var sb = new System.Text.StringBuilder();
        var blank = 0;
        foreach (var l in lines)
        {
            if (l.Length == 0) { blank++; if (blank > 1) continue; }
            else blank = 0;
            sb.Append(l).Append('\n');
        }
        return sb.ToString().Trim();
    }
}
