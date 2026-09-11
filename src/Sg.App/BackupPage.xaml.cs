using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

/// <summary>One thing the backup holds, as a row: a branch to pick, or a shelf or a wip to know about.</summary>
public sealed class BackupRow
{
    public BackupEntry Entry { get; init; } = new();
    public string Name => Entry.Name;

    public string What => Entry.Unreadable != null ? "cannot be read"
        : Entry.Kind == "branch" ? (Entry.Commits == 1 ? "1 commit" : Entry.Commits + " commits") + (Entry.HasWip ? " + wip" : "")
        : Entry.Kind == "wip" ? "uncommitted changes of " + Entry.Branch
        : Entry.Kind == "edits" ? "local edits of checkout " + Entry.Branch
        : "shelf \"" + Entry.Title + "\"" + (Entry.Branch.Length > 0 ? " of " + Entry.Branch : "");

    public string From => Entry.Unreadable ?? $"from {(Entry.Checkout.Length > 0 ? Entry.Checkout : Entry.Url)} r{Entry.Revision}"
                                             + (Entry.Last is { } t ? $"   {t.LocalDateTime:yyyy-MM-dd HH:mm}" : "");

    /// <summary>What stands between this and a restore, in two words: it is here already, or the checkout moved on.</summary>
    public string Note => Entry.Unreadable != null ? "" : Entry.ExistsHere ? "here" : Entry.Drift.Count > 0 ? "checkout moved on" : Entry.Checkout.Length == 0 ? "no checkout matches" : "";

    public Brush NoteBrush => (Brush)Application.Current.Resources[
        Entry.ExistsHere || Entry.Unreadable != null ? "TextFillColorTertiaryBrush" : Note.Length > 0 ? "StatusModifiedBrush" : "TextFillColorSecondaryBrush"];

    public string Tip => Entry.Unreadable ?? (Entry.Url.Length > 0 ? Entry.Url + " r" + Entry.Revision : Entry.Name);
}

/// <summary>
/// What the backup repository holds, read before anything is made. Pick a branch, read what it was cut
/// from beside what this checkout is at, and only then is Restore offered. Back up now and Prune live
/// here too, because they are about the same place.
/// </summary>
public sealed partial class BackupPage : SgPage
{
    List<BackupEntry> _entries = new();
    BackupEntry? _picked;
    bool _binding;

    public BackupPage()
    {
        InitializeComponent();
        Title = "Backup";
        Session.Log.Sink = Pane;
        _ = LoadAsync();
    }

    CheckoutConfig? Into => IntoBox.SelectedItem is string name ? Session.Root?.Checkout(name) : null;

    async Task LoadAsync()
    {
        var root = Session.Require();
        var cfg = root.Config.Backup;
        var configured = Backup.Configured(root);
        NoBackup.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        Filled.Visibility = Nothing.Visibility = Visibility.Collapsed;
        RestoreRow.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        BackupNowButton.IsEnabled = PruneButton.IsEnabled = configured;
        RestoreButton.IsEnabled = false;
        if (!configured)
        {
            Subtitle = "no backup repository";
            Summary.Text = "Set the URL in Settings first.";
            return;
        }
        Subtitle = cfg!.Url;
        UrlText.Text = cfg.Url + (cfg.Prefix.Length > 0 ? "   under " + cfg.Prefix + "/" : "");

        var list = await Runner.Quiet(Pane, () => Backup.List(root));
        if (list == null)
        {
            Summary.Text = "The line at the bottom says why the backup could not be read.";
            return;
        }
        _entries = list;
        var branches = list.Where(e => e.Kind == "branch").Select(e => new BackupRow { Entry = e }).ToList();
        var others = list.Where(e => e.Kind != "branch").Select(e => new BackupRow { Entry = e }).ToList();
        if (list.Count == 0)
        {
            Nothing.Visibility = Visibility.Visible;
            Summary.Text = "";
            return;
        }
        Filled.Visibility = Visibility.Visible;
        Headline.Text = $"{branches.Count} branch(es), {others.Count(o => o.Entry.Kind == "shelf")} shelf/shelves, "
                        + $"{others.Count(o => o.Entry.Kind is "wip" or "edits")} folder(s) with uncommitted changes";
        BranchesHeader.Text = branches.Count == 1 ? "Branch" : $"Branches ({branches.Count})";
        Branches.ItemsSource = branches;
        OthersHeader.Visibility = OthersCard.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Others.ItemsSource = others;

        _binding = true;
        IntoBox.ItemsSource = root.Config.Checkouts.Select(c => c.Name).ToList();
        _binding = false;

        // The first branch that is not here yet is what the page was most likely opened for.
        var first = branches.FirstOrDefault(b => !b.Entry.ExistsHere && b.Entry.Unreadable == null) ?? branches.FirstOrDefault();
        if (first != null) Branches.SelectedItem = first;
        else Show(null);
    }

    void Branch_Changed(object sender, SelectionChangedEventArgs e) => Show((Branches.SelectedItem as BackupRow)?.Entry);

    /// <summary>One branch on screen: its commits, its bases beside the ones here, and the restore controls filled for it.</summary>
    void Show(BackupEntry? entry)
    {
        _picked = entry;
        var has = entry != null && entry.Unreadable == null;
        CommitsHeader.Visibility = CommitsCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        BasesHeader.Visibility = BasesCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has)
        {
            DriftBar.IsOpen = false;
            SyncButton();
            return;
        }
        CommitsHeader.Text = entry!.Commits == 1 ? "Commit" : $"Commits ({entry.Commits})";
        Subjects.ItemsSource = entry.Commits == 0 ? new List<string> { "None: the branch equals its snapshot. Restoring it makes the worktree again, empty." } : entry.Subjects;
        _binding = true;
        NameBox.Text = entry.Name;
        IntoBox.SelectedItem = entry.Checkout.Length > 0 ? entry.Checkout : null;
        WipBox.IsChecked = entry.HasWip;
        WipBox.IsEnabled = entry.HasWip;
        _binding = false;
        ShowBases();
        SyncButton();
    }

    void ShowBases()
    {
        var entry = _picked;
        var co = Into;
        if (entry == null) return;
        var drift = co == null ? new List<ExportDrift>() : Export.DriftOf(Session.Require(), MetaOf(entry), co);
        var rows = entry.Bases.Select(b =>
        {
            var d = drift.FirstOrDefault(x => x.Where == b.Where);
            return new ImportBaseRow
            {
                Where = b.Where,
                Url = b.Url,
                Exported = "r" + b.Revision,
                Local = d == null ? "r" + b.Revision : d.Elsewhere ? "another branch" : d.Missing ? "not here" : "r" + d.Local,
                Differs = d != null,
                Note = d?.Elsewhere == true ? d.LocalUrl : null,
            };
        }).ToList();
        Bases.ItemsSource = rows;
        var moved = rows.Where(r => r.Differs).ToList();
        DriftBar.IsOpen = moved.Count > 0;
        DriftBar.Message = moved.Count == 0 ? ""
            : "Every commit is merged across the difference, the way a rebase does, so read the diff before you push. "
              + string.Join(", ", moved.Take(6).Select(r => $"{r.Where} {r.Exported} → {r.Local}"))
              + (moved.Count > 6 ? $", and {moved.Count - 6} more" : "");
    }

    /// <summary>The entry as the export reader sees an export, so the drift is read by the same code.</summary>
    static ExportMeta MetaOf(BackupEntry e) => new() { Branch = e.Name, Bases = e.Bases };

    void SyncButton()
    {
        var entry = _picked;
        var name = NameBox.Text.Trim();
        var taken = name.Length > 0 && Session.Root?.Git.RefSha("refs/heads/" + name) != null;
        var force = ForceBox.IsChecked == true;
        RestoreButton.IsEnabled = entry != null && entry.Unreadable == null && name.Length > 0 && Into != null && (!taken || force);
        RestoreLabel.Text = entry == null ? "Restore" : force && taken ? $"Overwrite with {entry.Commits} commit(s)" : $"Restore {entry.Commits} commit(s)";
        Summary.Text = entry == null ? "Pick a branch."
            : entry.Unreadable != null ? entry.Unreadable
            : Into == null && entry.Checkout.Length == 0 ? $"No checkout here points at {entry.Url}. Pick one only if you know it is the same repository."
            : Into == null ? "Pick the checkout to build it on."
            : name.Length == 0 ? "Give the branch a name."
            : taken && !force ? $"{name} is already a branch here. Tick Overwrite to write over it, or give it another name."
            : taken ? $"{name} here is written over with the backup version. Its worktree is reset and any uncommitted changes in it are dropped."
            : $"{name} will be made on {Into.Name}, and its worktree with it.";
    }

    void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_binding) SyncButton();
    }

    void Force_Changed(object sender, RoutedEventArgs e)
    {
        if (!_binding) SyncButton();
    }

    void Into_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        ShowBases();
        SyncButton();
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var entry = _picked;
        var co = Into;
        if (entry == null || co == null) return;
        var name = NameBox.Text.Trim();
        var wip = WipBox.IsChecked == true && entry.HasWip;
        var root = Session.Require();
        var force = ForceBox.IsChecked == true;
        var overwrite = force && root.Git.RefSha("refs/heads/" + name) != null;

        var confirmed = overwrite
            ? await Dialogs.Confirm(this, "Overwrite " + name,
                $"A branch {name} is already here. Write over it with the backup version?\n\n"
                + $"Its worktree at {root.WorktreePathFor(name)} is reset to what the backup holds, and any uncommitted changes in it are dropped. "
                + $"{entry.Commits} commit(s) are merged onto the snapshot this checkout has now" + (wip ? ", and the uncommitted changes come back after them" : "")
                + ". Nothing goes to SVN.", "Overwrite")
            : await Dialogs.Confirm(this, "Restore " + name,
                $"Make the branch {name} on {co.Name}, and a worktree folder for it at {root.WorktreePathFor(name)}?\n\n"
                + $"{entry.Commits} commit(s) are merged onto the snapshot this checkout has now"
                + (wip ? ", and the uncommitted changes come back after them" : "") + ". Nothing goes to SVN.", "Restore");
        if (!confirmed) return;

        var res = await Busy.During(sender, () => Runner.Run(Pane, "restore " + name,
            () => Backup.Restore(root, entry.Name, name == entry.Name ? null : name, co.Name, wip, force)), restoreEnabled: false);
        if (res == null) { SyncButton(); return; }

        var lines = new List<string>();
        lines.Add(res.Ok
            ? $"{res.Branch} is here: {res.Applied} commit(s) in {res.Path}." + (res.Replaced ? " It wrote over the branch that was here." : "") + (res.Relinked ? " The store still had them, so nothing was replayed." : "")
              + (res.Drift.Count == 0 ? "" : $" They were merged across {res.Drift.Count} revision(s) that had moved on.")
            : $"{res.Applied} of {res.Commits} commit(s) went in. \"{res.Stopped}\" would not merge, so it and everything after it are not on the branch."
              + (res.Why == null ? "" : "\n" + res.Why.Split('\n')[0]));
        if (res.WipShelf != null)
            lines.Add(res.WipWritten
                ? "The uncommitted changes are written into the worktree" + (res.WipConflicted.Count > 0 ? $", {res.WipConflicted.Count} of them with conflict markers." : ".")
                : $"The uncommitted changes wait on the shelf as {res.WipShelf}: {res.WipConflicted.Count} file(s) would not merge.");
        if (res.Shelves.Count > 0) lines.Add("Shelves made again: " + string.Join(", ", res.Shelves) + ".");
        ResultBar.Severity = res.Ok && res.WipConflicted.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = string.Join("\n", lines);
        ResultBar.IsOpen = true;

        var conflicts = res.Conflicted.Concat(res.WipConflicted).ToList();
        ConflictsHeader.Visibility = ConflictsCard.Visibility = conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Conflicts.ItemsSource = conflicts;

        entry.ExistsHere = true;
        RestoreButton.IsEnabled = false;
        Summary.Text = res.Ok ? "Done. Close this and the worktree is on the checkout's card." : "The branch is there with what did go in.";
    }

    async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Require();
        var res = await Busy.During(sender, () => Runner.Run(Pane, "backup", () => Backup.Run(root)));
        if (res == null) return;
        var rejected = res.Items.Where(i => i.Rejected).ToList();
        ResultBar.Severity = res.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = Sentence(res);
        ResultBar.IsOpen = true;
        if (rejected.Count > 0 && rejected[0].Why != null) Pane.Append(rejected[0].Why!);
        await LoadAsync();
    }

    /// <summary>One line about a backup that ran: what went, what was there already, what was refused.</summary>
    public static string Sentence(BackupResult res)
    {
        var up = res.Items.Count(i => i.State == "up to date");
        var reconciled = res.Items.Count(i => i.Pushed && i.Reconciled);
        var behind = res.Items.Where(i => i.Behind).Select(i => i.Name).ToList();
        var rejected = res.Items.Where(i => i.Rejected).Select(i => i.Name).ToList();
        var failed = res.Items.Where(i => i.Failed).Select(i => i.Name).ToList();
        var parts = new List<string>();
        if (res.Pushed > 0) parts.Add(res.Pushed + " sent" + (reconciled > 0 ? $" ({reconciled} over an older copy from another machine)" : ""));
        if (up > 0) parts.Add(up + " already there");
        if (behind.Count > 0) parts.Add("newer on the remote for " + string.Join(", ", behind) + " (restore to bring it here)");
        if (rejected.Count > 0) parts.Add("diverged for " + string.Join(", ", rejected) + " (another machine has different work under this name)");
        if (failed.Count > 0) parts.Add("failed for " + string.Join(", ", failed));
        if (parts.Count == 0) parts.Add("nothing here to back up");
        return "Backup: " + string.Join(", ", parts) + ".";
    }

    async void Prune_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Require();
        var gone = await Busy.During(sender, () => Runner.Run(Pane, "prune", () => Backup.Prune(root, delete: false)));
        if (gone == null) return;
        if (gone.Count == 0)
        {
            await Dialogs.Info(this, "Nothing to prune", "Everything on the backup answers to something here.");
            return;
        }
        if (!await Dialogs.Confirm(this, "Delete from the backup",
                $"{gone.Count} ref(s) on the backup answer to nothing here any more:\n\n" + string.Join("\n", gone.Take(20))
                + (gone.Count > 20 ? $"\nand {gone.Count - 20} more" : "") + "\n\nDelete them there? What is here is not touched.",
                "Delete"))
            return;
        var deleted = await Busy.During(sender, () => Runner.Run(Pane, "prune", () => Backup.Prune(root, delete: true)));
        if (deleted == null) return;
        ResultBar.Severity = InfoBarSeverity.Success;
        ResultBar.Message = $"{deleted.Count} ref(s) deleted from the backup.";
        ResultBar.IsOpen = true;
        await LoadAsync();
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (Host?.Window is MainWindow main) main.ShowSettings();
        else Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
