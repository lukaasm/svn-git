using Microsoft.UI.Xaml;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// 'sg checkout add' for a root that is already open. Until this existed the GUI could register a
/// checkout only while creating a root, or by copying one on the server, so a working copy already
/// on disk could only be added from the command line and the pane looked like it held one thing.
/// </summary>
public sealed partial class AddCheckoutPage : SgPage
{
    /// <summary>True when a checkout was registered, so the overview knows to read the root again.</summary>
    public bool Added { get; private set; }

    /// <summary>folder is a working copy already picked, by a drop on the window; null starts the page empty.</summary>
    public AddCheckoutPage(string? folder = null)
    {
        InitializeComponent();
        Session.Log.Sink = Pane;
        Title = "Add checkout";
        Subtitle = Session.Root?.RootPath ?? "no root open";
        Fields.Owner = this;
        Fields.Root = Session.Root;
        Fields.Changed += Sync;
        if (folder != null) Fields.SetFolder(folder);
        Fields.Revalidate();
    }

    /// <summary>A control that cannot do anything is disabled, never a dialog explaining why.</summary>
    void Sync() => AddButton.IsEnabled = Session.Root != null && Fields.Ready;

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Root;
        if (root == null) return;
        // Read every field here: the work below runs on a thread pool thread, and touching a WinUI
        // control from there throws RPC_E_WRONG_THREAD.
        var fromUrl = Fields.FromUrl;
        var url = Fields.Url;
        var folder = Fields.Folder;
        var name = Fields.Name;
        var skip = Fields.Skip;
        var junctions = Fields.Junctions;
        var shared = Fields.Shared;
        var optional = Fields.Optional;

        AddButton.IsEnabled = false;
        Fields.IsEnabled = false;
        var r = await Busy.During(AddButton, () => Runner.Run(Pane, fromUrl ? "checkout " + url : "checkout add " + folder,
            () => fromUrl
                ? Ops.CheckoutFromUrl(root, url, folder.Length > 0 ? folder : null, skip, junctions, optional, name.Length > 0 ? name : null, shared)
                : Ops.CheckoutAdd(root, folder, skip, junctions, optional, name.Length > 0 ? name : null, shared)), restoreEnabled: false);

        if (r == null)
        {
            // It did not register, so let the user change what they typed and try again.
            Fields.IsEnabled = true;
            Fields.Revalidate();
            return;
        }

        Added = true;
        Result.Text = $"{r.Checkout.Name}: r{r.Snapshot.Revision}, {r.Snapshot.Externals.Count} external(s)";
        Pane.Append($"{r.Checkout.Name}: r{r.Snapshot.Revision}, snapshot {r.Snapshot.Sha[..10]}, {r.Snapshot.Externals.Count} external(s)");
        foreach (var w in r.Snapshot.Warnings) Pane.Append("warn: " + w);
        Fields.Root = Session.Root;
        Fields.Revalidate();
        CloseButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
