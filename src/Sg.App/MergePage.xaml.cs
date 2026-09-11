using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Sg.Core;
using Windows.System;

namespace Sg.App;

/// <summary>
/// Taking changes from another server branch into the checkout, the way TortoiseSVN's merge does it.
/// Two shapes, one window: pick nothing and it takes everything the source branch has that this one has
/// not; pick revisions and it takes only those, which is the cherry pick.
///
/// It never commits. What a merge brings in is a local change of the checkout, and the changes window is
/// where it is read and sent, so a merge and an edit made by hand go out the same way.
/// </summary>
public sealed partial class MergePage : SgPage
{
    readonly CheckoutConfig _co;
    List<MergeTarget> _targets = new();
    List<MergeSource> _sources = new();
    List<SvnRevRow> _rows = new();
    int _generation;
    bool _binding;

    public MergePage(CheckoutConfig co)
    {
        InitializeComponent();
        _co = co;
        Title = "Merge from another branch";
        Checkout = co.Name;
        Subtitle = $"{co.Name}   {co.Path}";
        ColumnSplitter.Attach(Splitter);
        Shortcuts.DiffNavigation(this, Diff);
        Shortcuts.Add(this, VirtualKey.F5, () => _ = LoadTargetsAsync());
        Session.Log.Sink = Pane;
        _ = LoadTargetsAsync();
    }

    MergeTarget? Target => TargetBox.SelectedItem as MergeTarget;
    MergeSource? Source => SourceBox.SelectedItem as MergeSource;

    /// <summary>What is picked, oldest first. Empty means "everything these branches have not got".</summary>
    List<MergeRevision> Picked() => Revisions.SelectedItems.OfType<SvnRevRow>()
        .Where(r => !r.IsFold && _offered.ContainsKey(r))
        .Select(r => _offered[r]).OrderBy(r => r.Revision).ToList();

    /// <summary>How many server reads are in flight. The bar over the page runs while any of them is.</summary>
    int _busyDepth;

    void SetBusy(bool on)
    {
        _busyDepth += on ? 1 : -1;
        BusyBar.IsIndeterminate = _busyDepth > 0;
        Motion.FadeTo(BusyBar, _busyDepth > 0 ? 1 : 0);
    }

    async Task LoadTargetsAsync()
    {
        var root = Session.Require();
        SetBusy(true);
        try
        {
            var targets = await Runner.Quiet(Pane, () => Merge.Targets(root, _co));
            if (targets == null) return;
            _targets = targets;
            _binding = true;
            TargetBox.ItemsSource = _targets;
            TargetBox.SelectedIndex = _targets.Count > 0 ? 0 : -1;
            _binding = false;
            await LoadSourcesAsync();
        }
        finally { SetBusy(false); }
    }

    /// <summary>The branches on offer follow the working copy: a merge stays inside one repository.</summary>
    async Task LoadSourcesAsync()
    {
        var root = Session.Require();
        var target = Target;
        if (target == null) return;
        SetBusy(true);
        try { await LoadSourcesCoreAsync(root, target); }
        finally { SetBusy(false); }
    }

    async Task LoadSourcesCoreAsync(SgRoot root, MergeTarget target)
    {
        var gen = ++_generation;
        var sources = await Runner.Quiet(Pane, () => Merge.Sources(root, target));
        if (sources == null || gen != _generation) return;
        _sources = sources;
        _binding = true;
        SourceBox.ItemsSource = _sources;
        SourceBox.SelectedIndex = _sources.Count > 0 ? 0 : -1;
        _binding = false;
        if (_sources.Count == 0)
        {
            ShowProblems([$"no other branch of the same repository was found beside {target.Url}."]);
            _rows = new List<SvnRevRow>();
            Revisions.ItemsSource = _rows;
            Diff.ShowText("", "nothing to merge from");
            SyncButtons();
            return;
        }
        await LoadRevisionsAsync();
    }

    /// <summary>Every working copy this merge covers, and the branch folder each takes from.</summary>
    List<MergePair> _pairs = new();

    /// <summary>What is on offer, in the order the list shows it, with what a row stands for beside it.</summary>
    readonly Dictionary<SvnRevRow, MergeRevision> _offered = new();

    /// <summary>The revisions already taken are folded away until they are asked for.</summary>
    bool _mergedOpen;

    async Task LoadRevisionsAsync()
    {
        var root = Session.Require();
        var target = Target;
        var source = Source;
        if (target == null || source == null) return;
        var gen = ++_generation;
        _reading = true;
        SyncButtons();
        RevisionsSkeleton.Show();
        var read = await Runner.Quiet(Pane, () =>
        {
            // A merge of the root covers the externals too: the content of a monorepo lives in them.
            var pairs = Merge.Pairs(root, _co, target, source.Url);
            return new
            {
                Pairs = pairs,
                Offered = Merge.Offered(root, _co, pairs, 200),
                Problems = pairs.SelectMany(p => Merge.Problems(root, _co, p.Target, p.SourceUrl)).Distinct().ToList(),
            };
        });
        RevisionsSkeleton.Hide();
        _reading = false;
        if (read == null || gen != _generation) { SyncButtons(); return; }

        _pairs = read.Pairs;
        _offered.Clear();
        // One colour per working copy, handed out in the order the pairs come, and the tag only where
        // there is more than one of them to tell apart.
        var colour = _pairs.Select((p, i) => (p.Target.Wc, i)).ToDictionary(x => x.Wc, x => x.i, StringComparer.OrdinalIgnoreCase);
        var many = _pairs.Count > 1;
        foreach (var o in read.Offered)
        {
            var row = new SvnRevRow
            {
                Revision = o.Revision,
                Author = o.Entry.Author,
                Date = Msg.Day(o.Entry.Date),
                Subject = Msg.Subject(o.Entry.Message),
                Entry = o.Entry,
                Group = o.Pair.Target.Wc,
                ShowRepo = many,
                RepoColor = colour.TryGetValue(o.Pair.Target.Wc, out var c) ? c : 0,
                RepoTip = $"{o.Pair.Label} takes this from {o.Pair.SourceUrl}",
                Merged = o.Merged,
            };
            _offered[row] = o;
        }
        RevisionsHeader.Text = many
            ? $"Revisions on {source.Name}, across {_pairs.Count} working copies"
            : $"Revisions on {source.Name}";
        ShowProblems(read.Problems);
        ShowRevisions();
    }

    /// <summary>
    /// The list: what is still on offer, then one line for the revisions this checkout has already
    /// taken. Those are the bulk of a long-lived branch and none of them is a thing to do, so they are
    /// folded away until asked for.
    /// </summary>
    void ShowRevisions()
    {
        var open = _offered.Keys.Where(r => !r.Merged).ToList();
        var done = _offered.Keys.Where(r => r.Merged).ToList();
        _rows = new List<SvnRevRow>(open);
        if (done.Count > 0)
        {
            var fold = new SvnRevRow
            {
                IsFold = true,
                Expanded = _mergedOpen,
                Subject = done.Count == 1 ? "1 revision already merged" : $"{done.Count} revisions already merged",
                Author = "",
                Entry = done[0].Entry,
                Revision = done[0].Revision,
                Toggled = _ => { _mergedOpen = !_mergedOpen; ShowRevisions(); },
            };
            _rows.Add(fold);
            if (_mergedOpen) _rows.AddRange(done);
        }
        // The rows survive the rebuild, so what was picked can be picked again by identity.
        var keep = Revisions.SelectedItems.OfType<SvnRevRow>().Where(r => !r.IsFold).ToList();
        _binding = true;
        Revisions.ItemsSource = _rows;
        foreach (var back in keep) if (_rows.Contains(back)) Revisions.SelectedItems.Add(back);
        _binding = false;
        SyncButtons();
        ShowPickedText();
    }

    void ShowProblems(IReadOnlyList<string> problems)
    {
        ProblemBar.Message = string.Join("\n", problems);
        ProblemBar.IsOpen = problems.Count > 0;
        _blocked = problems.Count > 0;
    }

    bool _blocked;

    /// <summary>The revisions on offer are still being read, so nothing can be picked and nothing sent.</summary>
    bool _reading;

    /// <summary>
    /// A merge, a test merge or a take-back-out is running. Busy.During disables the button that was
    /// pressed and no other, and all three write the same working copy: pressing two of them left svn
    /// running two merges into one folder at once.
    /// </summary>
    bool _running;

    void SyncButtons()
    {
        // A merge that is offered before the list of revisions has arrived is a merge of everything,
        // pressed by somebody who has not seen what "everything" is yet.
        var ready = Target != null && Source != null && !_reading && !_running;
        TestButton.IsEnabled = ready;
        // A test merge writes nothing, so it stays available even while a problem blocks the real one.
        MergeButton.IsEnabled = ready && !_blocked;
        var picked = Picked();
        ClearPickButton.IsEnabled = picked.Count > 0;
        // Taking changes back out needs the revisions named: there is no "undo everything" to offer.
        TakeOutButton.IsEnabled = ready && !_blocked && picked.Count > 0;
        MergeLabel.Text = picked.Count == 0 ? "Merge" : picked.Count == 1 ? "Merge r" + picked[0].Revision : $"Merge {picked.Count} revisions";
    }

    void ShowPickedText()
    {
        var picked = Picked();
        if (Source == null || Target == null) { Summary.Text = ""; return; }
        // Which working copies it lands in, named, because a merge of the root is several of them.
        var where = picked.Count == 0
            ? string.Join(", ", _pairs.Select(x => x.Label))
            : string.Join(", ", picked.Select(x => x.Pair.Label).Distinct());
        Summary.Text = picked.Count == 0
            ? $"Everything {Source.Name} has that {where} has not."
            : $"{picked.Count} revision(s) into {where}.";
    }

    async void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        await LoadSourcesAsync();
    }

    async void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        await LoadRevisionsAsync();
    }

    void Revisions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding) return;
        // The line the merged revisions fold into is a fold, not a revision: pressing it opens them.
        if ((e.AddedItems.LastOrDefault() ?? Revisions.SelectedItem) is SvnRevRow { IsFold: true } fold)
        {
            // The click on the fold line replaced the whole selection with the fold. Put back what it
            // replaced before opening it: reading the revisions already taken is not a change of mind
            // about which ones to merge, and it used to throw every pick away.
            _binding = true;
            Revisions.SelectedItems.Remove(fold);
            foreach (var back in e.RemovedItems.OfType<SvnRevRow>()) Revisions.SelectedItems.Add(back);
            _binding = false;
            fold.Toggle();
            return;
        }
        SyncButtons();
        ShowPickedText();
    }

    void ClearPick_Click(object sender, RoutedEventArgs e)
    {
        Revisions.SelectedItems.Clear();
        SyncButtons();
        ShowPickedText();
    }

    async void Test_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => RunAsync(dryRun: true), restoreEnabled: false);

    /// <summary>The same merge, run backwards: what TortoiseSVN calls reverting the changes of a revision.</summary>
    async void TakeOut_Click(object sender, RoutedEventArgs e)
    {
        var target = Target;
        var picked = Picked();
        if (target == null || Source == null || picked.Count == 0) return;
        if (!await Dialogs.Confirm(this, "Take back out",
            $"Undo {picked.Count} revision(s) in {string.Join(", ", picked.Select(x => x.Pair.Label).Distinct())}? It lands as local changes in the checkout; nothing goes to the server until you commit them.", "Take back out")) return;
        await Busy.During(sender, () => RunAsync(dryRun: false, reverse: true), restoreEnabled: false);
    }

    async void Merge_Click(object sender, RoutedEventArgs e)
    {
        var target = Target;
        var source = Source;
        if (target == null || source == null) return;
        var picked = Picked();
        var what = picked.Count == 0
            ? $"everything {source.Name} has that {string.Join(", ", _pairs.Select(x => x.Label))} has not"
            : $"{picked.Count} revision(s) into {string.Join(", ", picked.Select(x => x.Pair.Label).Distinct())}";
        if (!await Dialogs.Confirm(this, "Merge",
            $"Bring {what} into {target.Label}? It lands as local changes in the checkout; nothing goes to the server until you commit them.", "Merge")) return;
        await Busy.During(MergeButton, () => RunAsync(dryRun: false), restoreEnabled: false);
    }

    async Task RunAsync(bool dryRun, bool reverse = false)
    {
        var root = Session.Require();
        var target = Target;
        var source = Source;
        if (target == null || source == null || _running) return;
        _running = true;
        SyncButtons();
        try { await RunCoreAsync(root, target, source, dryRun, reverse); }
        finally { _running = false; SyncButtons(); ShowPickedText(); }
    }

    async Task RunCoreAsync(SgRoot root, MergeTarget target, MergeSource source, bool dryRun, bool reverse)
    {
        var picked = Picked();
        var label = dryRun ? "test merge" : reverse ? "take back out" : "merge";
        ResultBar.IsOpen = false;

        var r = await Runner.Run(Pane, label, () => Merge.RunAll(root, _co, _pairs, picked.Count == 0 ? null : picked, dryRun, reverse));
        if (r == null) return;

        OutcomeHeader.Text = dryRun ? "What a merge would do" : "What the merge did";
        Diff.ShowText(Describe(r), $"{source.Name} → {target.Label}" + (dryRun ? "   test only, nothing written" : ""));

        if (dryRun)
        {
            Summary.Text = r.Changed.Count == 0
                ? "Nothing would change."
                : $"{r.Changed.Count} path(s) would change" + (r.Clean ? "." : $", {r.Conflicts.Count} in conflict.");
            return;
        }

        ResultBar.Severity = r.Clean ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Message = r.Changed.Count == 0
            ? "Nothing changed: there was nothing to bring in."
            : r.Clean
                ? $"{r.Changed.Count} path(s) changed in the checkout. Nothing is committed yet."
                : $"{r.Changed.Count} path(s) changed, {r.Conflicts.Count} in conflict. Solve the conflicts in the checkout, then commit.";
        ResultBar.ActionButton = ChangesButton();
        ResultBar.IsOpen = true;
        // The revisions this branch has already taken change what a further merge would do.
        await LoadRevisionsAsync();
    }

    /// <summary>The merge is only half the job; the changes window is where what it brought in goes out.</summary>
    Button ChangesButton()
    {
        var b = new Button { Content = "Open the checkout changes" };
        ToolTipService.SetToolTip(b, "See what the merge brought in, file by file, and commit it to SVN when it is right.");
        b.Click += (_, _) => Go(() => new SvnCommitPage(_co) { Checkout = Checkout }, "changes:" + _co.Name);
        return b;
    }

    static string Describe(MergeResult r)
    {
        if (r.Changed.Count == 0) return "Nothing would change.\n\n" + r.Output;
        var lines = new List<string>();
        lines.Add(r.Revisions.Count == 0
            ? "Everything the source branch has that this one has not:"
            : (r.Reverse ? "Taking back out " : "Revisions ") + string.Join(", ", r.Revisions.Select(x => "r" + x)) + ":");
        lines.Add("");
        foreach (var (action, path) in r.Changed) lines.Add($"{action}  {path}");
        if (r.Conflicts.Count > 0)
        {
            lines.Add("");
            lines.Add($"{r.Conflicts.Count} in conflict:");
            foreach (var c in r.Conflicts) lines.Add("  " + c);
        }
        lines.Add("");
        lines.Add("--- what svn said ---");
        lines.Add(r.Output);
        return string.Join("\n", lines);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await Busy.During(sender, () => LoadTargetsAsync());

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
