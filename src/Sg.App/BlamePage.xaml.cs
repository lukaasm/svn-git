using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// Who last touched each line of a file, the way this bridge has to answer it. A worktree's git history
/// is one commit per sync, so plain git blame names the sync and not the change; here the lines that came
/// in with a snapshot carry the SVN revision and the person who wrote them, and the lines the branch
/// changed carry its own commit. Picking a line shows what that change said and what else it did here.
/// </summary>
public sealed partial class BlamePage : SgPage
{
    /// <summary>The worktree the file lives in, or null when it is a file of the checkout.</summary>
    readonly string? _worktree;
    readonly CheckoutConfig? _co;
    readonly string _path;
    List<BlameRow> _rows = new();
    // Null, not "": a line svn could not blame carries an empty mark, and with "" as the sentinel the
    // first click on such a line looked like a click on the line already open and did nothing.
    string? _shown;
    /// <summary>The checkout this file belongs to, worked out once with the blame.</summary>
    CheckoutConfig? _base;
    int _generation;

    /// <summary>
    /// One file, from a branch worktree or from the checkout. Exactly one of worktree and co is given:
    /// a worktree file needs git as well as svn, a checkout file needs only svn.
    /// </summary>
    public BlamePage(string path, string? worktree, CheckoutConfig? co)
    {
        InitializeComponent();
        _path = path;
        _worktree = worktree;
        _co = co;
        Title = "Blame";
        Subtitle = path;
        ColumnSplitter.Attach(Splitter, minLeft: 420, minRight: 300);
        Shortcuts.DiffNavigation(this, Diff);
        Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadAsync());
        // A plain filter, not the file tree one: these rows are lines of one file, not paths, and a
        // tree of them would have nothing to branch on.
        Filter.TextChanged += (_, _) => ShowLines();
        Session.Log.Sink = Pane;
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        var root = Session.Require();
        var gen = ++_generation;
        LinesSkeleton.Show();
        // The checkout comes back with the blame, because working it out costs "git rev-parse" and a
        // config read, and it used to run on the UI thread every time an SVN line was picked.
        var read = await Runner.Quiet(Pane, () => new
        {
            Co = _co ?? Ops.BaseCheckout(root, root.Git.CurrentBranch(_worktree!)),
            Blame = _worktree != null ? Blame.OfWorktree(root, _worktree, _path) : Blame.OfCheckout(root, _co!, _path),
        });
        LinesSkeleton.Hide();
        if (read == null || gen != _generation) return;
        _base = read.Co;
        var result = read.Blame;

        // One colour per revision, handed out as they first appear, so a run of lines from one change
        // reads as a block rather than as a column of numbers.
        var colours = new Dictionary<string, int>(StringComparer.Ordinal);
        _rows = result.Lines.Select(l =>
        {
            if (!colours.TryGetValue(l.Mark, out var c)) colours[l.Mark] = c = colours.Count;
            return new BlameRow
            {
                Line = l,
                Colour = c,
                Display = $"{l.Mark} {l.Author} {l.Text}",
            };
        }).ToList();

        Filter.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowLines();
        Nothing.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Filled.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        WarnBar.Message = string.Join("\n", result.Warnings);
        WarnBar.IsOpen = result.Warnings.Count > 0;

        var svnLines = _rows.Count - result.LocalLines;
        Summary.Text = result.LocalLines == 0
            ? $"{_rows.Count} line(s), all of them from SVN."
            : $"{_rows.Count} line(s): {result.LocalLines} the branch changed, {svnLines} from SVN.";
        // A reload draws a new list, so nothing is open any more: the guard has to forget what was, or
        // clicking the line you were reading before Refresh does nothing at all.
        _shown = null;
        DetailHead.Text = "";
        DetailMessage.Text = "";
        if (_rows.Count > 0)
        {
            // Half the window is about the line nobody has picked yet, so it says so rather than
            // sitting empty under two headers.
            DetailHead.Text = "Pick a line on the left.";
            DetailMessage.Text = "Its revision, what that revision said, and what else it did to this file.";
            Diff.ShowText("", "pick a line");
        }
    }

    /// <summary>
    /// The lines on screen: all of them, or the ones the filter box matches. The header says which,
    /// the way every other list in this app says it.
    /// </summary>
    void ShowLines()
    {
        var query = Filter.Text.Trim();
        var shown = query.Length == 0
            ? _rows
            : _rows.Where(r => r.Display.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Lines.ItemsSource = shown;
        Header.Text = _rows.Count == 0 ? _path
            : shown.Count == _rows.Count ? $"{_path}   ({_rows.Count} lines)"
            : $"{_path}   showing {shown.Count} of {_rows.Count} lines";
    }

    async void Lines_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Lines.SelectedItem is not BlameRow row) return;
        var line = row.Line;
        if (line.Mark == _shown) return;
        _shown = line.Mark;
        var root = Session.Require();

        if (line.Local && line.Sha != null)
        {
            var sha = line.Sha;
            DetailHead.Text = $"{sha}\n{line.Author}   {line.Date}\nOn the branch. SVN has not seen this line.";
            DetailMessage.Text = Msg.Body(line.Summary);
            var mine = $"{_path}   {sha[..Math.Min(8, sha.Length)]}";
            Diff.BeginLoading(mine);
            var patch = await Task.Run(() => root.Git.UnifiedDiff(_worktree!, sha + "^", sha, _path));
            if (_shown == line.Mark) Diff.ShowUnified(patch, mine);
            return;
        }

        if (line.Revision is not { } revision || revision == 0)
        {
            DetailHead.Text = "Not committed yet.";
            DetailMessage.Text = "";
            Diff.ShowText("", "nothing to show");
            return;
        }

        // An SVN line: the revision's own message, and what it did to this file.
        var co = _base;
        if (co == null) return;
        var title = $"{_path}   r{revision}";
        Diff.BeginLoading(title);
        var read = await Runner.Quiet(Pane, () =>
        {
            var url = root.Svn.Info(co.Path, _path).Url;
            var log = root.Svn.LogVerbose(co.Path, _path, 200).FirstOrDefault(x => x.Revision == revision);
            return new { Log = log, Diff = root.Svn.DiffRevision(url, revision) };
        });
        if (read == null || _shown != line.Mark) return;
        DetailHead.Text = read.Log == null
            ? $"r{revision}   {line.Author}   {Msg.When(line.Date)}"
            : $"r{read.Log.Revision}   {read.Log.Author}   {Msg.When(read.Log.Date)}";
        DetailMessage.Text = Msg.Body(read.Log?.Message);
        Diff.ShowUnified(read.Diff, title);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => LoadAsync());

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
