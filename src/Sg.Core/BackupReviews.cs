using System.Text;

namespace Sg.Core;

public static partial class Backup
{
    static CodeReviewData ReadReviewCommit(SgRoot root, string sha)
    {
        var blob = root.Git.Out(null, "rev-parse", sha + ":review.json");
        if (long.Parse(root.Git.Out(null, "cat-file", "-s", blob)) > CodeReview.MaxDataBytes) throw new SgException("Saved code review exceeds the supported size.");
        return CodeReview.Decode(root.Git.Run(null, "cat-file", "blob", blob).EnsureOk().StdOut);
    }
    static CodeReviewData? FetchReview(SgRoot root, BackupConfig cfg, string name, IReadOnlyDictionary<string, string> remote)
    {
        var reference = RemoteRef(cfg, "review", name);
        if (!remote.TryGetValue(reference, out var sha)) return null;
        var fetched = FetchedRef("review", name);
        if (root.Git.RefSha(fetched) != sha) root.Git.FetchRefs(cfg.Url, ["+" + reference + ":" + fetched]);
        if (root.Git.RefSha(fetched) != sha) throw new SgException("Code review backup changed during fetch. Refresh and try again.");
        return ReadReviewCommit(root, sha);
    }
    static void BackUpReviews(SgRoot root, BackupResult result, bool check)
    {
        var cfg = Require(root);
        var remote = root.Git.LsRemote(cfg.Url);
        var worktrees = root.Git.WorktreeList();
        foreach (var branch in result.Items.Where(i => i.Kind == "branch" && i.State is "pushed" or "up to date" or "would push" or "would reconcile").ToArray())
        {
            var path = worktrees.FirstOrDefault(w => w.Branch == branch.Name)?.Path;
            if (path == null || !Directory.Exists(path)) continue;
            var item = new BackupItem { Kind = "review", Name = branch.Name, RemoteRef = RemoteRef(cfg, "review", branch.Name) };
            try
            {
                var local = CodeReview.Read(root, path);
                var saved = FetchReview(root, cfg, branch.Name, remote);
                if (local.Threads.Count == 0 && saved == null) continue;
                result.Items.Add(item);
                var merged = CodeReview.Merge(local, saved ?? new());
                var payload = CodeReview.Encode(merged);
                var bytes = Encoding.UTF8.GetByteCount(payload);
                if (cfg.MaxFileBytes > 0 && bytes > cfg.MaxFileBytes || cfg.MaxPushBytes > 0 && bytes > cfg.MaxPushBytes)
                    throw new SgException("Code review context exceeds the configured backup size limit. Increase the limit to include it.");
                var have = remote.GetValueOrDefault(item.RemoteRef);
                if (!check) merged = CodeReview.Import(root, path, merged);
                if (saved != null && payload == CodeReview.Encode(CodeReview.Merge(saved, new())))
                {
                    item.Thin = have!; item.State = "up to date"; continue;
                }
                // Independent metadata commit: never inject annotations or context into a source-code tree.
                var blob = root.Git.HashBlob(Encoding.UTF8.GetBytes(payload));
                var tree = root.Git.MakeTree([new("100644", "blob", blob, "review.json")]);
                item.Thin = root.Git.CommitTree(tree, have, "sg code review: " + branch.Name + "\n");
                if (check) { item.State = "would push"; continue; }
                var answers = root.Git.PushRefs(cfg.Url, [new PushRef(item.Thin, item.RemoteRef, have)], force: false);
                if (!answers.TryGetValue(item.RemoteRef, out var answer) || !answer.Ok) throw new SgException("Code review backup changed or was rejected. Retry to merge the latest feedback.");
                root.Git.UpdateRef(PushedRef("review", branch.Name), item.Thin);
                item.State = "pushed";
            }
            catch (Exception e) when (e is SgException or IOException or UnauthorizedAccessException)
            {
                if (!result.Items.Contains(item)) result.Items.Add(item);
                item.State = "failed"; item.Why = e.Message;
            }
        }
    }
}
