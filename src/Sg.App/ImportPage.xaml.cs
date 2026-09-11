using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// One working copy the export was cut from, beside the revision this checkout has instead. The two
/// numbers sit next to each other because the difference between them is the whole question the page
/// is asked: every commit is merged across it.
/// </summary>
public sealed class ImportBaseRow
{
    public string Where { get; set; } = "";
    public string Url { get; set; } = "";
    public string Exported { get; set; } = "";
    public string Local { get; set; } = "";
    public bool Differs { get; set; }

    /// <summary>Where this working copy points instead, when it points somewhere else entirely.</summary>
    public string? Note { get; set; }

    /// <summary>The URL to draw: the one it is at here when that is not the one it was exported from.</summary>
    public string Shown => Note ?? Url;

    public Brush LocalBrush =>
        (Brush)Application.Current.Resources[Differs ? "StatusModifiedBrush" : "TextFillColorSecondaryBrush"];

    public override string ToString() => $"{Where}  exported at {Exported}, here {Local}  {Shown}";
}

/// <summary>
/// An export file, read before it is put back. It says what branch is in it, what it was cut from, and
/// how far this checkout has moved since - and only then offers the button, because the answer to that
/// last question is what the import will have to merge across.
/// </summary>
public sealed partial class ImportPage : SgPage
{
    string _file;
    ExportMeta? _meta;
    bool _binding;

    /// <summary>A checkout here points at the URL the export names. When none does, saying so is the answer.</summary>
    bool _matched;

    /// <summary>The worktree an import is sitting stopped in, once one is. Null while nothing has stopped.</summary>
    string? _waiting;

    public ImportPage(string file)
    {
        InitializeComponent();
        _file = file;
        Title = "Import a branch";
        Session.Log.Sink = Pane;
        _ = LoadAsync();
    }

    CheckoutConfig? Into => IntoBox.SelectedItem is string name ? Session.Root?.Checkout(name) : null;

    async Task LoadAsync()
    {
        var root = Session.Require();
        Subtitle = _file;
        FilePath.Text = _file;

        var read = await Runner.Quiet(Pane, () =>
        {
            var meta = Export.Read(_file);
            var co = Export.MatchCheckout(root, meta);
            return new { Meta = meta, Co = co, Drift = co == null ? new List<ExportDrift>() : Export.DriftOf(root, meta, co) };
        });
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
        var drift = co == null ? new List<ExportDrift>() : Export.DriftOf(Session.Require(), meta, co);
        var rows = meta.Bases.Select(b =>
        {
            var d = drift.FirstOrDefault(x => x.Where == b.Where);
            return new ImportBaseRow
            {
                Where = b.Where,
                Url = b.Url,
                Exported = "r" + b.Revision,
                Local = d == null ? "r" + b.Revision
                    : d.Elsewhere ? "another branch"
                    : d.Missing ? "not here"
                    : "r" + d.Local,
                Differs = d != null,
                Note = d?.Elsewhere == true ? d.LocalUrl : null,
            };
        }).ToList();
        Bases.ItemsSource = rows;

        var moved = rows.Where(r => r.Differs).ToList();
        DriftBar.IsOpen = moved.Count > 0;
        DriftBar.Message = moved.Count == 0
            ? ""
            : (moved.Any(m => m.Note != null)
                  ? "One of these points at another branch of its repository, so the two revisions are not comparable at all and the merge could be large. "
                  : "Every commit is merged across the difference, the way a rebase does, so read the diff before you push. ")
              + string.Join(", ", moved.Take(6).Select(r => $"{r.Where} {r.Exported} → {r.Local}"))
              + (moved.Count > 6 ? $", and {moved.Count - 6} more" : "");
    }

    void SyncButton()
    {
        var name = NameBox.Text.Trim();
        var taken = name.Length > 0 && Session.Root?.Git.RefSha("refs/heads/" + name) != null;
        ImportButton.IsEnabled = _meta != null && name.Length > 0 && Into != null && !taken;
        ImportLabel.Text = _meta == null ? "Import" : $"Import {_meta.Commits} commit(s)";
        Summary.Text = _meta == null ? ""
            : Into == null && !_matched ? $"No checkout here points at {_meta.Root?.Url}. Pick one only if you know it is the same repository."
            : Into == null ? "Pick the checkout to build it on."
            : name.Length == 0 ? "Give the branch a name."
            : taken ? $"{name} is already a branch here. Give it another name."
            : $"{name} will be made on {Into.Name}, and its worktree with it.";
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
        var meta = _meta;
        var co = Into;
        if (meta == null || co == null) return;
        var name = NameBox.Text.Trim();
        var root = Session.Require();

        // The worktree is the long part: it is a checkout of the whole tree, the same as sg branch.
        if (!await Dialogs.Confirm(this, "Import " + name,
                $"Make the branch {name} on {co.Name}, and a worktree folder for it at {root.WorktreePathFor(name)}?\n\n"
                + $"{meta.Commits} commit(s) are replayed onto the snapshot this checkout has now. Nothing goes to SVN.",
                "Import"))
            return;

        var res = await Busy.During(sender, () => Runner.Run(Pane, "import " + name, () => Export.Import(root, _file, name, co.Name)), restoreEnabled: false);
        if (res == null) { SyncButton(); return; }

        ResultBar.Severity = res.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = res.Ok
            ? $"{res.Branch} is here: {res.Applied} commit(s) in {res.Path}."
              + (res.Drift.Count == 0 ? "" : " They were merged across " + res.Drift.Count + " revision(s) that had moved on.")
            : $"{res.Applied} of {res.Commits} commit(s) went in, and it stopped on \"{res.Stopped}\". "
              + $"The import is waiting in {res.Path} with the rest of the series behind it: pick a version for each file, then continue."
              + (res.Why == null ? "" : "\n" + res.Why.Split('\n')[0]);
        ResultBar.IsOpen = true;

        ConflictsHeader.Visibility = ConflictsCard.Visibility = res.Conflicted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Conflicts.ItemsSource = res.Conflicted;

        // A patch that would not merge does not cost the rest of them any more: the import stays where
        // it stopped, and one button away is the page that finishes it.
        _waiting = res.Waiting ? res.Path : null;
        ResolveButton.Visibility = res.Waiting ? Visibility.Visible : Visibility.Collapsed;

        // The branch exists now, so this page has nothing left to offer about this file.
        ImportButton.IsEnabled = false;
        Summary.Text = res.Ok ? "Done. Close this and the worktree is on the checkout's card."
            : "Resolve it, and the commits behind it land too.";
    }

    /// <summary>The page that finishes a stopped import. It is the same one a stopped rebase opens.</summary>
    void Resolve_Click(object sender, RoutedEventArgs e)
    {
        var wt = _waiting;
        if (wt == null) return;
        Go(() => new ConflictPage(wt) { Checkout = Into?.Name, Branch = NameBox.Text.Trim() }, "resolve:" + wt);
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
