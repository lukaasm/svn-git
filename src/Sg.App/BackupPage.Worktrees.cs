using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

public sealed partial class BackupPage
{
    void RenderWorktrees(IEnumerable<BackupWorktree> worktrees)
    {
        foreach (var worktree in worktrees)
        {
            // The overview's card, with the backup's state in the badge: the same title, the same chips of
            // glyph and colour with the words on hover, and one button of the same width on the right.
            var status = WorktreeStatus(worktree);
            var help = (worktree.Excluded ? "Excluded from the backup: its branch, its uncommitted changes and its shelves stay on this machine.\n" : "")
                + status.Help + (worktree.LastConfirmed is { } when ? $"\nConfirmed here {when.LocalDateTime:g}." : "");
            var header = new WorktreeTitle
            {
                Text = worktree.Name, Path = worktree.Path ?? "", BadgeText = status.Text, BadgeTip = help,
                BadgeSeverity = worktree.Excluded ? ChipSeverity.Neutral : status.Severity,
                // A copy with no folder here has the cloud where the folder is, which says it already.
                BadgeVisibility = worktree.Path == null ? Visibility.Collapsed : Visibility.Visible,
            };
            var card = new SettingsExpander { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(card, "BackupWorktree_" + worktree.Name);
            AutomationProperties.SetName(card, worktree.Name);
            // The card's button opens the worktree's backup page, named for what it is there for: a copy
            // with nothing here is restored; anything else is managed - backed up, compared, restored.
            var open = worktree.Path == null
                ? WorktreeButton("Restore…", "\uE896", "BackupWorktreeOpen_" + worktree.Name,
                    () => { OpenWorktree(worktree.Name); return Task.CompletedTask; }, help: "Choose a checkout and a branch name to restore " + worktree.Name + " here.")
                : WorktreeButton("Manage", "\uE713", "BackupWorktreeOpen_" + worktree.Name,
                    () => { OpenWorktree(worktree.Name); return Task.CompletedTask; }, help: "Open backup and restore options for " + worktree.Name);
            open.MinWidth = (double)Application.Current.Resources["CardActionWidth"];
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (worktree.Remote?.HasReview == true)
            {
                var review = new StatusChip { Glyph = "\uE90A", Severity = ChipSeverity.Neutral };
                AutomationProperties.SetName(review, "Code review");
                TaskGate.SetHelp(review, "The backup holds this worktree's code review comments and their context.");
                right.Children.Add(review);
            }
            right.Children.Add(open);
            card.Content = right;
            var populated = false;
            void Populate()
            {
                if (populated) return;
                populated = true;
                var backup = WorktreeButton("Back up", "\uE74E", "BackupWorktreeSend_" + worktree.Name,
                    () => BackUpAsync(worktree.Name), worktree.CanBackUp,
                    worktree.Excluded ? "Include this worktree in its backup settings first." : worktree.Path == null
                        ? "Restore this remote copy to a local worktree before backing it up." : "Back up only " + worktree.Name);
                card.Items.Add(new SettingsCard
                {
                    Header = "Local worktree", Description = worktree.Path ?? "No local worktree folder",
                    HeaderIcon = new FontIcon { Glyph = "\uE8B7" }, Content = new TaskGate { Content = backup },
                });
                var restore = WorktreeButton("Restore…", "\uE896", "BackupWorktreeRestore_" + worktree.Name,
                    () => { OpenWorktree(worktree.Name); return Task.CompletedTask; }, worktree.Remote != null,
                    worktree.Remote == null ? "Back up this worktree to create its first remote copy." : "Choose a checkout, branch name, saved edits, and replacement options for " + worktree.Name);
                card.Items.Add(new SettingsCard
                {
                    Header = "Remote backup", Description = worktree.Remote == null ? "No remote copy yet"
                        : (worktree.Remote.HasWip ? "Contains commits and saved edits." : "Contains commits.")
                            + (worktree.Remote.HasReview ? " Code review comments and context are included." : "")
                            + (worktree.Remote.HasAppearance ? " Checkout appearance is included." : "") + " Open to review their version.",
                    HeaderIcon = new FontIcon { Glyph = "\uE753" }, Content = restore,
                });
                var catalog = _catalog;
                var compare = WorktreeButton("Compare…", "\uE8A5", "BackupWorktreeCompare_" + worktree.Name,
                    () => { Go(() => new BackupComparePage(worktree.Name, catalog), "backup-compare:" + worktree.Name); return Task.CompletedTask; },
                    worktree.Remote != null, "Compare local and saved commit histories; read a patch only when selected.");
                card.Items.Add(new SettingsCard
                {
                    Header = "Compare histories", Description = "Review local and saved commits side by side, with their SVN bases and patches.",
                    HeaderIcon = new FontIcon { Glyph = "\uE8A5" }, Content = compare,
                });
                var prune = WorktreeButton("Prune…", "\uE74D", "BackupWorktreePrune_" + worktree.Name,
                    () => PruneAsync(worktree.Name), help: "Preview stale refs belonging only to " + worktree.Name);
                card.Items.Add(new SettingsCard
                {
                    Header = "Prune this worktree's backup", Description = "Review stale commits, saved edits, shelves, and metadata before deleting them from the remote.",
                    HeaderIcon = new FontIcon { Glyph = "\uE74D" }, Content = new TaskGate { Content = prune },
                });
            }
            card.RegisterPropertyChangedCallback(SettingsExpander.IsExpandedProperty, (_, _) =>
            {
                // Remember immediately, before the expansion animation finishes or search rebuilds the cards.
                if (!Branches.Children.Contains(card)) return;
                if (card.IsExpanded) { _expanded.Add(worktree.Name); Populate(); }
                else _expanded.Remove(worktree.Name);
            });
            if (_expanded.Contains(worktree.Name)) { Populate(); card.IsExpanded = true; }
            Branches.Children.Add(card);
        }
    }

    /// <summary>
    /// The badge after the name, in the overview's colours for the same facts: red when nothing of the
    /// worktree went, green when the backup holds its tip.
    /// </summary>
    static (string Text, ChipSeverity Severity, string Help) WorktreeStatus(BackupWorktree worktree) =>
        worktree.Remote == null ? ("Local only", ChipSeverity.Critical, "No remote backup was found for this worktree.")
        : worktree.Path == null ? ("Remote only", ChipSeverity.Neutral, "This backup has no local worktree folder. Restore makes one.")
        : worktree.CommitStatus switch
        {
            BackupCommitStatus.Saved => ("Commits saved", ChipSeverity.Success, "The remote matches this worktree's committed tip. Working files and shelves have not been compared."),
            BackupCommitStatus.LocalDiffers => ("Local commits differ", ChipSeverity.Attention, "Local commit IDs differ from the saved version. A restore or replay can change IDs without changing the work. Back up compares the changes before sending."),
            _ => ("Compare versions", ChipSeverity.Caution, "This remote version has not been confirmed against the local tip. Manage opens its saved commits; a backup checks how the versions relate."),
        };

    static IconButton WorktreeButton(string label, string glyph, string id, Func<Task> run, bool enabled = true, string help = "")
    {
        var button = new IconButton { Text = label, Glyph = glyph, IsEnabled = enabled, MinWidth = 124 };
        AutomationProperties.SetAutomationId(button, id);
        TaskGate.SetHelp(button, help);
        button.Click += async (_, _) => await Busy.During(button, run);
        return button;
    }

    void OpenWorktree(string name, bool separate = false) => Go(() => new BackupPage(name, separate: separate), "backup:branch:" + name);

    async Task SuggestSeparateNameAsync()
    {
        if (_picked is not { Unreadable: null } entry || _hidden) return;
        var root = Session.Require();
        var name = await Task.Run(() =>
        {
            var candidate = entry.Name + "-backup";
            for (var n = 2; root.Git.RefSha("refs/heads/" + candidate) != null; n++) candidate = entry.Name + "-backup-" + n;
            return candidate;
        });
        if (_hidden || _picked != entry || NameBox.Text != entry.Name) return;
        ForceBox.IsChecked = false;
        NameBox.Text = name;
        NameBox.Focus(FocusState.Programmatic);
    }

    void ShowPageReport(SgRoot root)
    {
        var result = Backup.Last(root);
        if (!Overview && result is { Worktree: null } && Worktree != null)
        {
            // A full run can also contain the selected worktree's reconciliation actions.
            var items = result.Items.Where(i => i.Name == Worktree && i.Kind is "branch" or "wip").ToList();
            result = items.Count == 0 ? null : new BackupResult
            {
                Url = result.Url, When = result.When, Worktree = Worktree, Items = items,
            };
        }
        if (!Overview && result?.Worktree != Worktree) LastReport.Hide();
        else ShowReport(LastReport, result, ActFor);
        HistoryDetails.Visibility = LastReport.Visibility;
        if (result is { Ok: false } || result?.Behind > 0 || result?.Items.Any(i => i.LeftOut.Count > 0) == true)
            HistoryDetails.IsExpanded = true;
    }

    async Task BackUpAsync(string? worktree)
    {
        var root = Session.Require();
        ResultBar.IsOpen = false;
        HistoryDetails.Visibility = Visibility.Visible;
        HistoryDetails.IsExpanded = true;
        LastReport.Running(worktree == null ? "Backing up all worktrees…" : "Backing up " + worktree + "…", root.Config.Backup?.Url ?? "");
        var res = await Runner.Run(Pane, worktree == null ? "backup all worktrees" : "backup " + worktree, () => Backup.Run(root, worktree: worktree));
        ShowPageReport(root);
        if (res != null) await LoadAsync();
    }

    async void BackupAll_Click(object sender, RoutedEventArgs e) => await BackUpAsync(null);
    async void PruneAll_Click(object sender, RoutedEventArgs e) => await PruneAsync(null);

    async Task PruneAsync(string? worktree)
    {
        var root = Session.Require();
        using var read = _reads.Begin();
        var plan = await read.Run(Pane, () => Backup.PlanPrune(root, worktree));
        if (plan == null || !read.Current || _hidden) return;
        if (plan.Refs.Count == 0)
        {
            await Dialogs.Info(this, "Nothing to prune", (worktree == null ? "These backups have" : worktree + " has")
                + " no stale backup refs. Copies that still belong to local work are kept.");
            return;
        }
        var scope = worktree ?? "all worktrees";
        if (!await Dialogs.Confirm(this, "Prune backup · " + scope,
            $"Delete these {plan.Refs.Count} stale backup refs for {scope}:\n\n" + string.Join("\n", plan.Refs.Keys)
            + "\n\nLocal worktrees and files stay in place. Only the listed remote versions will be deleted.", "Delete listed backups")) return;
        var deleted = await Runner.Run(Pane, "prune backup " + scope, () => Backup.Prune(root, plan));
        if (deleted == null) return;
        ResultBar.Severity = InfoBarSeverity.Success;
        ResultBar.Message = $"{deleted.Count} stale backup refs deleted for {scope}.";
        ResultBar.IsOpen = true;
        await LoadAsync();
    }
}
