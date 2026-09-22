using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
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
    public string Note => Entry.Unreadable != null ? "" : Entry.Excluded ? "excluded here" : Entry.ExistsHere ? "here" : Entry.Drift.Count > 0 ? "checkout moved on" : Entry.Checkout.Length == 0 ? "no checkout matches" : "";

    public Brush NoteBrush => (Brush)Application.Current.Resources[
        Entry.ExistsHere || Entry.Excluded || Entry.Unreadable != null ? "TextFillColorTertiaryBrush" : Note.Length > 0 ? "StatusModifiedBrush" : "TextFillColorSecondaryBrush"];

    public string Tip => Entry.Unreadable
        ?? (Entry.Excluded ? "The worktree is excluded from the backup on this machine, so this is what it sent before, or another machine's copy. No backup writes over it; Prune lists it.\n" : "")
         + (Entry.Url.Length > 0 ? Entry.Url + " r" + Entry.Revision : Entry.Name);
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
    int _nameCheck;

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
            LastReport.Hide();
            Subtitle = "no backup repository";
            Summary.Text = "Set the URL in Settings first.";
            return;
        }
        Subtitle = cfg!.Url;
        // The URL is under the page title already. The card says only what the title does not: the prefix, when
        // there is one, and the worktrees left out, so a branch missing from the list is not read as lost.
        var notes = new List<string>();
        if (cfg.Prefix.Length > 0) notes.Add("everything under " + cfg.Prefix + "/");
        if (cfg.Excluded.Count > 0) notes.Add("left out here: " + string.Join(", ", cfg.Excluded));
        UrlText.Text = string.Join(". ", notes);
        UrlText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Before the remote is read: how the last run went is known here, and is worth seeing even when the remote cannot be reached now.
        ShowReport(LastReport, Backup.Last(root), ActFor);

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

    async void SyncButton()
    {
        var entry = _picked;
        var name = NameBox.Text.Trim();
        var generation = ++_nameCheck;
        var root = Session.Root;
        RestoreButton.IsEnabled = false;
        BranchTarget check = new(false, null);
        if (name.Length > 0 && root != null && _picked != null)
        {
            ExplainTarget("Checking branch name and destination…");
            await Task.Delay(200);
            if (generation != _nameCheck || root != Session.Root) return;
            check = await Task.Run(() => BranchTarget.Check(root, name));
            if (generation != _nameCheck || root != Session.Root) return;
        }
        var taken = check.Taken;
        var force = ForceBox.IsChecked == true;
        RestoreButton.IsEnabled = entry != null && entry.Unreadable == null && name.Length > 0 && Into != null && (!taken || force) && check.Error == null;
        RestoreLabel.Text = entry == null ? "Restore" : force && taken ? $"Overwrite with {entry.Commits} commit(s)" : $"Restore {entry.Commits} commit(s)";
        ExplainTarget(entry == null ? "Pick a branch."
            : entry.Unreadable != null ? entry.Unreadable
            : Into == null && entry.Checkout.Length == 0 ? $"No checkout here points at {entry.Url}. Pick one only if you know it is the same repository."
            : Into == null ? "Pick the checkout to build it on."
            : name.Length == 0 ? "Give the branch a name."
            : check.Error != null ? check.Error
            : taken && !force ? $"{name} is already a branch here. Choose another name, or select 'Replace if it exists here' to overwrite it."
            : taken ? $"{name} here is written over with the backup version. Its worktree is reset and any uncommitted changes in it are dropped."
            : $"{name} will be made on {Into.Name}, and its worktree with it.");
    }

    void ExplainTarget(string message)
    {
        Summary.Text = message;
        AutomationProperties.SetHelpText(RestoreButton, message);
        AutomationProperties.SetHelpText(NameBox, message);
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
                + $"The existing branch and its entire worktree are kept under a recovery name before the backup is restored. "
                + $"{entry.Commits} commit(s) are merged onto the snapshot this checkout has now" + (wip ? ", and the uncommitted changes come back after them" : "")
                + ". Nothing goes to SVN.", "Overwrite")
            : await Dialogs.Confirm(this, "Restore " + name,
                $"Make the branch {name} on {co.Name}, and a worktree folder for it at {root.WorktreePathFor(name)}?\n\n"
                + $"{entry.Commits} commit(s) are merged onto the snapshot this checkout has now"
                + (wip ? ", and the uncommitted changes come back after them" : "") + ". Nothing goes to SVN.", "Restore");
        if (!confirmed) return;

        var res = await Busy.During(sender, () => Runner.Run(Pane, "restore " + name,
            () => Backup.Restore(root, entry.Name, name == entry.Name ? null : name, co.Name, wip, force), worktree: new(co.Name, name, root.WorktreePathFor(name))), restoreEnabled: false);
        ++_nameCheck; // A late name check must not replace the operation result.
        if (res == null) { SyncButton(); return; }

        var lines = new List<string>();
        lines.Add(res.Ok
            ? $"{res.Branch} is here: {res.Applied} commit(s) in {res.Path}." + (res.Replaced ? $" Original work is preserved as {res.RecoveryBranch}" + (res.RecoveryPath == null ? "." : $" in {res.RecoveryPath}.") : "") + (res.Relinked ? " The store still had them, so nothing was replayed." : "")
              + (res.Drift.Count == 0 ? "" : $" They were merged across {res.Drift.Count} revision(s) that had moved on.")
            : $"{res.Applied} of {res.Commits} commit(s) applied. Paused on \"{res.Stopped}\"; the remaining commits are queued."
              + (res.Why == null ? "" : "\n" + res.Why.Split('\n')[0]));
        if (res.WipShelf != null)
            lines.Add(res.WipWritten
                ? "The uncommitted changes are written into the worktree" + (res.WipConflicted.Count > 0 ? $", {res.WipConflicted.Count} of them with conflict markers." : ".")
                : $"The uncommitted changes wait on the shelf as {res.WipShelf}: {res.WipConflicted.Count} file(s) would not merge.");
        if (res.Shelves.Count > 0) lines.Add("Shelves made again: " + string.Join(", ", res.Shelves) + ".");
        ResultBar.Severity = res.Ok && res.WipConflicted.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = string.Join("\n", lines);
        ResultBar.IsOpen = true;
        SetResumeAction(res);

        var conflicts = res.Conflicted.Concat(res.WipConflicted).ToList();
        ConflictsHeader.Visibility = ConflictsCard.Visibility = conflicts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Conflicts.ItemsSource = conflicts;

        entry.ExistsHere = true;
        RestoreButton.IsEnabled = false;
        Summary.Text = res.Ok ? "Done. Close this and the worktree is on the checkout's card." : "Resume the operation to finish, skip a commit, or cancel.";
    }

    async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Require();
        ResultBar.IsOpen = false;
        LastReport.Running("Backing up...", root.Config.Backup?.Url ?? "");
        var res = await Busy.During(sender, () => Runner.Run(Pane, "backup", () => Backup.Run(root)));
        // The run kept its result, a failed one too, so the report reads it back rather than being handed it.
        ShowReport(LastReport, Backup.Last(root), ActFor);
        if (res == null) return;
        foreach (var i in res.Items.Where(i => i.Failed || i.Rejected)) Pane.Append($"{i.Kind} {i.Name}: {i.Why}");
        await LoadAsync();
    }

    /// <summary>
    /// A backup as a report: a chip per outcome with its count, and a row for every item that did not simply
    /// find the remote already holding it - sent, left files out, failed, diverged, or newer over there.
    /// </summary>
    /// <summary>
    /// The buttons a row of the report gets: pull a copy that is newer; and for one that differs, pull
    /// the remote's commits on top of this branch or keep this machine's over it. Both, because which is
    /// right is the reader's call, and the row is where the reason is.
    /// </summary>
    RowActions? ActFor(BackupItem item) =>
        item.Behind ? new RowActions("Get changes", () => PullAsync(item))
        : item.Rejected && item.Kind is "branch" or "wip" ? new RowActions("Restore separately", () => RestoreSeparatelyAsync(item), "Keep this machine's", () => KeepAsync(item))
        : item.Rejected ? new RowActions("Keep this machine's", () => KeepAsync(item))
        : null;

    /// <summary>What a report row can do: one thing, or a first and a second.</summary>
    public sealed record RowActions(string Text, Func<Task> Run, string OtherText = "", Func<Task>? Other = null);

    async Task RestoreSeparatelyAsync(BackupItem item)
    {
        await LoadAsync();
        var entry = _entries.FirstOrDefault(e => e.Kind == "branch" && e.Name == item.Name);
        if (entry == null) return;
        Show(entry);
        ForceBox.IsChecked = false;
        var name = item.Name + "-backup";
        for (var n = 2; Session.Require().Git.RefSha("refs/heads/" + name) != null; n++) name = item.Name + "-backup-" + n;
        NameBox.Text = name;
        NameBox.Focus(FocusState.Programmatic);
    }

    void SetResumeAction(RestoreResult result)
    {
        ResultBar.ActionButton = null;
        if (!result.Waiting) return;
        var button = new Button { Content = "Resume operation" };
        button.Click += (_, _) => Go(() => new ConflictPage(result.Path) { Checkout = result.Checkout, Branch = result.Branch }, "resolve:" + result.Path);
        ResultBar.ActionButton = button;
    }

    /// <summary>What another machine sent, onto the branch here and into its folder, without making the worktree again.</summary>
    async Task PullAsync(BackupItem item)
    {
        var root = Session.Require();
        var r = await Runner.Run(Pane, "pull " + item.Name, () => Backup.Pull(root, item.Name));
        if (r == null) return;
        ResultBar.ActionButton = null;
        ResultBar.Severity = r.Ok && r.WipWhy == null && r.WipConflicted.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = PullSentence(r);
        SetResumeAction(r);
        ResultBar.IsOpen = true;
        // The report still reads "behind" until the next backup says otherwise, and the timer sends one soon.
        if (Host?.Window is MainWindow main) main.BackupSoon();
        await LoadAsync();
    }

    public static string PullSentence(RestoreResult r)
    {
        var parts = new List<string>();
        if (r.Branch.Length > 0)
            parts.Add(r.Commits == 0 ? $"{r.Branch} had every commit the backup holds." : $"{r.Applied} of {r.Commits} commit(s) pulled onto {r.Branch}.");
        if (r.WipAlreadyHere) parts.Add("The uncommitted changes in the backup were here already.");
        else if (r.WipShelf != null)
            parts.Add(r.WipWritten
                ? "The uncommitted changes are written into " + r.Path + (r.WipConflicted.Count > 0 ? $", {r.WipConflicted.Count} with conflict markers." : ".")
                : $"Uncommitted changes are saved on shelf {r.WipShelf}. Open Shelved changes to review and restore them."
                  + (r.WipWhy == null ? "" : " " + r.WipWhy));
        if (!r.Ok) parts.Add(r.Waiting
            ? $"Paused on \"{r.Stopped}\". The remaining commits are queued; resume to resolve, skip, or cancel."
            : $"\"{r.Stopped}\" could not be applied. {r.Why}");
        return string.Join(" ", parts);
    }

    /// <summary>This machine's copy over the one the remote holds, for this item alone, after saying what it writes over.</summary>
    async Task KeepAsync(BackupItem item)
    {
        if (!await Dialogs.Confirm(this, "Keep this machine's " + item.Name,
                $"The backup holds a different version of {item.Kind} {item.Name}:\n\n{item.Why}\n\n"
                + "Write this machine's over it? Only this one item is sent. The version it replaces stays readable in this store until the next backup fetches over it.",
                "Write over it"))
            return;
        var root = Session.Require();
        LastReport.Running("Backing up " + item.Name + "...");
        var res = await Runner.Run(Pane, "backup " + item.Name, () => Backup.Run(root, force: true, only: [item.Kind + "/" + item.Name]));
        ShowReport(LastReport, Backup.Last(root), ActFor);
        if (res != null) await LoadAsync();
    }

    public static void ShowReport(ReportCard card, BackupResult? res, Func<BackupItem, RowActions?>? act = null)
    {
        if (res == null)
        {
            card.Hide();
            return;
        }
        // When, in the quiet line under the sentence. The URL is not repeated: the page says it under its title.
        var when = res.When is { } t ? $"{t.LocalDateTime:yyyy-MM-dd HH:mm},{WorktreeRow.Ago(t)}" : "";
        if (res.Error != null)
        {
            card.Show(ChipSeverity.Critical, "", "The last backup could not run", res.Error + (when.Length > 0 ? "\n" + when : ""));
            return;
        }
        var failed = res.Items.Count(i => i.Failed);
        var leftOut = res.Items.Count(i => i.LeftOut.Count > 0);
        var bad = failed + res.Rejected;
        var severity = bad > 0 ? ChipSeverity.Critical : res.Behind + leftOut > 0 ? ChipSeverity.Caution : ChipSeverity.Success;
        var headline = res.Items.Count == 0 ? "The last backup found nothing to send"
            : bad == 0 ? "Backed up"
            : $"The last backup did not send {bad} of {res.Items.Count}";
        var detail = when + (res.RemoteOnly.Count > 0 ? $"   {res.RemoteOnly.Count} ref(s) there answer to nothing here: Prune" : "");
        ReportCount[] counts =
        [
            new(ChipSeverity.Success, "", res.Pushed, $"{res.Pushed} sent: the remote holds them now."),
            new(ChipSeverity.Neutral, "", res.Items.Count(i => i.State == "up to date"), "Already there: the remote held these as they are here, so nothing went."),
            new(ChipSeverity.Caution, "", leftOut, $"{leftOut} went without files too big for the backup. The files are named on the rows."),
            new(ChipSeverity.Caution, "", res.Behind, $"{res.Behind} newer on the remote than here. Restore brings them here."),
            new(ChipSeverity.Critical, "", res.Rejected, $"{res.Rejected} diverged: another machine has different work under the name."),
            new(ChipSeverity.Critical, "", failed, $"{failed} failed. The reason is on each row."),
        ];
        card.Show(severity, "", headline, detail, counts,
            res.Items.Where(i => i.State != "up to date" || i.LeftOut.Count > 0).Select(i => RowOf(i, act)));
    }

    static ReportRow RowOf(BackupItem i, Func<BackupItem, RowActions?>? act)
    {
        var row = RowOf(i);
        return act?.Invoke(i) is { } a
            ? new ReportRow(row.Severity, row.Glyph, row.Name, row.What, row.Detail, row.Tip) { ActionText = a.Text, Action = a.Run, OtherText = a.OtherText, Other = a.Other }
            : row;
    }

    static ReportRow RowOf(BackupItem i)
    {
        var kind = i.Kind switch { "branch" => "branch", "wip" => "uncommitted changes", "edits" => "local edits", _ => "shelf" };
        var (severity, glyph, state) = i.State switch
        {
            "failed" => (ChipSeverity.Critical, "", "failed"),
            "rejected" => (ChipSeverity.Critical, "", "diverged"),
            "behind" => (ChipSeverity.Caution, "", "newer on the remote"),
            "up to date" => (ChipSeverity.Caution, "", "already there, without some files"),
            "pushed" when i.LeftOut.Count > 0 => (ChipSeverity.Caution, "", "sent without some files"),
            "pushed" => (ChipSeverity.Success, "", i.Reconciled ? "sent over an older copy" : "sent"),
            _ => (ChipSeverity.Neutral, "", i.State),
        };
        var leftOut = i.LeftOut.Count > 0 ? "left out, too big: " + string.Join(", ", i.LeftOut) : "";
        var detail = i.Why ?? leftOut;
        var tip = string.Join("\n", new[] { i.RemoteRef, i.Why ?? "", leftOut }.Where(s => s.Length > 0));
        return new ReportRow(severity, glyph, i.Name, kind + ", " + state, detail, tip);
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
                $"{gone.Count} ref(s) on the backup answer to nothing this machine backs up any more - removed, dropped, or left out:\n\n" + string.Join("\n", gone.Take(20))
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
