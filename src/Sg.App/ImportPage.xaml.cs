using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// An export file, read before it is put back. It says what branch is in it, what it was cut from, and
/// how far this checkout has moved since - and only then offers the button, because the answer to that
/// last question is what the import will have to merge across.
/// </summary>
public sealed partial class ImportPage : SgPage
{
    readonly OperationForm<ImportRequest> _form;
    readonly PageReads _reads = new();
    bool _hidden;
    string _file;
    ExportMeta? _meta;
    bool _binding;
    readonly BranchTargetValidation _targetValidation = new();

    /// <summary>A checkout here points at the URL the export names. When none does, saying so is the answer.</summary>
    bool _matched;

    /// <summary>The paused result owns its destination even if the form selection changes afterward.</summary>
    ImportResult? _waiting;

    public ImportPage(string file)
    {
        InitializeComponent();
        _form = new(NameBox, IntoBox, PickButton);
        _file = file;
        Title = "Import a branch";
        Session.Log.Sink = Pane;
        RevisionPreview.Changed += SyncButton;
        Unloaded += (_, _) => OnHidden();
    }

    public override void OnShown(bool returning) { _hidden = false; _ = LoadAsync(); }
    public override void OnHidden()
    {
        _hidden = true;
        _reads.Cancel();
        RevisionPreview.Clear();
        _targetValidation.Invalidate();
    }

    CheckoutConfig? Into => IntoBox.SelectedItem is string name ? Session.Root?.Checkout(name) : null;

    async Task LoadAsync()
    {
        if (_hidden) return;
        using var request = _reads.Begin();
        RevisionPreview.Clear();
        _targetValidation.Invalidate();
        ExistingDestination.Update(null, null);
        _meta = null;
        AppearancePreview.Hide();
        ImportButton.IsEnabled = false;
        var root = Session.Require();
        var file = _file;
        Subtitle = file;
        FilePath.Text = file;
        Unreadable.Visibility = Visibility.Collapsed;
        Filled.Visibility = Visibility.Collapsed;
        ImportReading.Show("Reading export and matching its checkout…");
        var read = await request.Run(Pane, () =>
        {
            var meta = Export.Read(file);
            var co = Export.MatchCheckout(root, meta);
            return new { Meta = meta, Co = co };
        });
        if (!request.Current || _hidden || root != Session.Root) return;
        ImportReading.Hide();
        if (read == null)
        {
            // The reason is in the strip; the page offers the only two things that still mean anything.
            _meta = null;
            Unreadable.Text = "The line at the bottom says why. Pick another file, or close this.";
            Unreadable.Visibility = Visibility.Visible;
            Filled.Visibility = Visibility.Collapsed;
            ImportButton.IsEnabled = false;
            return;
        }

        _meta = read.Meta;
        Unreadable.Visibility = Visibility.Collapsed;
        Filled.Visibility = Visibility.Visible;

        Headline.Text = $"{read.Meta.Branch}  -  {read.Meta.Commits} commit(s) from {read.Meta.Checkout}";
        Provenance.Text = $"made {read.Meta.Created.LocalDateTime:yyyy-MM-dd HH:mm} on {read.Meta.From}"
                          + (read.Meta.Sg.Length > 0 ? $", by sg {read.Meta.Sg}" : "");

        _binding = true;
        IntoBox.ItemsSource = root.Config.Checkouts.Select(c => c.Name).ToList();
        // Only the checkout whose URL matches. Nothing is picked when none does, because building the
        // branch on a tree from another repository would merge every patch into something enormous
        // rather than refuse, and the page would have offered to do it.
        IntoBox.SelectedItem = read.Co?.Name;
        NameBox.Text = read.Meta.Branch;
        _binding = false;
        _matched = read.Co != null;

        CommitsHeader.Text = read.Meta.Commits == 1 ? "Commit" : $"Commits ({read.Meta.Commits})";
        Subjects.ItemsSource = read.Meta.Subjects;
        ShowBases();
        SyncButton();
    }

    /// <summary>The base of the export beside the base here, one working copy per line.</summary>
    void ShowBases()
    {
        var meta = _meta;
        var co = Into;
        if (meta == null) return;
        AppearancePreview.Show(meta.HasAppearance, meta.AppearanceIcon, meta.Checkout, co);
        RevisionPreview.Show(Session.Require(), meta, co, Pane);
    }

    async void SyncButton()
    {
        if (_form == null || _form.Running || _hidden) return;
        var name = NameBox.Text.Trim();
        var root = Session.Root;
        ExistingDestination.Update(null, null);
        ImportButton.IsEnabled = false;
        if (name.Length > 0 && root != null && _meta != null)
            ExplainTarget("Checking branch name and destination…");
        var check = await _targetValidation.CheckAsync(_meta == null ? null : root, name);
        if (check == null) return;
        ExistingDestination.Update(root, check.Existing);
        var taken = check.Taken;
        ImportButton.IsEnabled = _meta != null && name.Length > 0 && Into != null && !taken && check.Error == null && RevisionPreview.Ready;
        var retry = Into is { } checkout && _form.IsRetry(new ImportRequest(_file, name, checkout.Name));
        ImportLabel.Text = retry ? "Retry import" : _meta == null ? "Import" : $"Import {_meta.Commits} commit(s)";
        ExplainTarget(_meta == null ? "Choose a readable export file to import."
            : Into == null && !_matched ? $"No checkout here points at {_meta.Root?.Url}. Pick one only if you know it is the same repository."
            : Into == null ? "Pick the checkout to build it on."
            : name.Length == 0 ? "Give the branch a name."
            : check.Error != null ? check.Error
            : !RevisionPreview.Ready ? RevisionPreview.Reason
            : taken ? $"{name} is already a branch here. Give it another name."
            : $"{name} will be made on {Into.Name}, and its worktree with it.");
    }

    void ExplainTarget(string message)
    {
        Summary.Text = message;
        TaskGate.SetHelp(ImportButton, message);
        AutomationProperties.SetHelpText(NameBox, message);
    }

    void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_binding) SyncButton();
    }

    void Into_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        ShowBases();
        SyncButton();
    }

    async void Pick_Click(object sender, RoutedEventArgs e)
    {
        var picked = await WindowHelper.PickOpenFile(this, Export.Extension);
        if (picked == null) return;
        _file = picked;
        ResultBar.IsOpen = false;
        await LoadAsync();
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_form.Running || !RevisionPreview.Ready) return;
        var meta = _meta;
        var co = Into;
        if (meta == null || co == null) return;
        var name = NameBox.Text.Trim();
        var root = Session.Require();

        var request = new ImportRequest(_file, name, co.Name);

        // The worktree is the long part: it is a checkout of the whole tree, the same as sg branch.
        if (!await Dialogs.Confirm(this, "Import " + name,
                $"Make the branch {name} on {co.Name}, and a worktree folder for it at {root.WorktreePathFor(name)}?\n\n"
                + $"{meta.Commits} commit(s) are replayed onto the snapshot this checkout has now. Nothing goes to SVN."
                + (meta.HasAppearance ? "\n\n" + CheckoutIcons.RestoreDescription(true, co) : ""),
                "Import"))
            return;

        _targetValidation.Invalidate();
        ExistingDestination.Update(null, null);
        ResultBar.IsOpen = false;
        var res = await _form.Run(request, submitted => Runner.Run(Pane, "import " + submitted.Name,
            () => Export.Import(root, submitted.File, submitted.Name, submitted.Checkout),
            worktree: new(submitted.Checkout, submitted.Name, root.WorktreePathFor(submitted.Name))), sender);
        _targetValidation.Invalidate(); // A late name check must not replace the operation result.
        if (res == null) { SyncButton(); return; }

        var outcome = TaskResults.Describe(res);
        ResultBar.Severity = outcome.State == TaskState.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = outcome.Detail;
        ResultBar.IsOpen = true;
        AppearancePreview.ShowResult(res.CheckoutAppearanceRestored, res.CheckoutAppearanceWarning);

        ConflictsHeader.Visibility = ConflictsCard.Visibility = res.Conflicted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Conflicts.ItemsSource = res.Conflicted;

        // A patch that would not merge does not cost the rest of them any more: the import stays where
        // it stopped, and one button away is the page that finishes it.
        _waiting = res.Waiting ? res : null;
        ResolveButton.Visibility = res.Waiting ? Visibility.Visible : Visibility.Collapsed;

        // The branch exists now, so this page has nothing left to offer about this file.
        ImportButton.IsEnabled = false;
        ExplainTarget(outcome.State == TaskState.Succeeded ? "Done. Close this and the worktree is on the checkout's card."
            : res.Waiting ? "Resume the operation to finish, skip a commit, or cancel."
            : "Review the result above for the reason the import stopped.");
    }

    /// <summary>The page that finishes a stopped import. It is the same one a stopped rebase opens.</summary>
    void Resolve_Click(object sender, RoutedEventArgs e)
    {
        var wt = _waiting;
        if (wt == null) return;
        Go(() => new ConflictPage(wt.Path) { Checkout = wt.Checkout, Branch = wt.Branch }, "resolve:" + wt.Path);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
