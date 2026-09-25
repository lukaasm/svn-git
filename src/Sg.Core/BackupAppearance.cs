using System.Text;
using System.Text.Json;

namespace Sg.Core;

public static partial class Backup
{
    // A small independent commit per worktree, like code review metadata. It follows the
    // worktree into its matching checkout, without adding files to any source-code tree.
    sealed record SavedAppearance(int Format, byte[]? Icon);
    const int MaxAppearanceBytes = CheckoutAppearance.MaxBytes * 4 / 3 + 1024;

    static SavedAppearance? FetchAppearance(SgRoot root, BackupConfig cfg, string name, IReadOnlyDictionary<string, string> remote)
    {
        var reference = RemoteRef(cfg, "appearance", name);
        if (!remote.TryGetValue(reference, out var sha)) return null;
        var fetched = FetchedRef("appearance", name);
        if (root.Git.RefSha(fetched) != sha) root.Git.FetchRefs(cfg.Url, ["+" + reference + ":" + fetched]);
        if (root.Git.RefSha(fetched) != sha) throw new SgException("Checkout appearance backup changed during fetch. Refresh and try again.");
        var blob = root.Git.Out(null, "rev-parse", sha + ":appearance.json");
        if (long.Parse(root.Git.Out(null, "cat-file", "-s", blob)) > MaxAppearanceBytes)
            throw new SgException("Saved checkout appearance exceeds the supported size.");
        SavedAppearance appearance;
        try
        {
            appearance = JsonSerializer.Deserialize<SavedAppearance>(root.Git.Out(null, "cat-file", "blob", blob))
                ?? throw new SgException("Invalid checkout appearance backup.");
        }
        catch (JsonException) { throw new SgException("Invalid checkout appearance backup."); }
        if (appearance.Format != 1) throw new SgException("Unsupported checkout appearance backup format.");
        if (appearance.Icon != null) CheckoutAppearance.Validate(appearance.Icon);
        return appearance;
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
                var local = new SavedAppearance(1, checkout.Icon == "" ? null : CheckoutAppearance.ReadIcon(root, checkout));
                var payload = JsonSerializer.SerializeToUtf8Bytes(local);
                if (cfg.MaxFileBytes > 0 && payload.Length > cfg.MaxFileBytes || cfg.MaxPushBytes > 0 && payload.Length > cfg.MaxPushBytes)
                    throw new SgException("Checkout appearance exceeds the configured backup size limit.");
                var have = remote.GetValueOrDefault(item.RemoteRef);
                if (saved != null && payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(saved)))
                {
                    item.Thin = have!; item.State = "up to date"; continue;
                }
                var blob = root.Git.Run(null, ["hash-object", "-w", "--stdin"], payload).EnsureOk().StdOut.Trim();
                var tree = root.Git.Run(null, ["mktree"], Encoding.UTF8.GetBytes("100644 blob " + blob + "\tappearance.json\n")).EnsureOk().StdOut.Trim();
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

    static void RestoreAppearance(SgRoot root, CheckoutConfig checkout, SavedAppearance? appearance, RestoreResult result)
    {
        // Both a custom image and an explicit choice of initials belong to this machine.
        if (appearance == null || checkout.Icon != null) return;
        try
        {
            CheckoutAppearance.SetIcon(root, checkout.Name, appearance.Icon);
            result.CheckoutAppearanceRestored = true;
        }
        catch (Exception e) when (e is SgException or IOException or UnauthorizedAccessException)
        {
            // Work has already been recovered. A cosmetic write failure must not hide that result.
            result.CheckoutAppearanceWarning = "Work recovered, but the checkout appearance could not be saved: " + e.Message;
            root.Log.Warn(result.CheckoutAppearanceWarning);
        }
    }
}
