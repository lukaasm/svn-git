namespace Sg.Core;

/// <summary>
/// A worktree with work stopped half way in it: which replay stopped, how far it got, and what is
/// still in conflict. Nothing in here is particular to a rebase or to an import; that is the point.
/// </summary>
public sealed class ConflictState
{
    public string Worktree = "";
    public string Branch = "";
    public string Checkout = "";
    /// <summary>What the checkout's server is called: SVN, or git. It names the base a rebase replays over.</summary>
    public string Server = "SVN";
    public string? BackupName;
    /// <summary>A replay from the backup into work that was already here, not a restore of it.</summary>
    public bool BackupPull;
    /// <summary>git's two-letter status of each file in conflict, like UU or UD. <see cref="Conflicts.Describe"/> words it.</summary>
    public Dictionary<string, string> Codes = new(StringComparer.Ordinal);

    /// <summary>What the commit being finished holds so far: its staged files, resolutions included.</summary>
    public List<string> ResolutionReviewFiles = new();

    /// <summary>What stopped. None means the worktree is in a normal state and there is nothing to finish.</summary>
    public Replay Kind;

    /// <summary>The files git left at three stages, waiting for a version to be picked.</summary>
    public List<string> Conflicted = new();

    /// <summary>Where a series got to: commit At of Of. Both zero when git did not count.</summary>
    public int At;
    public int Of;

    /// <summary>The subject of the commit or patch it stopped on. Empty when git does not say.</summary>
    public string Stopped = "";

    /// <summary>
    /// It stopped, and there is nothing here to resolve: no file held at three stages, and nothing
    /// staged either. That is not "every file is resolved" - it is a patch git would not take at all,
    /// or a commit that changes nothing here any more. Continuing has nothing to commit and only says
    /// so in words about "git add" that read like the resolution was forgotten, so the two are told
    /// apart here instead, and each is offered the way out that works for it.
    /// </summary>
    public bool Stuck;

    /// <summary>
    /// While it is stuck, what the worktree holds that a hand could still finish: the files a forced
    /// apply changed, and the .rej files it wrote beside them. Empty until one has been forced.
    /// </summary>
    public List<string> ByHand = new();

    public bool Finalizing => Kind == Replay.None && BackupName != null;
    public bool InProgress => Kind != Replay.None || Finalizing;

    /// <summary>The word for what stopped, for a sentence that has to name it.</summary>
    public string Verb => BackupName != null ? "backup replay" : Conflicts.Verb(Kind);

    /// <summary>What the left side of a conflict is: the version already here, whatever put it there.</summary>
    public string OursLabel => Kind == Replay.Import ? "current branch" : $"updated {Server} base";

    /// <summary>What the right side is: the version the stopped commit or patch wants.</summary>
    public string TheirsLabel => BackupName != null ? "incoming backup" : Kind == Replay.Import ? "imported commit" : "branch commit";
}

/// <summary>What forcing a stuck patch into the worktree did, file by file.</summary>
public sealed class ForcedApply
{
    /// <summary>Files the whole patch still fitted. They are changed on disk and not staged.</summary>
    public List<string> Applied = new();

    /// <summary>Files where some of it did not fit. Each has a ".rej" beside it holding those hunks.</summary>
    public List<string> Rejected = new();

    public string Output = "";
}

/// <summary>One step of a stopped replay: it finished, or it moved on and stopped again.</summary>
public sealed class ResolveResult
{
    public RestoreResult? Backup;
    public OperationRecord? Operation;
    public Replay Kind;
    public string Branch = "";
    public string Checkout = "";

    /// <summary>The whole replay is through. Nothing is half done in the worktree any more.</summary>
    public bool Ok;

    /// <summary>It moved on and stopped again. The page comes back with the next set of files.</summary>
    public bool Conflict;

    /// <summary>Commits the branch has that its snapshot does not, once the replay is through.</summary>
    public int Ahead;

    public string Output = "";

    /// <summary>Shared folders mirrored from the checkout after a rebase finished. Empty otherwise.</summary>
    public List<string> Refreshed = new();

    /// <summary>The commit or patch it stopped on this time, when it stopped again.</summary>
    public string Stopped = "";

    /// <summary>It stopped again on one that will not go in at all. ConflictState.Stuck says the same.</summary>
    public bool Stuck;

    public string Verb => Conflicts.Verb(Kind);
}

/// <summary>
/// One way to finish work that stopped, whatever stopped it. A rebase and an import both leave the
/// same thing behind - files at three stages and a commit waiting to be made - and both are moved on
/// by the same three verbs, so they are one thing here with a git command each.
///
/// Nothing here decides anything: it carries on, drops one, or puts it all back. Picking a version
/// per file is Git.TakeSide and Git.MarkResolved, which never cared which replay asked.
/// </summary>
public static class Conflicts
{
    /// <summary>Includes pending backup finalization when Git has already finished its queue.</summary>
    public static bool HasPending(Git git, string path) => git.ReplayInProgress(path) != Replay.None || Backup.ReplayName(git, path) != null;

    /// <summary>A file's two-letter git status, like UU for both modified. Empty when it is not in conflict.</summary>
    public static string StatusCode(Git git, string worktree, string path) =>
        git.StatusEntries(worktree, untracked: false).FirstOrDefault(e => e.Path == path) is { } e ? e.X + e.Y : "";

    /// <summary>
    /// How a file came to be in conflict, in the words git status itself uses, which are also what VS
    /// Code and the other git tools show. "Us" is the version here now, "them" the one coming in.
    /// </summary>
    public static string Describe(string code) => code switch
    {
        "UU" => "both modified",
        "AA" => "both added",
        "DD" => "both deleted",
        "UD" => "deleted by them",
        "DU" => "deleted by us",
        "AU" => "added by us",
        "UA" => "added by them",
        _ => "conflict",
    };

    public static string Verb(Replay kind) => kind switch
    {
        Replay.Rebase => "rebase",
        Replay.Import => "import",
        _ => "",
    };

    /// <summary>
    /// How a refusal names what is already stopped here. Every operation that will not run over half
    /// done work says this and then its own reason, so none of them has to guess it was a rebase.
    /// </summary>
    public static string Note(Git git, string worktree) => git.ReplayInProgress(worktree) switch
    {
        Replay.Import => "an import stopped part way in " + worktree,
        Replay.Rebase => "a rebase stopped part way in " + worktree,
        _ => Backup.ReplayName(git, worktree) != null ? "backup edits await recovery in " + worktree : "nothing is stopped in " + worktree,
    };

    /// <summary>The sentence that follows it: where the two buttons that finish it are.</summary>
    public const string Where = "Finish it or put it back first: 'sg resolve', or the Resolve conflicts page.";

    /// <summary>What is stopped in this worktree, and what is left in conflict. Kind is None when nothing is.</summary>
    public static ConflictState State(SgRoot root, string worktree)
    {
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var branch = git.BranchOrRebaseHead(worktree);
        var co = Ops.BaseCheckout(root, branch);
        var at = git.Progress(worktree);
        var state = new ConflictState
        {
            Worktree = worktree,
            Branch = branch,
            Checkout = co.Name,
            Server = root.Vcs(co).ServerName,
            Kind = git.ReplayInProgress(worktree),
            BackupName = Backup.ReplayName(git, worktree),
            BackupPull = Backup.ReplayIsPull(git, worktree),
            Conflicted = git.ConflictedFiles(worktree),
            At = at.At,
            Of = at.Of,
            Stopped = at.Subject,
        };
        foreach (var entry in git.StatusEntries(worktree, untracked: true).Where(x => state.Conflicted.Contains(x.Path)))
            state.Codes[entry.Path] = entry.X + entry.Y;
        if (state.InProgress)
            state.ResolutionReviewFiles = git.Out(worktree, "diff", "--cached", "--name-only").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        state.Stuck = state.Kind != Replay.None && state.Conflicted.Count == 0 && git.NothingStaged(worktree);
        // Only then, and only because a stuck step is the one place the worktree's own changes are the
        // thing to look at: everywhere else they are noise, and a replay leaves the worktree clean.
        if (state.Stuck)
            state.ByHand = git.StatusEntries(worktree, untracked: true).Select(e => e.Path).ToList();
        return state;
    }

    /// <summary>
    /// Goes on with whatever stopped here. Every file has to have a version picked first, because git
    /// commits what is staged, and a file still held at three stages is not staged at all.
    /// </summary>
    public static ResolveResult Continue(SgRoot root, string worktree)
    {
        using var operation = root.Lock();
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        if (git.ReplayInProgress(worktree) == Replay.None && Backup.ReplayName(git, worktree) != null)
            return Step(root, worktree, Replay.Import, new ProcResult { Exe = "sg", Args = ["recover-edits"], ExitCode = 0 });
        var kind = Started(git, worktree);
        var left = git.ConflictedFiles(worktree);
        if (left.Count > 0)
            throw new SgException($"{left.Count} file(s) are still in conflict. Pick a version for each, or mark it resolved:\n  "
                                  + string.Join("\n  ", left.Take(10)));
        if (git.NothingStaged(worktree))
            throw new SgException(StuckNote(kind) + " Nothing is staged, so there is nothing for this step to commit.\n"
                                  + (kind == Replay.Import
                                      ? "Skip it, or force what fits of it into the worktree, finish it by hand and mark it resolved."
                                      : "Review the original commit, then skip it if this resolution is intended."));
        return Step(root, worktree, kind, kind == Replay.Import ? git.ContinueMailbox(worktree) : git.RebaseContinue(worktree));
    }

    /// <summary>The one sentence that says a step is stuck rather than resolved. The pages share it.</summary>
    public static string StuckNote(Replay kind) => kind == Replay.Import
        ? "This patch has no staged changes. It may not have applied, or its resolution may produce no changes."
        : "This commit produces no changes after resolution. Review it before skipping.";

    /// <summary>
    /// Puts as much of the stopped patch into the worktree as still fits, and writes every hunk that
    /// does not into a ".rej" file beside its own file. The way on from a patch git will not take: what
    /// landed is there to read, what did not is there in full to put in by hand.
    ///
    /// It stages nothing. Half a patch is not a thing to commit without looking at it, and staging it
    /// would make the page say every file was resolved when only some of them were.
    /// </summary>
    public static ForcedApply ApplyWhatFits(SgRoot root, string worktree)
    {
        using var operation = root.Lock();
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        if (Started(git, worktree) != Replay.Import)
            throw new SgException("this is a rebase, and a rebase keeps a commit rather than a patch file. "
                                  + "A rebase that stops with nothing to resolve stopped on a commit that changes nothing here: skip it.");
        if (git.ConflictedFiles(worktree).Count > 0)
            throw new SgException("some files are in conflict, so git did take this patch. Pick a version for each instead.");

        var patch = git.StoppedPatchFile(worktree)
                    ?? throw new SgException("git kept no patch file for the one it stopped on, so there is nothing to force.");
        var r = git.ApplyReject(worktree, patch);
        var res = new ForcedApply { Output = (r.StdOut.Trim() + "\n" + r.StdErr.Trim()).Trim() };
        foreach (var raw in res.Output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Applied patch ", StringComparison.Ordinal) && line.EndsWith(" cleanly.", StringComparison.Ordinal))
                res.Applied.Add(PathUtil.Rel(line[14..^9].Trim()));
            else if (line.StartsWith("Applying patch ", StringComparison.Ordinal) && line.Contains(" with ", StringComparison.Ordinal))
                res.Rejected.Add(PathUtil.Rel(line[15..line.IndexOf(" with ", StringComparison.Ordinal)].Trim()));
        }
        if (res.Applied.Count == 0 && res.Rejected.Count == 0)
            throw new SgException("none of this patch fits the files here, so nothing was written:\n" + res.Output);
        return res;
    }

    /// <summary>
    /// Drops the commit or patch that stopped and goes on with the ones after it. The way out when what
    /// it carries is here already, or is not wanted: that one is gone, and everything after it still lands.
    /// </summary>
    public static ResolveResult Skip(SgRoot root, string worktree)
    {
        using var operation = root.Lock();
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var kind = Started(git, worktree);
        return Step(root, worktree, kind, kind == Replay.Import ? git.SkipMailbox(worktree) : git.RebaseSkip(worktree));
    }

    /// <summary>
    /// Puts the branch back to where the replay found it. A rebase gives back the branch as it was. An
    /// import gives back the branch with none of the export on it, the commits that already went in
    /// included, because git undoes a series as one thing - so the caller says that out loud first.
    /// </summary>
    public static void Abort(SgRoot root, string worktree)
    {
        using var operation = root.Lock();
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        if (Started(git, worktree) == Replay.Import) git.AbortMailbox(worktree);
        else git.RebaseAbort(worktree);
        if (git.ReplayInProgress(worktree) == Replay.None) Backup.ClearReplay(git, worktree);
    }

    /// <summary>
    /// Hands the files this stop left in conflict to the resolver, once. What it settles is staged;
    /// what it could not is still in conflict, named with the reason, and the step waits for a hand.
    /// Nothing is continued: the answer is there to read before it goes into the branch.
    /// </summary>
    public static AutoResolveResult AutoResolve(SgRoot root, string worktree, IEnumerable<string>? only = null)
    {
        using var operation = root.Lock();
        var state = State(root, worktree);
        if (!state.InProgress) throw new SgException("nothing is stopped in " + worktree + ", so there is nothing to resolve.");
        if (state.Conflicted.Count == 0)
            throw new SgException(state.Stuck
                ? StuckNote(state.Kind) + " There is nothing for a resolver to settle either. Skip it."
                : "nothing is in conflict any more. Carry on with: sg resolve continue");
        var pick = only?.Select(PathUtil.Rel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pick is { Count: > 0 })
        {
            state.Conflicted = state.Conflicted.Where(pick.Contains).ToList();
            if (state.Conflicted.Count == 0) throw new SgException("none of the named files is in conflict.");
        }
        return Resolver.Resolve(root, state);
    }

    /// <summary>
    /// The same, and then on: continue, and when the next commit stops, ask again, until the replay is
    /// through or one stop holds a file the resolver could not settle. A rebase step that is left with
    /// nothing to commit is dropped the way Skip drops it, because that is the only way on from one and
    /// what it carried is in the branch already. An import stops on such a step instead: forcing what
    /// fits of a patch is a choice for a hand.
    /// </summary>
    public static AutoResolveRun AutoResolveAll(SgRoot root, string worktree)
    {
        using var operation = root.Lock();
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var kind = Started(git, worktree);
        var first = State(root, worktree);
        var run = new AutoResolveRun { Kind = kind, Branch = first.Branch, Checkout = first.Checkout };
        // One pass per commit of the series and then some; a replay cannot stop more often than that.
        var most = Math.Max(first.Of, 1) * 2 + 10;
        for (var pass = 0; pass < most; pass++)
        {
            Cancellation.ThrowIfRequested();
            var state = State(root, worktree);
            if (!state.InProgress) { run.Ok = true; return run; }
            if (state.Conflicted.Count > 0)
            {
                root.Log.Info($"the {state.Verb} stopped" + (state.Of > 0 ? $" at {state.At} of {state.Of}" : "")
                              + (state.Stopped.Length > 0 ? $", on \"{state.Stopped}\"" : "") + $": {state.Conflicted.Count} file(s) in conflict");
                var step = Resolver.Resolve(root, state);
                run.Steps.Add(step);
                if (!step.AllResolved)
                {
                    run.Why = $"{step.Left.Count} file(s) the resolver could not settle: "
                              + string.Join(", ", step.Left.Select(l => l.Path + " (" + l.Why + ")"));
                    return run;
                }
                root.Log.Info($"settled {step.Resolved.Count} file(s), carrying on");
            }
            ResolveResult r;
            if (git.NothingStaged(worktree))
            {
                if (kind == Replay.Import)
                {
                    run.Why = StuckNote(kind) + " Skip it, or force what fits, by hand.";
                    return run;
                }
                root.Log.Info("this commit changes nothing here any more, skipping it");
                run.Skipped++;
                r = Skip(root, worktree);
            }
            else r = Continue(root, worktree);
            if (r.Ok)
            {
                run.Ok = true;
                run.Finished = r;
                return run;
            }
        }
        run.Why = "it stopped more often than the series is long, which should not happen; the rest is left as it is.";
        return run;
    }

    static Replay Started(Git git, string worktree)
    {
        var kind = git.ReplayInProgress(worktree);
        if (kind == Replay.None)
            throw new SgException("nothing is stopped in " + worktree + ", so there is nothing to carry on or put back.");
        return kind;
    }

    /// <summary>
    /// What one step did. Still in progress means it moved on and stopped again, which is an answer and
    /// not a failure: a series of twenty commits can want a hand three times before it is through.
    /// </summary>
    static ResolveResult Step(SgRoot root, string worktree, Replay kind, ProcResult r)
    {
        var git = root.Git;
        var branch = git.BranchOrRebaseHead(worktree);
        var co = Ops.BaseCheckout(root, branch);
        var res = new ResolveResult
        {
            Kind = kind,
            Branch = branch,
            Checkout = co.Name,
            Output = (r.StdOut.Trim() + "\n" + r.StdErr.Trim()).Trim(),
        };

        if (git.ReplayInProgress(worktree) != Replay.None)
        {
            res.Conflict = true;
            res.Stopped = git.Progress(worktree).Subject;
            // Stopped with nothing to pick a side for is not "resolved and waiting": it is a step that
            // will not go in at all, and continuing can only fail. Saying which it is here is what
            // keeps a page from offering Continue over and over with nothing changing.
            res.Stuck = git.ConflictedFiles(worktree).Count == 0 && git.NothingStaged(worktree);
            if (res.Stuck) res.Output = StuckNote(kind) + "\n" + res.Output;
            return res;
        }

        r.EnsureOk();
        res.Backup = Backup.FinishReplay(root, worktree);
        res.Operation = Operations.AfterReplay(root, worktree);
        res.Ok = true;
        res.Ahead = git.CountCommits(root.SnapshotRef(co), "refs/heads/" + branch);
        // Only a rebase moves the branch onto a newer snapshot, so only a rebase leaves the shared
        // folders behind. An import lands on the snapshot its worktree was already built against.
        if (kind == Replay.Rebase && res.Operation?.Kind != "Update from SVN") res.Refreshed = Ops.RefreshShared(root, co, worktree, branch);
        return res;
    }
}
