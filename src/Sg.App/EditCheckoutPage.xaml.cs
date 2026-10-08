using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>One external of a checkout, as the edit page lists it.</summary>
public sealed class ExternalRow
{
    public string Rel { get; set; } = "";
    public string Url { get; set; } = "";
    public string Declared { get; set; } = "";
    public bool Switched { get; set; }
    public long Revision { get; set; }
    /// <summary>What the server calls where it is: r266, or the short commit.</summary>
    public string Label { get; set; } = "";
    /// <summary>A git submodule, whose declared place is the commit its parent pins.</summary>
    public bool Git { get; set; }

    public Visibility SwitchedVisibility => Switched ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The chip's word: an external is switched, a submodule is on a branch of its own.</summary>
    public string ChipText => Git ? "on a branch" : "switched";

    /// <summary>What a screen reader announces for the row: the list item's name is its item's text.</summary>
    public override string ToString() => Switched ? $"{Rel}, {(Git ? "on a branch" : "switched")}: {Url}" : $"{Rel}: {Url}";

    public string Tip => (Switched, Git) switch
    {
        (true, true) => $"{Rel}\nnow: {Url}  {Label}\nthe parent pins a commit of {Declared}\nOn a branch here only. Nobody else sees it.",
        (true, false) => $"{Rel}\nnow: {Url}  {Label}\nsvn:externals declares: {Declared}\nSwitched here only. Nobody else sees it.",
        (false, true) => $"{Rel}\n{Url}  {Label}, the commit the parent pins",
        _ => $"{Rel}\n{Url}  {Label}",
    };
}

/// <summary>
/// What a checkout is called, which folders sg leaves alone, and where each external points. The
/// externals half is a working copy state and not a commit: switching one is svn switch on that
/// external's own directory, which is how a checkout runs against another branch of one repository
/// without changing it for anyone. A git clone's submodules take the same place: one is put on a branch
/// of its own repository, or back on the commit its parent pins.
/// </summary>
public sealed partial class EditCheckoutPage : SgPage
{
    string _name;

    /// <summary>Something changed that the overview has to read again.</summary>
    public bool Changed { get; private set; }

    public EditCheckoutPage(CheckoutConfig co)
    {
        InitializeComponent();
        IconCard.Resources["SettingsCardHeaderIconMaxSize"] = CheckoutIcons.Size;
        _name = co.Name;
        Session.Log.Sink = Pane;
        Title = "Edit checkout";
        Checkout = co.Name;
        Subtitle = co.Path;
        NameBox.Text = co.Name;
        ShowIcon(co);
        SkipPick.Paths = co.Skip;
        JunctionPick.Paths = co.Junctions;
        OptionalPick.Paths = co.Optional;
        SharedBox.Mode = co.Shared;
        SkipPick.Changed += ShowFolders;
        JunctionPick.Changed += ShowFolders;
        OptionalPick.Changed += ShowFolders;
        SharedBox.Changed += () => { _cloneFellBack = false; ShowFolders(); };
        FoldersSection.Expanded += (_, _) => ShowFolders();
        FoldersSection.Collapsed += (_, _) => ShowFolders();
        _ = DetectSharingAsync(co.Path, Session.Root?.Config.WorktreeRoot ?? Session.Root?.RootPath);
        ShowFolders();
        var path = co.Path;
        _ = PathPicker.OfferFoldersOf(path, SkipPick, JunctionPick, OptionalPick);
        _git = co.IsGit;
        if (co.IsGit) SayGit();
        RowMenu.Attach(Externals, source => RowUnder(source) is { } row ? ExternalMenu(row) : null);
        _ = LoadExternalsAsync();
    }

    /// <summary>The saved choice was a clone and the volumes said it cannot be made, so the box went back to a junction.</summary>
    bool _cloneFellBack;

    /// <summary>
    /// The closed Folders section's description: what is set, in the words the add page uses, and what the
    /// volumes said about cloning. A saved clone that fell back to a junction is said even while it is open:
    /// pressing Save then writes the junction, and the box alone only shows where it landed.
    /// </summary>
    void ShowFolders()
    {
        var text = CheckoutFields.FoldersText(SkipPick.Paths.Count, JunctionPick.Paths.Count, SharedBox.Mode, OptionalPick.Paths.Count);
        var note = SharedBox.Note;
        if (_cloneFellBack) note = "The saved ReFS clone cannot be made here, so new worktrees would get junctions." + (note != null ? " " + note : "");
        if (note != null && (_cloneFellBack || !FoldersSection.IsExpanded)) text += "\n" + note;
        FoldersSection.Description = text;
    }

    async Task DetectSharingAsync(string checkoutPath, string? worktreeRoot)
    {
        var wasClone = SharedBox.Mode == SharedMode.Clone;
        var detecting = SharedBox.DetectAsync(checkoutPath, worktreeRoot);
        ShowFolders();
        await detecting;
        if (wasClone && SharedBox.Mode != SharedMode.Clone) _cloneFellBack = true;
        ShowFolders();
    }

    /// <summary>A git clone, whose externals are its submodules: the menu's words follow.</summary>
    bool _git;

    /// <summary>The external a right click or the menu key landed on, from any element inside its row.</summary>
    static ExternalRow? RowUnder(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { DataContext: ExternalRow row }) return row;
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>
    /// The menu on an external's row. Back to declared acts on one row, so it is on that row rather than a
    /// button under the list; it is offered on a switched row only, and says why when it is greyed, because
    /// a disabled menu item shows no tooltip.
    /// </summary>
    MenuFlyout ExternalMenu(ExternalRow row)
    {
        // The branch box and Switch follow the selection, so the row the menu is on becomes the selected one.
        if (!ReferenceEquals(Externals.SelectedItem, row)) Externals.SelectedItem = row;
        var can = row is { Switched: true, Declared.Length: > 0 };
        var label = _git ? "Back to the pin" : "Back to declared";
        var revert = new MenuFlyoutItem
        {
            Text = can ? label : label + (!row.Switched ? (_git ? " (already on it)" : " (not switched)") : " (nothing declared)"),
            Icon = new FontIcon { Glyph = "" },
            IsEnabled = can,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(revert, "RevertButton");
        ToolTipService.SetToolTip(revert, _git
            ? "Check out the commit the parent pins again, which is what everyone else has."
            : "Switch it back to the URL svn:externals declares, which is what everyone else has.");
        revert.Click += Revert_Click;
        return new MenuFlyout { Items = { revert } };
    }

    /// <summary>The section's words for a git clone, whose externals are its submodules.</summary>
    void SayGit()
    {
        ExternalsHeader.Text = "Submodules";
        ExternalsIntro.Text = "Put a submodule on a branch of its own repository, on this machine only. This is git checkout in that submodule: "
                              + "the commit its parent pins is left alone, so nothing is committed and nobody else sees it. Commits made in it go to that branch. "
                              + "Sync afterwards and the snapshot records the branch's content.";
        NoExternals.Title = "No submodules";
        NoExternals.Text = "This clone has no submodules checked out. There is nothing to point elsewhere.";
        BranchBox.Header = "Put the selected submodule on";
        BranchBox.PlaceholderText = "pick a branch";
        ToolTipService.SetToolTip(BranchBox, "The branches of this submodule's repository, read from the server.");
        ToolTipService.SetToolTip(SwitchButton, "Checks the branch out in this submodule only, following its remote branch. The parent's pin and the server are left alone.");
    }

    void ShowIcon(CheckoutConfig co)
    {
        IconCard.HeaderIcon = CheckoutIcons.Create(co);
        ResetIconButton.IsEnabled = !string.IsNullOrEmpty(co.Icon);
        TaskGate.SetHelp(ResetIconButton, string.IsNullOrEmpty(co.Icon) ? "This checkout already uses initials." : "Restore the checkout initials.");
    }

    async void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Root;
        var co = CheckoutConfig();
        if (root == null || co == null) return;
        try
        {
            var path = await WindowHelper.PickOpenFile(this, ".png", ".ico", ".jpg", ".jpeg", ".bmp", ".gif");
            if (path == null || Session.Root != root) return;
            await Busy.During(ChooseIconButton, async () =>
            {
                var png = await Task.Run(() => CheckoutImage.ReadAsync(path));
                await SaveIcon(root, co, png);
            });
        }
        catch (Exception ex)
        {
            if (XamlRoot != null) await Dialogs.Info(this, "Could not use this image", ex.Message);
            else Runner.ReadError(Pane, ex);
        }
    }

    async void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Root is { } root && CheckoutConfig() is { } co)
            await Busy.During(ResetIconButton, () => SaveIcon(root, co, null), restoreEnabled: false);
    }

    async Task SaveIcon(SgRoot root, CheckoutConfig co, byte[]? png)
    {
        if (Session.Root != root) return;
        var ok = await Runner.Run(Pane, "change checkout icon", () => CheckoutAppearance.SetIcon(root, co.Name, png));
        if (Session.Root != root) return;
        ShowIcon(co);
        if (!ok) return;
        Changed = true;
        Result.Text = png == null ? "Checkout initials restored" : "Checkout image saved";
        if (WindowHelper.WindowOf(this) is MainWindow window) window.RefreshCheckoutIcons();
    }

    CheckoutConfig? CheckoutConfig()
    {
        // Only a saved rename changes the target; text in the name box may name another checkout.
        var root = Session.Root;
        return root?.Config.Checkouts.FirstOrDefault(c => c.Name.Equals(_name, StringComparison.OrdinalIgnoreCase));
    }

    async Task LoadExternalsAsync()
    {
        var root = Session.Root;
        var co = CheckoutConfig();
        if (root == null || co == null) return;
        NoExternals.Visibility = Visibility.Collapsed;
        ExternalsReading.Show($"Reading checkout {ServerWords.Externals(co)}…", Externals.ItemsSource == null);
        var list = await Runner.Quiet(Pane, () => Ops.ExternalsOf(root, co));
        ExternalsReading.Hide();
        if (list == null) return;
        Externals.ItemsSource = list.Select(e => new ExternalRow
        {
            Rel = e.Rel,
            Url = e.Url,
            Declared = e.Declared,
            Switched = e.Switched,
            Revision = e.Revision,
            Label = e.Label,
            Git = co.IsGit,
        }).ToList();
        NoExternals.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Result.Text = list.Count == 0
            ? ""
            : co.IsGit
                ? $"{list.Count} submodule(s), {list.Count(e => e.Switched)} on a branch here"
                : $"{list.Count} external(s), {list.Count(e => e.Switched)} switched here";
    }

    /// <summary>The branch names offered for the external in hand, so a switch is a pick and not a URL.</summary>
    List<string> _branches = new();
    string _branchesFor = "";

    async void External_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = Externals.SelectedItem as ExternalRow;
        BranchBox.IsEnabled = row != null;
        SwitchButton.IsEnabled = row != null;
        BranchBox.ItemsSource = null;
        BranchBox.Text = row?.Url ?? "";
        if (row == null) return;

        // Reading the branch list is a call to the server, so the box works as a URL field meanwhile.
        var root = Session.Root;
        if (root == null || CheckoutConfig() is not { } co) return;
        var url = row.Url;
        if (_branchesFor != url)
        {
            var names = await Runner.Quiet(Pane, () => Ops.BranchNames(root, co, url));
            if (Externals.SelectedItem != row) return;   // the user moved on while the server answered
            _branches = names ?? new List<string>();
            _branchesFor = url;
        }
        BranchBox.ItemsSource = _branches;
        BranchBox.Text = url;
    }

    /// <summary>Picking a branch name turns into the URL that branch has, by the rule server branches use.</summary>
    void Branch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var root = Session.Root;
        if (root == null || BranchBox.SelectedItem is not string branch) return;
        if (Externals.SelectedItem is not ExternalRow row || CheckoutConfig() is not { } co) return;
        try { BranchBox.Text = Ops.UrlForBranch(root, co, row.Url, branch); }
        catch (SgException) { /* a repository that follows neither shape: the typed URL still works */ }
    }

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Root;
        var co = CheckoutConfig();
        if (root == null || co == null) return;
        var edit = new Ops.CheckoutEdit(
            NameBox.Text.Trim(),
            SkipPick.Paths,
            JunctionPick.Paths,
            OptionalPick.Paths,
            Shared: SharedBox.Mode);

        var ok = await Busy.During(SaveButton, () => Runner.Run(Pane, "edit " + co.Name, () => Ops.UpdateCheckout(root, co, edit)));
        if (!ok) return;
        Changed = true;
        _name = co.Name;
        Checkout = NameBox.Text.Trim();
        // Shared folders are always skipped too, so show what was actually written rather than what was typed.
        var saved = CheckoutConfig();
        if (saved != null) { SkipPick.Paths = saved.Skip; ShowIcon(saved); }
        Result.Text = "saved";
    }

    async void Switch_Click(object sender, RoutedEventArgs e) => await SwitchTo(BranchBox.Text.Trim());

    async void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (Externals.SelectedItem is ExternalRow row) await SwitchTo(row.Declared);
    }

    async Task SwitchTo(string url)
    {
        var root = Session.Root;
        var co = CheckoutConfig();
        if (root == null || co == null || Externals.SelectedItem is not ExternalRow row) return;
        if (url.Length == 0 || url.TrimEnd('/').Equals(row.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return;

        var text = !co.IsGit
            ? $"Point {row.Rel} at:\n{url}\n\nThis machine only. svn:externals is left alone, so nothing is committed and nobody else sees it. "
              + "Sync afterwards so the snapshot records the switched content. Sync keeps the switch: "
              + "svn update pulls an external back to the declared URL, and sync points it away again."
            : url.Contains('#')
                ? $"Put {row.Rel} on:\n{url}\n\nThis machine only. The commit its parent pins is left alone, so nothing is committed and nobody else sees it. "
                  + "Sync follows the branch from now on, and commits made in the submodule go to it."
                : $"Put {row.Rel} back on the commit its parent pins.\n\nSync keeps it there from now on, and commits made in it are pinned by the parent.";
        if (!await Dialogs.Confirm(this, (co.IsGit ? "Move " : "Switch ") + row.Rel, text, "Switch"))
            return;

        var rel = row.Rel;
        var r = await Busy.During(SwitchButton, () => Runner.Run(Pane, "switch " + rel, () => Ops.SwitchExternal(root, co, rel, url)));
        if (r == null) return;
        Changed = true;
        Result.Text = $"{rel} is now at {url}";
        await LoadExternalsAsync();
    }
}
