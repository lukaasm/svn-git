using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// A list of paths inside a checkout, chosen rather than typed. It offers the folders that are
/// really there and keeps the text box, because a folder that does not exist yet is still a valid
/// answer and a power user should not have to click three times to say four of them.
/// </summary>
public sealed partial class PathPicker : UserControl
{
    public PathPicker()
    {
        InitializeComponent();
        Candidates.SelectionChanged += (_, _) => AddButton.IsEnabled = Candidates.SelectedItem != null;
        Box.TextChanged += (_, _) => Changed?.Invoke();
    }

    /// <summary>The list changed. Hosts that enable a Save on it listen here.</summary>
    public event Action? Changed;

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(PathPicker),
        new PropertyMetadata("", (d, e) => ((PathPicker)d).HeaderText.Text = (string)e.NewValue));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>One path per line, the way the config stores them.</summary>
    public List<string> Paths
    {
        get => Box.Text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        set => Box.Text = string.Join("\n", value);
    }

    public new bool IsEnabled
    {
        set
        {
            Box.IsEnabled = value;
            Candidates.IsEnabled = value;
            AddButton.IsEnabled = value && Candidates.SelectedItem != null;
        }
    }

    /// <summary>
    /// Offers the folders of a checkout, two levels deep. Two is what the paths in this config look
    /// like: libs/prebuilt and engine, never libs/prebuilt/data/gui. Reading it is a directory
    /// listing, so it happens off the UI thread and the box works before it arrives.
    /// </summary>
    public async Task OfferFoldersOf(string checkoutPath)
    {
        var found = await Task.Run(() => Folders(checkoutPath));
        Candidates.ItemsSource = found;
    }

    static List<string> Folders(string root, int depth = 2)
    {
        var res = new List<string>();
        if (!Directory.Exists(root)) return res;

        void Walk(string dir, string rel, int left)
        {
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (name is ".svn" or ".git" or ".sg") continue;
                var here = rel.Length == 0 ? name : rel + "/" + name;
                res.Add(here);
                if (left > 1) Walk(sub, here, left - 1);
            }
        }

        Walk(root, "", depth);
        res.Sort(StringComparer.OrdinalIgnoreCase);
        return res;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Candidates.SelectedItem is not string picked) return;
        var paths = Paths;
        if (paths.Contains(picked, StringComparer.OrdinalIgnoreCase)) return;
        paths.Add(picked);
        Paths = paths;
        Changed?.Invoke();
    }
}
