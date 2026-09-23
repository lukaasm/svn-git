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
            var header = new StackPanel { Spacing = 4, MaxWidth = 460 };
            var name = new TextBlock { Text = worktree.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(name, worktree.Name);
            header.Children.Add(name);
            var status = WorktreeStatus(worktree);
            var badges = new WrapRow { Spacing = 8 };
            var chip = new StatusChip
            {
                Text = status.Text, Glyph = status.Glyph, Severity = status.Severity,
            };
            ToolTipService.SetToolTip(chip, status.Help);
            AutomationProperties.SetHelpText(chip, status.Help);
            badges.Children.Add(chip);
            if (worktree.Excluded) badges.Children.Add(new StatusChip { Text = "Excluded", Glyph = "\uE711", Severity = ChipSeverity.Caution });
            header.Children.Add(badges);
            if (worktree.LastConfirmed is { } when)
                header.Children.Add(new TextBlock { Text = $"Confirmed here {when.LocalDateTime:g}",
                    Style = (Style)Application.Current.Resources["Secondary"], TextWrapping = TextWrapping.Wrap });
            var card = new SettingsExpander { Header = header, HeaderIcon = new FontIcon { Glyph = "\uED25" },
                HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(card, "BackupWorktree_" + worktree.Name);
            AutomationProperties.SetName(card, worktree.Name);
            var open = WorktreeButton("Manage", "\uE713", "BackupWorktreeOpen_" + worktree.Name,
                () => { OpenWorktree(worktree.Name); return Task.CompletedTask; }, help: "Open backup and restore options for " + worktree.Name);
            card.Content = open;
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
                        : worktree.Remote.HasWip ? "Contains commits and saved edits. Open to review their version." : "Contains commits. Open to review their version.",
                    HeaderIcon = new FontIcon { Glyph = "\uE753" }, Content = restore,
                });
                var prune = WorktreeButton("Prune…", "\uE74D", "BackupWorktreePrune_" + worktree.Name,
                    () => PruneAsync(worktree.Name), help: "Preview stale refs belonging only to " + worktree.Name);
                card.Items.Add(new SettingsCard
                {
                    Header = "Prune this worktree's backup", Description = "Review stale branch, saved-edits, and shelf refs before deleting them from the remote.",
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

    static (string Text, string Glyph, ChipSeverity Severity, string Help) WorktreeStatus(BackupWorktree worktree) =>
        worktree.Remote == null ? ("Local only", "\uE8B7", ChipSeverity.Caution, "No remote backup was found for this worktree.")
        : worktree.Path == null ? ("Remote only", "\uE753", ChipSeverity.Neutral, "This backup has no local worktree folder. Manage opens restore options.")
        : worktree.CommitStatus switch
        {
            BackupCommitStatus.Saved => ("Commits saved", "\uE73E", ChipSeverity.Success, "The remote matches this worktree's committed tip. Working files and shelves have not been compared."),
            BackupCommitStatus.LocalDiffers => ("Local commits differ", "\uE70F", ChipSeverity.Attention, "Local commit IDs differ from the saved version. A restore or replay can change IDs without changing the work. Back up compares the changes before sending."),
            _ => ("Compare versions", "\uE8AB", ChipSeverity.Caution, "This remote version has not been confirmed against the local tip. Manage opens its saved commits; a backup checks how the versions relate."),
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
