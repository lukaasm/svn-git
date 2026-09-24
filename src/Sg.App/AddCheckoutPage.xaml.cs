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
    readonly SgRoot? _root;
    bool _running;
    /// <summary>True when a checkout was registered, so the overview knows to read the root again.</summary>
    public bool Added { get; private set; }

    /// <summary>folder is a working copy already picked, by a drop on the window; null starts the page empty.</summary>
    public AddCheckoutPage(string? folder = null)
    {
        InitializeComponent();
        _root = Session.Root;
        Session.Log.Sink = Pane;
        Title = "Add checkout";
        Subtitle = Session.Root?.RootPath ?? "no root open";
        Fields.Owner = this;
        Fields.Root = _root;
        Fields.Changed += Sync;
        if (folder != null) Fields.SetFolder(folder);
        Fields.Revalidate();
    }

    /// <summary>A control that cannot do anything is disabled, never a dialog explaining why.</summary>
    void Sync()
    {
        AddButton.IsEnabled = !_running && !Added && _root != null && Session.Root == _root && Fields.Ready;
        TaskGate.SetHelp(AddButton, _running ? "Adding this checkout. Follow progress in Tasks."
            : Added ? "This checkout has already been added."
            : _root == null || Session.Root != _root ? "Open the target root before adding a checkout."
            : Fields.Ready ? "Register this checkout and build its first snapshot." : Fields.ReadyReason);
    }

    async void Add_Click(object sender, RoutedEventArgs e)
    {
        var root = _root;
        if (_running || Added || root == null || Session.Root != root || !Fields.Ready) return;
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

        _running = true;
        Sync();
        Fields.IsEnabled = false;
        var r = await Busy.During(AddButton, () => Runner.Run(Pane, fromUrl ? "checkout " + url : "checkout add " + folder,
            () => fromUrl
                ? Ops.CheckoutFromUrl(root, url, folder.Length > 0 ? folder : null, skip, junctions, optional, name.Length > 0 ? name : null, shared)
                : Ops.CheckoutAdd(root, folder, skip, junctions, optional, name.Length > 0 ? name : null, shared)), restoreEnabled: false);

        _running = false;
        if (r == null)
        {
            // It did not register, so let the user change what they typed and try again.
            Fields.IsEnabled = true;
            Fields.Revalidate();
            return;
        }

        Added = true;
        var at = Rev.Label(r.Snapshot.Revision, r.Snapshot.Commit);
        var parts = r.Checkout.IsGit ? $"git {r.Checkout.Remote}/{r.Checkout.Branch}" : $"{r.Snapshot.Externals.Count} external(s)";
        Result.Text = $"{r.Checkout.Name}: {at}, {parts}";
        Pane.Append($"{r.Checkout.Name}: {at}, snapshot {r.Snapshot.Sha[..10]}, {parts}");
        foreach (var w in r.Snapshot.Warnings) Pane.Append("warn: " + w);
        // Registration creates the .git pointer that preflight rejects. The submitted form is
        // complete now; validating it again would turn a successful result into an ownership error.
        CloseButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        if (Window is MainWindow main) main.CheckoutRegistered(this, root, r.Checkout.Name);
        else if (Session.Root == root) Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
