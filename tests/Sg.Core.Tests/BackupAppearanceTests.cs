using System.Text;
using System.Text.Json;

namespace Sg.Core.Tests;

public sealed partial class BackupTests
{
    static readonly byte[] IconPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1V8AAAAASUVORK5CYII=");

    [Fact]
    public void Appearance_backup_is_scoped_portable_idempotent_and_pruned_with_its_worktree()
    {
        f.Setup();
        var path = MakeBranch("selected");
        MakeBranch("untouched");
        var cfg = Backup.Set(f.Root, Remote(), prefix: "desktop", uncommitted: false);
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, IconPng);
        var reference = Backup.RemoteRef(cfg, "appearance", "selected");
        var preview = Backup.Run(f.Root, check: true, worktree: "selected");
        Assert.Equal("would push", Item(preview, "appearance", "selected").State);
        Assert.Empty(f.Root.Git.LsRemote(_remote));
        Assert.True(Backup.Run(f.Root, worktree: "selected").Ok);
        var remote = f.Root.Git.LsRemote(_remote);
        Assert.Equal(2, remote.Count); // Just the selected branch and its appearance; no source image path.
        Assert.Contains(reference, remote.Keys);
        Assert.Equal("up to date", Item(Backup.Run(f.Root, worktree: "selected"), "appearance", "selected").State);
        Assert.Equal(remote[reference], f.Root.Git.LsRemote(_remote)[reference]);
        Assert.True(Assert.Single(Backup.List(f.Root)).HasAppearance);
        var catalog = Backup.Browse(f.Root);
        var selected = Assert.Single(catalog.Items);
        Assert.True(selected.HasAppearance);
        var planned = Backup.Preview(f.Root, catalog, selected);
        Assert.Equal(IconPng, planned.AppearanceIcon);
        Assert.Equal(remote[reference], planned.ExpectedRefs[reference]);
        var coverage = Backup.Coverage(f.Root, path);
        Assert.Contains(coverage.Coverage, c => c.Kind == "appearance" && c.Files.SequenceEqual([".sg/icons/" + f.Co.Icon]));
        Assert.Empty(Backup.ValidateReceipt(f.Root, coverage));

        var (far, co) = Far();
        Backup.Set(far, _remote, prefix: "desktop");
        // Checkout matching uses repository identity, not the source checkout's display name.
        Ops.UpdateCheckout(far, co, new Ops.CheckoutEdit(Name: "OtherMachine"));
        var farCatalog = Backup.Browse(far);
        var farPreview = Backup.Preview(far, farCatalog, Assert.Single(farCatalog.Items));
        Assert.Equal(IconPng, farPreview.AppearanceIcon);
        Assert.Null(co.Icon);
        Assert.False(Directory.Exists(Path.Combine(far.StorePath, "icons")));
        var restored = Backup.Restore(far, "selected", asBranch: "renamed", intoCheckout: co.Name, expectedRefs: planned.ExpectedRefs);
        Assert.True(restored.Ok);
        Assert.True(restored.CheckoutAppearanceRestored);
        Assert.Equal(IconPng, File.ReadAllBytes(CheckoutAppearance.IconPath(far, co)!));
        Assert.Equal(f.Co.Icon, SgConfig.Load(far.ConfigPath).Checkouts.Single().Icon);
        Assert.DoesNotContain("appearance.json", far.Git.Out(restored.Path, "ls-files"));

        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, null);
        Assert.StartsWith("Changed since receipt", Backup.LocalReceiptStatus(f.Root, path, coverage));
        Backup.Exclude(f.Root, "selected");
        Assert.True(Backup.Run(f.Root).Ok);
        Assert.Equal(remote[reference], f.Root.Git.LsRemote(_remote)[reference]);
        var prune = Backup.PlanPrune(f.Root, "selected");
        Assert.Equal(2, prune.Refs.Count);
        Assert.Contains(reference, prune.Refs.Keys);
        Backup.Prune(f.Root, prune);
        Assert.DoesNotContain(reference, f.Root.Git.LsRemote(_remote).Keys);
        Assert.Contains(Backup.RemoteRef(cfg, "appearance", "untouched"), f.Root.Git.LsRemote(_remote).Keys);
    }

    [Fact]
    public void Appearance_restore_preserves_local_choices_and_rehearsals_do_not_change_the_checkout()
    {
        f.Setup();
        MakeBranch("selected");
        Backup.Set(f.Root, Remote());
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, IconPng);
        Assert.True(Backup.Run(f.Root).Ok);
        var (far, co) = Far();
        var rehearsal = Backup.Restore(far, "selected", asBranch: "rehearsal", rehearsal: true);
        Assert.True(rehearsal.Ok);
        Assert.False(rehearsal.CheckoutAppearanceRestored);
        Assert.Null(co.Icon);
        Assert.False(Directory.Exists(Path.Combine(far.StorePath, "icons")));
        var preserved = Backup.Run(far, worktree: "rehearsal");
        Assert.True(preserved.Ok);
        Assert.DoesNotContain(preserved.Items, i => i.Kind == "appearance");

        CheckoutAppearance.SetIcon(far, co.Name, null); // User explicitly chose initials.
        Assert.False(Backup.Restore(far, "selected").CheckoutAppearanceRestored);
        Assert.Equal("", co.Icon);
        Assert.False(Backup.Pull(far, "selected").CheckoutAppearanceRestored);
        Assert.Equal("", co.Icon);

        CheckoutAppearance.SetIcon(far, co.Name, IconPng);
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, null); // Remote now explicitly uses initials.
        Assert.True(Backup.Run(f.Root).Ok);
        Assert.False(Backup.Restore(far, "selected", asBranch: "another").CheckoutAppearanceRestored);
        Assert.False(Backup.Pull(far, "selected").CheckoutAppearanceRestored);
        Assert.Equal(IconPng, File.ReadAllBytes(CheckoutAppearance.IconPath(far, co)!));
    }

    [Fact]
    public void Appearance_pull_fills_an_unconfigured_checkout_and_rejects_stale_previews()
    {
        f.Setup();
        var path = MakeBranch("selected");
        var cfg = Backup.Set(f.Root, Remote());
        Assert.True(Backup.Run(f.Root).Ok); // Old backups and automatic initials need no appearance ref.
        var (far, co) = Far();
        Assert.True(Backup.Restore(far, "selected").Ok);
        Assert.Null(co.Icon);
        var catalog = Backup.Browse(far);
        var before = Backup.Preview(far, catalog, Assert.Single(catalog.Items));
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, IconPng);
        Assert.True(Backup.Run(f.Root).Ok);
        Assert.Throws<SgException>(() => Backup.Restore(far, "selected", asBranch: "stale", expectedRefs: before.ExpectedRefs));
        Assert.Null(far.Git.RefSha("refs/heads/stale"));
        Assert.True(Backup.Pull(far, "selected").CheckoutAppearanceRestored);
        Assert.Equal(IconPng, File.ReadAllBytes(CheckoutAppearance.IconPath(far, co)!));
        catalog = Backup.Browse(far);
        before = Backup.Preview(far, catalog, Assert.Single(catalog.Items));
        var coverage = Backup.Coverage(f.Root, path);
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, null);
        Assert.True(Backup.Run(f.Root).Ok);
        Assert.Throws<SgException>(() => Backup.Restore(far, "selected", asBranch: "stale", expectedRefs: before.ExpectedRefs));
        Assert.NotEmpty(Backup.ValidateReceipt(f.Root, coverage));
        var remote = f.Root.Git.LsRemote(_remote);
        var json = RemoteGit("show", remote[Backup.RemoteRef(cfg, "appearance", "selected")] + ":appearance.json");
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(json).RootElement.GetProperty("Icon").ValueKind);

        var (fresh, freshCo) = Far("fresh");
        var resetCatalog = Backup.Browse(fresh);
        var resetPreview = Backup.Preview(fresh, resetCatalog, Assert.Single(resetCatalog.Items));
        Assert.True(resetPreview.Entry.HasAppearance);
        Assert.Null(resetPreview.AppearanceIcon);
        Assert.Null(freshCo.Icon);
        var reset = Backup.Restore(fresh, "selected");
        Assert.True(reset.CheckoutAppearanceRestored);
        Assert.Equal("", freshCo.Icon);
    }

    [Fact]
    public void Appearance_missing_assets_fail_backup_without_erasing_remote_and_bad_metadata_is_rejected_before_restore()
    {
        f.Setup();
        MakeBranch("selected");
        var cfg = Backup.Set(f.Root, Remote());
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, IconPng);
        Assert.True(Backup.Run(f.Root).Ok);
        var success = Backup.LastSuccess(f.Root);
        var reference = Backup.RemoteRef(cfg, "appearance", "selected");
        var saved = f.Root.Git.LsRemote(_remote)[reference];
        File.Delete(CheckoutAppearance.IconPath(f.Root, f.Co)!);
        var failed = Backup.Run(f.Root);
        Assert.False(failed.Ok);
        Assert.True(Item(failed, "appearance", "selected").Failed);
        Assert.Equal(saved, f.Root.Git.LsRemote(_remote)[reference]);
        Assert.Equal(success, Backup.LastSuccess(f.Root));

        var (blocked, blockedCo) = Far("blocked");
        File.WriteAllText(Path.Combine(blocked.StorePath, "icons"), "cannot create a folder here");
        var recovered = Backup.Restore(blocked, "selected");
        Assert.True(recovered.Ok);
        Assert.Equal(2, recovered.Applied);
        Assert.NotNull(recovered.CheckoutAppearanceWarning);
        Assert.Equal(TaskState.NeedsAttention, TaskResults.Describe(recovered).State);
        Assert.Null(blockedCo.Icon);

        var (far, co) = Far();
        foreach (var payload in new[] { "not json", "{\"Format\":2}", "{\"Format\":1,\"Icon\":\"eA==\"}", new string(' ', 800_000) })
        {
            var blob = Proc.Run("git", ["-C", _remote, "hash-object", "-w", "--stdin"], null, f.Log, Encoding.UTF8.GetBytes(payload)).EnsureOk().StdOut.Trim();
            var tree = Proc.Run("git", ["-C", _remote, "mktree"], null, f.Log, Encoding.UTF8.GetBytes("100644 blob " + blob + "\tappearance.json\n")).EnsureOk().StdOut.Trim();
            var commit = RemoteGit("-c", "user.name=Test", "-c", "user.email=test@example.com", "commit-tree", tree, "-m", "malformed appearance").Trim();
            RemoteGit("update-ref", reference, commit);
            var invalidCatalog = Backup.Browse(far);
            Assert.Throws<SgException>(() => Backup.Preview(far, invalidCatalog, Assert.Single(invalidCatalog.Items)));
            Assert.Throws<SgException>(() => Backup.Restore(far, "selected"));
            Assert.Null(far.Git.RefSha("refs/heads/selected"));
            Assert.Null(co.Icon);
            Assert.False(Directory.Exists(Path.Combine(far.StorePath, "icons")));
        }
    }
}
