using System.Text;

namespace Sg.Core;

public static partial class Backup
{
    // A small independent commit per worktree, like code review metadata. It follows the
    // worktree into its matching checkout, without adding files to any source-code tree.
    static CheckoutAppearanceData? FetchAppearance(SgRoot root, BackupConfig cfg, string name, IReadOnlyDictionary<string, string> remote)
    {
        var reference = RemoteRef(cfg, "appearance", name);
        if (!remote.TryGetValue(reference, out var sha)) return null;
        var fetched = FetchedRef("appearance", name);
        if (root.Git.RefSha(fetched) != sha) root.Git.FetchRefs(cfg.Url, ["+" + reference + ":" + fetched]);
        if (root.Git.RefSha(fetched) != sha) throw new SgException("Checkout appearance backup changed during fetch. Refresh and try again.");
        var blob = root.Git.Out(null, "rev-parse", sha + ":appearance.json");
        if (long.Parse(root.Git.Out(null, "cat-file", "-s", blob)) > CheckoutAppearance.MaxEncodedBytes)
            throw new SgException("Saved checkout appearance exceeds the supported size.");
        return CheckoutAppearance.Decode(Encoding.UTF8.GetBytes(root.Git.Out(null, "cat-file", "blob", blob)));
    }

    static void BackUpAppearance(SgRoot root, BackupResult result, bool check)
    {
        var bases = root.Git.BranchBases();
        var branches = result.Items.Where(i => i.Kind == "branch" && i.State is "pushed" or "up to date" or "would push" or "would reconcile")
            .Select(i => (i.Name, Checkout: root.Config.Checkouts.FirstOrDefault(c => c.Name == bases.GetValueOrDefault(i.Name))))
            .Where(b => b.Checkout != null).ToArray();
        if (branches.Length == 0) return;
        var cfg = Require(root);
        var remote = root.Git.LsRemote(cfg.Url);
        foreach (var (name, checkout) in branches)
        {
            var item = new BackupItem { Kind = "appearance", Name = name, RemoteRef = RemoteRef(cfg, "appearance", name) };
            if (checkout!.Icon == null && !remote.ContainsKey(item.RemoteRef)) continue;
            result.Items.Add(item);
            try
            {
                var saved = FetchAppearance(root, cfg, name, remote);
                // A machine that never chose an appearance must not erase another machine's choice.
                // Still account for that ref in coverage and restore rehearsals.
                if (checkout.Icon == null)
                {
                    item.Thin = remote[item.RemoteRef]; item.State = "up to date"; continue;
                }
                var payload = CheckoutAppearance.Encode(CheckoutAppearance.Capture(root, checkout)!);
                if (cfg.MaxFileBytes > 0 && payload.Length > cfg.MaxFileBytes || cfg.MaxPushBytes > 0 && payload.Length > cfg.MaxPushBytes)
                    throw new SgException("Checkout appearance exceeds the configured backup size limit.");
                var have = remote.GetValueOrDefault(item.RemoteRef);
                if (saved != null && payload.AsSpan().SequenceEqual(CheckoutAppearance.Encode(saved)))
                {
                    item.Thin = have!; item.State = "up to date"; continue;
                }
                var blob = root.Git.HashBlob(payload);
                var tree = root.Git.MakeTree([new("100644", "blob", blob, "appearance.json")]);
                item.Thin = root.Git.CommitTree(tree, have, "sg checkout appearance: " + name + "\n");
                if (check) { item.State = "would push"; continue; }
                var answers = root.Git.PushRefs(cfg.Url, [new PushRef(item.Thin, item.RemoteRef, have)], force: false);
                if (!answers.TryGetValue(item.RemoteRef, out var answer) || !answer.Ok)
                    throw new SgException("Checkout appearance backup changed or was rejected. Refresh and try again.");
                root.Git.UpdateRef(PushedRef("appearance", name), item.Thin);
                item.State = "pushed";
            }
            catch (Exception e) when (e is SgException or IOException or UnauthorizedAccessException)
            {
                item.State = "failed"; item.Why = e.Message;
            }
        }
    }

    static void RestoreAppearance(SgRoot root, CheckoutConfig checkout, CheckoutAppearanceData? appearance, RestoreResult result)
    {
        var restored = CheckoutAppearance.RestoreIfUnset(root, checkout, appearance);
        result.CheckoutAppearanceRestored = restored.Restored;
        result.CheckoutAppearanceWarning = restored.Warning;
    }
}
