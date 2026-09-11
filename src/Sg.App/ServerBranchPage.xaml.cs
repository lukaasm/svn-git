using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>One external of the source checkout, and what the new branch does with it.</summary>
public sealed class BranchPartRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>The page follows every row: a change makes the last dry run stale.</summary>
    public event Action? Changed;

    bool _branched = true;
    bool _under;
    string _branch = "";
    string _placeholder = "";

    /// <summary>Path of the external inside the checkout, like libs/tools.</summary>
    public string Wc { get; set; } = "";
    /// <summary>Where it points now.</summary>
    public string Url { get; set; } = "";

    /// <summary>On: copied to the new branch. Off: kept where it is, and the line that brings it in stays.</summary>
    public bool Branched
    {
        get => _branched;
        set
        {
            if (_branched == value) return;
            _branched = value;
            Raise(nameof(Branched));
            Raise(nameof(NameVisibility));
            Changed?.Invoke();
        }
    }

    /// <summary>Inside an external that is kept: kept with it, and not the user's to change until that one is branched again.</summary>
    public bool Under
    {
        get => _under;
        set
        {
            if (_under == value) return;
            _under = value;
            Raise(nameof(Free));
        }
    }

    public bool Free => !_under;

    /// <summary>A branch name of its own. Empty means the name of the whole.</summary>
    public string Branch
    {
        get => _branch;
        set
        {
            if (_branch == value) return;
            _branch = value;
            Raise(nameof(Branch));
            Changed?.Invoke();
        }
    }

    /// <summary>What an empty name box shows: the name of the whole, as typed above.</summary>
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            if (_placeholder == value) return;
            _placeholder = value;
            Raise(nameof(Placeholder));
        }
    }

    public Visibility NameVisibility => _branched ? Visibility.Visible : Visibility.Collapsed;

    public BranchPart ToPart() => new() { Wc = Wc, Url = Url, Keep = !_branched, Branch = _branch.Trim() };

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class ServerBranchPage : SgPage
{
    ServerBranchPlan? _plan;
    List<BranchPartRow> _rows = new();
    bool _loaded;
    /// <summary>The page is setting rows itself, after one was kept; that is not the user changing them.</summary>
    bool _locking;

    public ServerBranchPage(CheckoutConfig? preselect)
    {
        InitializeComponent();
        var root = Session.Require();
        Title = "New server branch";
        Checkout = preselect?.Name;
        Subtitle = root.RootPath;
        FromBox.ItemsSource = root.Config.Checkouts.Select(c => c.Name).ToList();
        FromBox.SelectedItem = (preselect ?? root.Config.Checkouts.FirstOrDefault())?.Name;
        Session.Log.Sink = Pane;
        ColumnSplitter.Attach(Splitter, minLeft: 360, minRight: 360);
        // Lists must not be filled before the content is on screen. That crashes inside XAML.
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            _ = LoadPartsAsync();
        };
    }

    CheckoutConfig? Source() => FromBox.SelectedItem is string n ? Session.Require().Checkout(n) : null;

    string PlaceholderText() => NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "same as the branch name";

    /// <summary>The last dry run no longer says what Create would do.</summary>
    /// <summary>A dry run is a server read of several seconds; the fields can move under it.</summary>
    int _generation;

    void Stale()
    {
        _generation++;
        _plan = null;
        ShowPlan("");
        CreateButton.IsEnabled = false;
    }

    void Name_TextChanged(object sender, TextChangedEventArgs e)
    {
        foreach (var r in _rows) r.Placeholder = PlaceholderText();
        Stale();
    }

    void From_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        Stale();
        _ = LoadPartsAsync();
    }

    /// <summary>The externals of the picked checkout, one card each. svn status on a big checkout takes a moment, so bars stand in first.</summary>
    async Task LoadPartsAsync()
    {
        var root = Session.Root;
        var co = Source();
        if (root == null || co == null) return;
        _rows = new();
        Parts.ItemsSource = null;
        NoParts.Visibility = Visibility.Collapsed;
        PartsSkeleton.Show();
        var parts = await Runner.Quiet(Pane, () => Server.Parts(root, co));
        if (Source()?.Name != co.Name) return;   // the user picked another checkout while svn answered
        PartsSkeleton.Hide();
        if (parts == null) return;
        _rows = parts.Where(p => p.Wc.Length > 0).Select(p => new BranchPartRow { Wc = p.Wc, Url = p.Url, Placeholder = PlaceholderText() }).ToList();
        foreach (var r in _rows) r.Changed += OnRowChanged;
        Parts.ItemsSource = _rows;
        NoParts.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PartsHeader.Text = _rows.Count == 0 ? "Externals" : $"Externals ({_rows.Count})";
    }

    void OnRowChanged()
    {
        if (_locking) return;
        _locking = true;
        try
        {
            // An external inside a kept one goes with it: the line that brings it in lives in a working copy nobody copies.
            foreach (var r in _rows)
            {
                var under = _rows.Any(o => !ReferenceEquals(o, r) && !o.Branched && PathUtil.IsUnder(r.Wc, o.Wc));
                if (under) r.Branched = false;
                r.Under = under;
            }
        }
        finally { _locking = false; }
        Stale();
    }

    void ShowPlan(string text)
    {
        Plan.Text = text;
        NoPlan.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanCard.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    async void DryRun_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Require();
        var co = Source();
        var name = NameBox.Text.Trim();
        if (co == null || name.Length == 0) { ShowPlan("Give a branch name and pick a checkout."); return; }
        var msg = MessageBox.Text.Trim().Length > 0 ? MessageBox.Text.Trim() : null;
        var parts = _rows.Select(r => r.ToPart()).ToList();
        var gen = ++_generation;
        var plan = await Busy.During(sender, () => Runner.Run(Pane, "dry run " + name, () => Server.PlanBranch(root, co, name, msg, parts)));
        // Retyping the name or picking another checkout while this ran put the plan away. Landing after
        // that used to put it back and arm Create, over fields the plan no longer described.
        if (gen != _generation) return;
        _plan = plan;
        ShowPlan(_plan?.Describe() ?? "");
        CreateButton.IsEnabled = _plan != null;
    }

    async void Create_Click(object sender, RoutedEventArgs e)
    {
        var root = Session.Require();
        var co = Source();
        if (_plan == null || co == null) return;
        var plan = _plan;
        if (!await Dialogs.Confirm(this, "Create server branch " + plan.Name,
                $"This makes {plan.Repos.Count} revision(s) on the server that everyone can see. Continue?", "Create")) return;
        await Busy.During(CreateButton, async () =>
        {
            var ok = await Runner.Run(Pane, "server branch " + plan.Name, () => Server.ExecuteBranch(root, plan));
            ShowPlan(plan.Describe() + "\n\n" + string.Join("\n", plan.Repos.Select(r => $"{r.ReposRoot}: {r.State}" + (r.Revision.HasValue ? $" r{r.Revision}" : ""))));
            if (!ok) return;
            if (NoCheckout.IsChecked == true)
            {
                Pane.Append("branch made. Make a checkout later with: New server checkout");
                return;
            }
            var res = await Runner.Run(Pane, "server checkout " + plan.Name, () => Server.Checkout(root, co, plan.NewRootUrl, plan.Name));
            if (res != null) Pane.Append($"checkout {res.Checkout.Name}: {res.Checkout.Path}, r{res.Snapshot.Revision}");
        }, restoreEnabled: false);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
