using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sg.App;

/// <summary>
/// A window around one page, for the Tortoise style route: right click a folder, one window for the
/// action. The same page sits behind the pane in the overview when it is reached from there.
/// </summary>
public sealed partial class PageWindow : Window
{
    public PageWindow(Func<SgPage> make, string key, int width, int height)
    {
        InitializeComponent();
        WindowHelper.Chrome(this, AppTitleBar, width, height);
        Host.Window = this;
        Host.Changed += Sync;
        Host.AttachShortcuts((FrameworkElement)Content, escapeCloses: true);
        Host.Go(make, key);
    }

    /// <summary>The page on screen, for whoever opened the window and wants its answer.</summary>
    public SgPage? Page => Host.Current;

    /// <summary>Every window this process has open, by the key of the page in it.</summary>
    static readonly Dictionary<string, PageWindow> Live = new();

    /// <summary>
    /// The window for one page, made once. Right clicking the same folder twice used to leave two
    /// windows on the same commit, and a second Explorer menu on a folder already open put its window
    /// behind the first. Asking again reloads the page it holds, which is what asking again means.
    /// </summary>
    public static PageWindow For(Func<SgPage> make, string key, int width, int height)
    {
        if (Live.TryGetValue(key, out var live))
        {
            live.Host.Go(make, key);
            return live;
        }
        var w = new PageWindow(make, key, width, height);
        Live[key] = w;
        w.Closed += (_, _) =>
        {
            if (Live.TryGetValue(key, out var still) && ReferenceEquals(still, w)) Live.Remove(key);
        };
        return w;
    }

    /// <summary>Opens a page in a window of its own, sized like the window that page used to be.</summary>
    public static PageWindow Open(Func<SgPage> make, string key, int width = 1500, int height = 920)
    {
        var w = For(make, key, width, height);
        WindowHelper.Show(w);
        return w;
    }

    void Sync()
    {
        var page = Host.Current;
        Title = page?.Title ?? "sg";
        AppTitleBar.Title = page?.Title ?? "sg";
        AppTitleBar.Subtitle = page?.Subtitle ?? "";
        AppTitleBar.IsBackButtonVisible = Host.CanGoBack;
    }

    void Back_Click(TitleBar sender, object args) => Host.Back();
}
