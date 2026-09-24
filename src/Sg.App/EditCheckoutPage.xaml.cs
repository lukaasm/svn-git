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

    public Visibility SwitchedVisibility => Switched ? Visibility.Visible : Visibility.Collapsed;

    public string Tip => Switched
        ? $"{Rel}\nnow: {Url}  r{Revision}\nsvn:externals declares: {Declared}\nSwitched here only. Nobody else sees it."
        : $"{Rel}\n{Url}  r{Revision}";
}

/// <summary>
/// What a checkout is called, which folders sg leaves alone, and where each external points. The
/// externals half is a working copy state and not a commit: switching one is svn switch on that
/// external's own directory, which is how a checkout runs against another branch of one repository
/// without changing it for anyone.
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
        _ = SharedBox.DetectAsync(co.Path, Session.Root?.Config.WorktreeRoot ?? Session.Root?.RootPath);
        var path = co.Path;
        _ = PathPicker.OfferFoldersOf(path, SkipPick, JunctionPick, OptionalPick);
        if (co.IsGit)
        {
            // A git clone has no externals to point anywhere: the section would only ever say so.
            ExternalsHeader.Visibility = ExternalsIntro.Visibility = ExternalsCard.Visibility = Visibility.Collapsed;
            return;
        }
        _ = LoadExternalsAsync();
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
        ExternalsReading.Show("Reading checkout externals…", Externals.ItemsSource == null);
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
        }).ToList();
        NoExternals.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Result.Text = list.Count == 0
            ? ""
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
        RevertButton.IsEnabled = row is { Switched: true, Declared.Length: > 0 };
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

        if (!await Dialogs.Confirm(this, "Switch " + row.Rel,
                $"Point {row.Rel} at:\n{url}\n\nThis machine only. svn:externals is left alone, so nothing is committed and nobody else sees it. "
                + "Sync afterwards so the snapshot records the switched content. Sync keeps the switch: "
                + "svn update pulls an external back to the declared URL, and sync points it away again.", "Switch"))
            return;

        var rel = row.Rel;
        var r = await Busy.During(SwitchButton, () => Runner.Run(Pane, "switch " + rel, () => Ops.SwitchExternal(root, co, rel, url)));
        if (r == null) return;
        Changed = true;
        Result.Text = $"{rel} is now at {url}";
        await LoadExternalsAsync();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
