namespace Sg.Core;

public sealed record ReviewInboxItem(string Id, string File, string Side, int First, int Last,
    string State, bool Conflict, string Comment, string Author, string LatestBody, string LatestActor,
    string LatestAction, DateTimeOffset Updated, int Updates);
public sealed record ReviewInboxWorktree(string Path, string Branch, string Checkout,
    IReadOnlyList<ReviewInboxItem> Items, string Revision, string? Error = null, string? Identity = null);
public sealed record ReviewInboxRow(string Worktree, string Branch, string Checkout, ReviewInboxItem Thread);
public sealed record ReviewInboxError(string Worktree, string Branch, string Message);
public sealed record ReviewInboxResult(int Schema, int Total, ReviewInboxRow[] Threads, int? NextOffset, ReviewInboxError[] Errors);

/// <summary>Read-only inventory of feedback in this root's registered branch worktrees. Reads only
/// review metadata; it does not create identities, read source files, or fetch remote repositories.</summary>
public sealed class ReviewInbox : IDisposable
{
    readonly SgRoot _root;
    readonly FileObservation _observation;
    public ReviewInbox(SgRoot root)
    {
        _root = root;
        _observation = new(Path.Combine(root.StorePath, "code-reviews", "*.json"), root.StorePath);
    }
    public event Action? Changed { add => _observation.Changed += value; remove => _observation.Changed -= value; }
    public string? WatchError => _observation.Error;

    /// <summary>Progress publishes complete per-worktree summaries. Errors are local to a worktree;
    /// a damaged document must not hide feedback from the remaining worktrees.</summary>
    public IReadOnlyList<ReviewInboxWorktree> Read(Action<ReviewInboxWorktree>? progress = null)
    {
        _observation.Reconnect();
        var bases = _root.Git.BranchBases();
        var result = new List<ReviewInboxWorktree>();
        foreach (var worktree in _root.Git.WorktreeList().Where(w => !w.Bare).OrderBy(w => w.Branch, StringComparer.OrdinalIgnoreCase))
        {
            Cancellation.ThrowIfRequested();
            var branch = worktree.Branch ?? _root.Git.RebaseHeadName(worktree.Path);
            if (branch == null || !bases.TryGetValue(branch, out var checkout)) continue;
            ReviewInboxWorktree entry;
            string? document = null;
            try
            {
                if (!Directory.Exists(worktree.Path)) throw new SgException("Worktree folder is unavailable.");
                document = CodeReview.ExistingDocument(_root, worktree.Path);
                var snapshot = document == null ? new ReviewSnapshot(new(), "missing") : ReviewFeed.ReadSnapshot(document);
                var items = snapshot.Data.Threads.Select(Summarize).OrderByDescending(t => t.Updated).ThenBy(t => t.Id, StringComparer.Ordinal).ToArray();
                entry = new(worktree.Path, branch, checkout, items, snapshot.Revision, Identity: document);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SgException)
            { entry = new(worktree.Path, branch, checkout, [], "unavailable", e.Message, document); }
            result.Add(entry); progress?.Invoke(entry);
        }
        return result;
    }
    static ReviewInboxItem Summarize(CodeThread thread)
    {
        var latest = thread.Events.MaxBy(e => e.At)!;
        var first = thread.Events[0]; var anchor = thread.Anchor;
        return new(thread.Id, anchor.File, anchor.Side, anchor.First, anchor.Last, thread.State, thread.Conflict,
            first.Body, first.Actor, latest.Body, latest.Actor, latest.Action, latest.At, thread.Events.Count - 1);
    }
    public static bool Matches(ReviewInboxItem item, ReviewInboxWorktree worktree, string query, string state = "open") =>
        (state == "all" || item.State == state) && (string.IsNullOrWhiteSpace(query) ||
        string.Join('\n', worktree.Branch, worktree.Checkout, item.File, item.Comment, item.Author, item.LatestBody, item.LatestActor)
            .Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    public static ReviewInboxResult Query(SgRoot root, string state = "open", string query = "", string? worktree = null, int offset = 0)
    {
        if (state is not ("open" or "resolved" or "all") || offset < 0) throw new SgException("Invalid state or offset.");
        using var inbox = new ReviewInbox(root);
        var entries = inbox.Read();
        var rows = entries.Where(w => worktree == null || w.Branch.Equals(worktree, StringComparison.OrdinalIgnoreCase) || w.Path.Equals(worktree, StringComparison.OrdinalIgnoreCase))
            .SelectMany(w => w.Items.Where(t => Matches(t, w, query, state)).Select(t => new ReviewInboxRow(w.Path, w.Branch, w.Checkout, t)))
            .OrderByDescending(r => r.Thread.Updated).ThenBy(r => r.Branch, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Thread.Id, StringComparer.Ordinal).ToArray();
        return new(1, rows.Length, rows.Skip(offset).Take(100).ToArray(), offset < rows.Length - 100 ? offset + 100 : null,
            entries.Where(w => w.Error != null).Select(w => new ReviewInboxError(w.Path, w.Branch, w.Error!)).ToArray());
    }
    public void Dispose() => _observation.Dispose();
}
