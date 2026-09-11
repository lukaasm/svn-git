using Microsoft.UI.Xaml;
using Sg.Core;

namespace Sg.App;

/// <summary>Two steps to a working root: 'sg init', then the first 'sg checkout add'. The second step is optional.</summary>
public sealed partial class NewRootPage : SgPage
{
    SgRoot? _root;
    bool _added;

    public NewRootPage()
    {
        InitializeComponent();
        Session.Log.Sink = Pane;
        Title = "New root";
        RootBox.TextChanged += (_, _) => Sync();
        Fields.Owner = this;
        Fields.Changed += Sync;
        Sync();
    }

    /// <summary>A control that cannot do anything is disabled, never a dialog explaining why.</summary>
    void Sync()
    {
        var step2 = _root != null && !_added;
        CreateRootButton.IsEnabled = _root == null && RootBox.Text.Trim().Length > 0;
        Fields.IsEnabled = step2;
        AddButton.IsEnabled = step2 && Fields.Ready;
        Step2.Opacity = _root == null ? 0.5 : 1.0;
    }

    async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var path = await WindowHelper.PickFolder(this);
        if (path != null) RootBox.Text = path;
    }

    async void CreateRoot_Click(object sender, RoutedEventArgs e)
    {
        // Read every control here: the work below runs on a thread pool thread, and
        // touching a WinUI control from there throws RPC_E_WRONG_THREAD.
        var path = RootBox.Text.Trim();
        var fsmonitor = FsMonitor.IsChecked == true;
        CreateRootButton.IsEnabled = false;
        var root = await Busy.During(CreateRootButton, () => Runner.Run(Pane, "init " + path, () => Ops.Init(path, Session.Log, fsmonitor)), restoreEnabled: false);
        if (root == null) { Sync(); return; }

        _root = root;
        Session.Open(root.RootPath);
        // The fields can only answer "already registered" once there is a root to ask about.
        Fields.Root = root;
        Fields.Revalidate();
        Subtitle = root.RootPath;
        RootBox.IsEnabled = false;
        FsMonitor.IsEnabled = false;
        Step1Tick.Visibility = Visibility.Visible;
        Sync();
    }

    async void AddCheckout_Click(object sender, RoutedEventArgs e)
    {
        var root = _root;
        if (root == null) return;
        var fromUrl = Fields.FromUrl;
        var url = Fields.Url;
        var folder = Fields.Folder;
        var name = Fields.Name;
        var skip = Fields.Skip;
        var junctions = Fields.Junctions;
        var shared = Fields.Shared;
        var optional = Fields.Optional;
        AddButton.IsEnabled = false;
        var r = await Busy.During(AddButton, () => Runner.Run(Pane, fromUrl ? "checkout " + url : "checkout add " + folder,
            () => fromUrl
                ? Ops.CheckoutFromUrl(root, url, folder.Length > 0 ? folder : null, skip, junctions, optional, name.Length > 0 ? name : null, shared)
                : Ops.CheckoutAdd(root, folder, skip, junctions, optional, name.Length > 0 ? name : null, shared)), restoreEnabled: false);
        if (r == null) { Sync(); return; }

        Pane.Append($"{r.Checkout.Name}: r{r.Snapshot.Revision}, snapshot {r.Snapshot.Sha[..10]}, {r.Snapshot.Externals.Count} external(s)");
        foreach (var w in r.Snapshot.Warnings) Pane.Append("warn: " + w);
        _added = true;
        Step2Tick.Visibility = Visibility.Visible;
        Sync();
        FinishButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
