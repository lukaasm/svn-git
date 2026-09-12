using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Sg.Core;

namespace Sg.App;

/// <summary>The colours the status letters wear. The brushes live in Templates.xaml, one set per theme.</summary>
public static class StatusColors
{
    public static Brush BrushFor(string code) => (Brush)Application.Current.Resources[Key(code) + "Brush"];
    public static Brush BackgroundFor(string code) => (Brush)Application.Current.Resources[Key(code) + "BackgroundBrush"];

    /// <summary>git writes two letters for some states, "MM" or "??"; the first one carries the colour.</summary>
    static string Key(string code)
    {
        var c = code.Trim();
        if (c.Length > 1 && c != "??") c = c[..1];
        return c switch
        {
            "M" or "P" => "StatusModified",
            "A" => "StatusAdded",
            "D" or "!" => "StatusDeleted",
            "R" => "StatusRenamed",
            "C" or "U" or "~" => "StatusConflict",
            "?" or "??" => "StatusUntracked",
            _ => "StatusOther",
        };
    }

    public static string Describe(string code) => code.Trim() switch
    {
        "M" => "Modified",
        "MM" => "Modified, part of it staged",
        "MD" => "Staged, then deleted from disk",
        "P" => "Properties changed",
        "A" => "Added",
        "AM" => "Added, then modified",
        "AD" => "Added, then deleted from disk",
        "RM" => "Renamed, then modified",
        "D" => "Deleted",
        "!" => "Missing from disk",
        "R" => "Renamed or replaced",
        "C" => "In conflict",
        "U" or "UU" => "In conflict",
        "~" => "Obstructed: something else sits where a versioned item should be",
        "?" or "??" => "Not under version control",
        "" => "",
        _ => code,
    };
}

/// <summary>A row in any file list: a coloured status letter, the path, and the working copy it belongs to.</summary>
public abstract class StatusRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>M, A, D, R, C, ?, ! and the like.</summary>
    public abstract string Code { get; }
    /// <summary>The path the way the list shows it.</summary>
    public abstract string PathText { get; }
    /// <summary>
    /// The bare path the tree cuts into folders. PathText may decorate it, with "(was ...)" and the
    /// like; this one must not, or the folder names come out wrong.
    /// </summary>
    public abstract string TreePath { get; }
    /// <summary>The working copy the row belongs to; "" is the root. Lists grouped by repository use it.</summary>
    public virtual string Group => "";
    /// <summary>What the filter box searches. The windows set it.</summary>
    public string Display { get; set; } = "";
    public Brush CodeBrush => StatusColors.BrushFor(Code);
    public Brush CodeBackground => StatusColors.BackgroundFor(Code);
    public string CodeTip => StatusColors.Describe(Code);
}

public sealed class CheckoutRow
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Detail { get; set; } = "";
    public CheckoutConfig Config { get; set; } = null!;
    /// <summary>The badges inside the navigation item: commits to sync, and edits made in the checkout.</summary>
    public StatusChip? RemoteBadge { get; set; }
    public StatusChip? LocalBadge { get; set; }
}

/// <summary>One shelf in the list: what it is called, where it came from, how much is in it, and when.</summary>
public sealed class ShelfRow
{
    bool? _gone;

    public ShelfInfo Shelf { get; set; } = null!;

    /// <summary>
    /// Whether the folder it goes back to is still there, asked once. ShelfInfo answers it by looking at
    /// the disk, and three of the things a row draws read it: a list of twenty on a network path was
    /// sixty waits on the UI thread every time it drew.
    /// </summary>
    public bool Gone => _gone ??= Shelf.Gone;

    public string Title => Shelf.Title;
    /// <summary>A checkout wears the checkout glyph and a branch the branch one, so the two never read alike.</summary>
    public string Glyph => Shelf.IsCheckout ? "\uE8B7" : "\uE8F4";
    public string Where => (Shelf.IsCheckout ? "checkout " : "branch ") + Shelf.Where;
    public string CountText => Shelf.Count == 1 ? "1 file" : Shelf.Count + " files";
    public string When => Shelf.Created == default ? "" : Shelf.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public Brush TitleBrush => (Brush)Application.Current.Resources[Gone ? "TextFillColorDisabledBrush" : "TextFillColorPrimaryBrush"];

    public string Tip =>
        $"{Shelf.Title}\n{Shelf.Count} file(s) from {Where}\n{Shelf.Path}"
        + (Gone ? "\nThat folder is gone, so this shelf has nowhere to go back to." : "")
        + "\n" + Shelf.Id;
}

/// <summary>What a worktree card can do. The primary one sits beside the name; the rest are rows under it.</summary>
public enum WorktreeAction { Commit, Log, Push, Rebase, Resolve, Remove }

public sealed class WorktreeRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    string _sizeText = "measuring...";
    bool _reclaimable;
    bool _expanded;

    /// <summary>
    /// The card is open on its detail rows. It lives on the row so a refresh that has to rebind the
    /// list can carry it over, instead of folding every card the user opened.
    /// </summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>Filled in from a background walk, so the card shows it when it arrives.</summary>
    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (_sizeText == value) return;
            _sizeText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        }
    }

    /// <summary>Nothing here is unpushed and nobody has touched it for weeks.</summary>
    public bool Reclaimable
    {
        get => _reclaimable;
        set
        {
            if (_reclaimable == value) return;
            _reclaimable = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Reclaimable)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReclaimVisibility)));
        }
    }

    public Visibility ReclaimVisibility => _reclaimable ? Visibility.Visible : Visibility.Collapsed;

    long _copied;
    string _copiedWhat = "";
    string? _cloneProblem;

    /// <summary>
    /// What the size walk found in the shared folders. A junction is the checkout's own folder and a
    /// ReFS clone shares its blocks until one side writes, so neither costs this branch anything; a
    /// full copy costs the folder's size again, once per worktree, and that is what the badge names.
    /// The walk lands after the card is drawn, so this raises for every property the badge reads.
    ///
    /// cloneProblem is why a block clone cannot be made here, or null when it can. It decides whether
    /// this is advice or only an amount: on a volume that cannot clone, sg itself tells you to use copy
    /// when you make the branch, and a badge that then asked for a clone would be asking for hardware.
    /// </summary>
    public void ShowCopied(IReadOnlyList<SharedCost> shared, string? cloneProblem)
    {
        var copied = shared.Where(s => s.Copied).ToList();
        var bytes = copied.Sum(s => s.Bytes);
        var what = string.Join(", ", copied.OrderByDescending(s => s.Bytes).Select(s => s.Rel));
        if (_copied == bytes && _copiedWhat == what && _cloneProblem == cloneProblem) return;
        _copied = bytes;
        _copiedWhat = what;
        _cloneProblem = cloneProblem;
        foreach (var name in new[] { nameof(CopiedText), nameof(CopiedTip), nameof(CopiedVisibility), nameof(CopiedSeverity) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Big enough to be worth a word. Under the threshold a copy is cheaper than the sentence about it.</summary>
    public Visibility CopiedVisibility => _copied >= DiskUsage.CopyWarnBytes ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The amount alone. The glyph says it is a copy and the tooltip says of what.</summary>
    public string CopiedText => DiskUsage.Human(_copied);

    /// <summary>
    /// Caution while there is something to do about it, Neutral when there is not. This volume cannot
    /// always clone, and a junction is not free either: it puts every worktree into one folder, which
    /// is the collision the private folder exists to avoid. Where neither is an improvement the chip
    /// still says the amount, because the amount is worth knowing, and says it without a colour.
    /// </summary>
    public ChipSeverity CopiedSeverity => _cloneProblem == null ? ChipSeverity.Caution : ChipSeverity.Neutral;

    public string CopiedTip
    {
        get
        {
            var head = $"{_copiedWhat} {(_copiedWhat.Contains(',') ? "are" : "is")} a full copy of the same folder in "
                       + $"the checkout, costing {DiskUsage.Human(_copied)} of this branch's own disk. ";
            return _cloneProblem == null
                ? head + "This root can block clone: a cloned folder shares every block with the checkout until one "
                       + "side writes, so it would cost nothing. Set the checkout's sharing to clone, then make the branch again."
                : head + "A junction would cost nothing, but every worktree then looks into the one folder and a build in "
                       + $"one changes it for all. A clone would cost nothing until one side writes, but {_cloneProblem}. "
                       + "So this is what the folder costs here, not something done wrong.";
        }
    }

    public int Ahead { get; set; }
    public string Branch { get; set; } = "";
    public string Path { get; set; } = "";
    public string Base { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Dirty { get; set; }
    /// <summary>How many tracked files are changed and not committed. The chip says the number, like the checkout's badge does.</summary>
    public int DirtyFiles { get; set; }
    public int Behind { get; set; }
    public bool Pending { get; set; }
    public bool Missing { get; set; }
    /// <summary>What stopped half way in this worktree, if anything: a rebase, or an import.</summary>
    public Replay Stopped { get; set; }

    public bool RebaseInProgress => Stopped != Replay.None;

    /// <summary>"rebase" or "import", for the sentences that have to name it. Empty when nothing stopped.</summary>
    public string StoppedVerb => Sg.Core.Conflicts.Verb(Stopped);

    public int Conflicts { get; set; }
    /// <summary>How the shared folders were made: "junction", "clone", "copy", or "" for a worktree older than the choice.</summary>
    public string Shared { get; set; } = "";
    /// <summary>Sets of changes put aside from this worktree, waiting to be taken back.</summary>
    public int Shelves { get; set; }
    /// <summary>Commits the backup does not hold: 0 when it holds the tip, -1 when the branch was never backed up.</summary>
    public int NotBackedUp { get; set; } = -1;
    public DateTimeOffset? BackedUp { get; set; }
    /// <summary>A backup repository is set, so the chip has something to say at all.</summary>
    public bool BackupOn { get; set; }

    public Visibility BackupVisibility => BackupOn && !Missing ? Visibility.Visible : Visibility.Collapsed;
    public string BackupText => NotBackedUp < 0 ? "not backed up"
        : NotBackedUp == 0 ? "backed up" + Ago(BackedUp)
        : NotBackedUp == 1 ? "1 commit not backed up"
        : NotBackedUp + " commits not backed up";
    public ChipSeverity BackupSeverity => NotBackedUp == 0 ? ChipSeverity.Neutral : ChipSeverity.Caution;
    public string BackupTip => NotBackedUp == 0
        ? "The backup repository holds every commit of this branch"
          + (BackedUp is { } t ? $", last confirmed {t.LocalDateTime:yyyy-MM-dd HH:mm}" : "") + ". Uncommitted changes go with the next backup."
        : NotBackedUp < 0
            ? "This branch has never been sent to the backup repository. The timer sends it, or open Backup on the checkout."
            : "Commits on this branch that the backup does not hold yet. The timer sends them, or open Backup on the checkout.";

    /// <summary>" just now", " 5 min ago", " 3 h ago", " 4 days ago", with the leading space; empty for null.</summary>
    internal static string Ago(DateTimeOffset? when)
    {
        if (when == null) return "";
        var d = DateTimeOffset.Now - when.Value;
        return d.TotalMinutes < 1 ? " just now"
            : d.TotalMinutes < 60 ? $" {(int)d.TotalMinutes} min ago"
            : d.TotalHours < 48 ? $" {(int)d.TotalHours} h ago"
            : $" {(int)d.TotalDays} days ago";
    }

    public string ShelvedText => Shelves == 1 ? "1 shelved" : Shelves + " shelved";
    public Visibility ShelvedVisibility => Shelves > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ShelvedDescription => Shelves == 0
        ? "Changes taken out of this worktree and kept, to put back later. Nothing is waiting right now."
        : $"{Shelves} set(s) of changes taken out of this worktree and kept. Open to read one and write it back.";

    /// <summary>
    /// Every property the card reads that is worked out from the state above. They are raised together
    /// because they change together: one read of the root sets all of the state at once, and a card that
    /// repainted its badge but not the sentence under it would be lying for the length of a frame.
    /// </summary>
    static readonly string[] Painted =
    {
        nameof(CardOpacity), nameof(NextAction),
        nameof(Behind), nameof(BehindTip), nameof(BehindVisibility), nameof(Conflicts), nameof(ConflictTip), nameof(ResolveVisibility),
        nameof(PendingVisibility), nameof(Shelves), nameof(ShelvedTip), nameof(ShelvedVisibility), nameof(ShelvedDescription),
        nameof(DirtyFiles), nameof(DirtyTip), nameof(DirtyVisibility), nameof(MissingVisibility), nameof(RebaseVisibility),
        nameof(Ahead), nameof(AheadTip), nameof(AheadSeverity), nameof(AheadVisibility),
        nameof(BackupText), nameof(BackupVisibility), nameof(BackupSeverity), nameof(BackupTip),
        nameof(PrimaryGlyph), nameof(PrimaryText), nameof(PrimaryStyle), nameof(PrimaryTip),
        nameof(SplitVisibility), nameof(PlainVisibility), nameof(PushEnabled), nameof(PushStyle),
    };

    /// <summary>
    /// Takes the state of a freshly read row without becoming a different object. The card bound to this
    /// row stays the card: its hover, its keyboard focus, the size it has already measured and the chip
    /// that says what it costs all survive, and only what actually changed is repainted.
    ///
    /// Replacing the bound list instead was one line, and it destroyed every card in it for one field on
    /// one worktree - including the card the user had open with the pointer on a row inside it.
    /// </summary>
    /// <summary>
    /// What the size walk already found, carried onto a row that is replacing this one. A worktree that
    /// was measured has been measured: making the new card say "measuring..." and drop the chips that say
    /// what it costs, only to be told the same numbers a thread hop later, is a blink that says nothing.
    /// </summary>
    public void CarryMeasured(WorktreeRow was)
    {
        IsExpanded = was.IsExpanded;
        SizeText = was.SizeText;
        Reclaimable = was.Reclaimable;
        _copied = was._copied;
        _copiedWhat = was._copiedWhat;
        _cloneProblem = was._cloneProblem;
    }

    public void Update(WorktreeRow n)
    {
        Base = n.Base;
        Detail = n.Detail;
        Dirty = n.Dirty;
        DirtyFiles = n.DirtyFiles;
        Behind = n.Behind;
        Pending = n.Pending;
        Missing = n.Missing;
        Stopped = n.Stopped;
        Conflicts = n.Conflicts;
        Ahead = n.Ahead;
        BaseRevision = n.BaseRevision;
        Shared = n.Shared;
        Shelves = n.Shelves;
        NotBackedUp = n.NotBackedUp;
        BackedUp = n.BackedUp;
        BackupOn = n.BackupOn;
        Repaint();
    }

    /// <summary>
    /// Every worked-out property again, from the top. Also what a Windows theme switch needs: AheadSeverity,
    /// PrimaryStyle and their like hand back a brush or a style taken out of the app's resources, and the
    /// one they took belongs to the theme that was on at the time. Nothing re-reads them on its own.
    /// </summary>
    public void Repaint()
    {
        foreach (var p in Painted) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// Everything this row puts on screen, size aside. A refresh that lands on the same answer touches
    /// nothing at all: no repaint, and no work for the bindings to do.
    /// </summary>
    public bool SameAs(WorktreeRow o) =>
        Branch == o.Branch && Path == o.Path && Base == o.Base && Detail == o.Detail
        && Dirty == o.Dirty && DirtyFiles == o.DirtyFiles && Behind == o.Behind && Pending == o.Pending && Missing == o.Missing
        && Stopped == o.Stopped && Conflicts == o.Conflicts && Ahead == o.Ahead
        && BaseRevision == o.BaseRevision && Shelves == o.Shelves
        && NotBackedUp == o.NotBackedUp && BackedUp == o.BackedUp && BackupOn == o.BackupOn;

    /// <summary>
    /// Whether Push is the thing to do next here, worked out from state the row already holds. Exactly
    /// one action on a card may be the loud one: six worktrees used to show six accent Push buttons,
    /// and one of them was on a card in the middle of a stopped rebase, where a push cannot work.
    /// </summary>
    public bool PushReady => !Missing && !RebaseInProgress && !Dirty && Ahead > 0;

    /// <summary>Pushing is possible at all. Nothing to send, or a stopped rebase, means it is not.</summary>
    public bool PushEnabled => !Missing && !RebaseInProgress && Ahead > 0;

    public Style? PushStyle => PushReady ? Application.Current.Resources["AccentButtonStyle"] as Style : null;

    /// <summary>The revision the snapshot this branch was born from is at. The bar over the cards names it.</summary>
    public long BaseRevision { get; set; }

    /// <summary>
    /// What this worktree wants next, in one sentence. The badges say what is true; this says what to
    /// do about it. A card that said "0 commit(s) ahead" was telling the user to work it out themselves.
    /// </summary>
    public string NextAction =>
        Missing ? "The folder is gone. Remove the branch, or put the folder back."
        : RebaseInProgress ? (Conflicts == 1
            ? $"The {StoppedVerb} stopped on 1 file. Resolve it to continue."
            : $"The {StoppedVerb} stopped on {Conflicts} files. Resolve them to continue.")
        : Pending ? "A push stopped half way. Fix the cause and push again."
        : Dirty && Behind > 0 ? $"Commit or discard the {DirtyFilesText}, then rebase onto the newer snapshot."
        : Dirty ? $"{DirtyFilesText[..1].ToUpperInvariant()}{DirtyFilesText[1..]} not committed. Commit them, or discard them."
        : Behind > 0 ? (Behind == 1
            ? "1 snapshot behind. Rebase to build on the latest SVN."
            : $"{Behind} snapshots behind. Rebase to build on the latest SVN.")
        : Ahead > 0 ? (Ahead == 1
            ? "1 commit ready. Push to SVN when you are."
            : $"{Ahead} commits ready. Push to SVN when you are.")
        : "Level with svn/" + Base + ".";

    /// <summary>
    /// How loudly this card is asking. The overview sorts on it, so whatever wants the user is at the
    /// top instead of wherever git happened to list it. 8 is a worktree with nothing to say.
    /// </summary>
    public int Rank =>
        Missing ? 1
        : RebaseInProgress ? 2
        : Pending ? 3
        : Dirty && Behind > 0 ? 4
        : Dirty ? 5
        : Behind > 0 ? 6
        : Ahead > 0 ? 7
        : 8;

    /// <summary>Anything but settled. A settled card recedes rather than competing for the eye.</summary>
    public bool Wants => Rank < 8;

    /// <summary>
    /// The one button beside the name: the thing NextAction says to do. A settled card offers the log,
    /// which is the only thing there is to do with a branch that equals its snapshot.
    /// </summary>
    public WorktreeAction Primary => Rank switch
    {
        1 => WorktreeAction.Remove,
        2 => WorktreeAction.Resolve,
        3 or 7 => WorktreeAction.Push,
        4 or 5 => WorktreeAction.Commit,
        6 => WorktreeAction.Rebase,
        _ => WorktreeAction.Log,
    };

    public string PrimaryText => Primary switch
    {
        WorktreeAction.Remove => "Remove",
        WorktreeAction.Resolve => "Resolve conflicts",
        WorktreeAction.Push => "Push to SVN",
        WorktreeAction.Commit => "Commit",
        WorktreeAction.Rebase => "Rebase",
        _ => "Log",
    };

    public string PrimaryGlyph => Primary switch
    {
        WorktreeAction.Remove => "\uE74D",
        WorktreeAction.Resolve => "\uE90F",
        WorktreeAction.Push => "\uE898",
        WorktreeAction.Commit => "\uE73E",
        WorktreeAction.Rebase => "\uE8AB",
        _ => "\uE81C",
    };

    public string PrimaryTip => Primary switch
    {
        WorktreeAction.Remove => "Delete the worktree folder and the git branch. Asks first. What was pushed stays in SVN.",
        WorktreeAction.Resolve => "Open the conflict resolver: pick a version per file, then continue, skip the one it stopped on, or put it all back.",
        WorktreeAction.Push => "Review the commits and files, then send the branch to SVN: sync, rebase, one svn commit per repository. Asks before it commits.",
        WorktreeAction.Commit => "Pick changed files, see each diff, write a message, and commit to the git branch. Nothing goes to SVN.",
        WorktreeAction.Rebase => "Put the branch on top of the latest snapshot. A conflict opens the resolver.",
        _ => "The commits on the branch and the snapshots under it, with files and diffs.",
    };

    /// <summary>Accent while the card is asking for something. A card with nothing to say has no loud button.</summary>
    public Style? PrimaryStyle => Wants ? Application.Current.Resources["AccentButtonStyle"] as Style : null;

    public double CardOpacity => Wants ? 1.0 : 0.7;

    /// <summary>"1 file" or "N files": the tracked changes waiting for a commit. Untracked ones are not counted.</summary>
    string DirtyFilesText => DirtyFiles == 0 ? "new files" : DirtyFiles == 1 ? "1 file" : $"{DirtyFiles} files";

    // The chips on the card carry a glyph and a number and nothing else; the words are here, one hover
    // away. A chip that said "4 uncommitted files" beside a line that said "4 files not committed" and a
    // button that said Commit was one fact three times over, and the row ran out of width for it.
    public string DirtyTip => DirtyFiles == 0
        ? "Files that are not tracked yet. Commit adds them to the branch; Discard deletes them."
        : $"{DirtyFilesText[..1].ToUpperInvariant()}{DirtyFilesText[1..]} changed and not committed. Commit or discard them before a rebase or a push, or shelve them for later.";
    public string BehindTip => (Behind == 1 ? "1 snapshot" : $"{Behind} snapshots")
        + " taken since this branch was made or last rebased. Rebase to build on the latest SVN state.";
    public string ConflictTip => $"The {StoppedVerb} stopped on {(Conflicts == 1 ? "1 file" : $"{Conflicts} files")} in conflict. "
        + "Use Resolve to pick a version for each, then continue.";
    public string ShelvedTip => (Shelves == 1 ? "1 set" : $"{Shelves} sets")
        + " of changes taken out of this worktree and kept. They are not in the branch and not in SVN: open Shelved changes to write them back.";
    public string AheadTip => (Ahead == 1 ? "1 commit" : $"{Ahead} commits")
        + " on the branch that SVN does not have. Push to SVN sends them, one svn commit per repository"
        + (Dirty ? ", once the uncommitted changes are committed, discarded or shelved."
            : RebaseInProgress ? $", once the {StoppedVerb} is finished."
            : ".");
    /// <summary>Green when a push is the thing to do; plain while something else has to happen first.</summary>
    public ChipSeverity AheadSeverity => PushReady ? ChipSeverity.Success : ChipSeverity.Neutral;
    public Visibility AheadVisibility => Ahead > 0 && !Missing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BehindVisibility => Behind > 0 && !RebaseInProgress ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RebaseVisibility => RebaseInProgress ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ResolveVisibility => RebaseInProgress ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => Pending ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DirtyVisibility => Dirty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MissingVisibility => Missing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Commit is a split button whose other half shelves the same changes. Every other primary action
    /// is a plain button, so the card holds both and shows the one its state calls for.
    /// </summary>
    public Visibility SplitVisibility => Primary == WorktreeAction.Commit ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlainVisibility => Primary == WorktreeAction.Commit ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// A file in the push, log and conflict windows. It is checkable because the conflict window picks
/// several files at once, and a tree has no place to drag a selection across.
/// </summary>
public sealed class FileRow : CheckableRow
{
    public string Path { get; set; } = "";
    public string? OldPath { get; set; }
    public char Status { get; set; }
    /// <summary>The working copy of a pushed file; "" is the root.</summary>
    public string Wc { get; set; } = "";
    public override string Code => Status == '\0' ? "" : Status.ToString();
    public override string PathText => Path + (OldPath != null ? $"  (was {OldPath})" : "");
    public override string TreePath => Path;
    public override string Group => Wc;
}

/// <summary>A file row the list also decides about: the commit and revert windows tick these.</summary>
public abstract class CheckableRow : StatusRow
{
    bool _checked;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Raise(nameof(Checked));
        }
    }
}

/// <summary>A file in the git commit window, with a checkbox.</summary>
public sealed class ChangeRow : CheckableRow
{
    public StatusEntry Entry { get; set; } = null!;
    /// <summary>Both status letters when the file is only half staged, so the list says so where it stands.</summary>
    public override string Code => Entry.BothCodes;
    public override string PathText => Entry.Path + (Entry.OldPath != null ? $"  (was {Entry.OldPath})" : "");
    public override string TreePath => Entry.Path;
}

/// <summary>A local change in the checkout, with a checkbox.</summary>
public sealed class SvnChangeRow : CheckableRow
{
    public Ops.SvnChange Change { get; set; } = null!;
    public override string Code => Change.Code;
    public override string PathText => Change.Path;
    public override string TreePath => Change.Path;
    public override string Group => Change.Wc;
}

/// <summary>What the mark at the head of a revision row says. The SVN log and the monitor read the same
/// list of revisions for different reasons, so each one says which mark a row wears.</summary>
public enum RevMark
{
    /// <summary>Nothing to say about it.</summary>
    None,
    /// <summary>The monitor: newer than the revision this repository was last marked read at.</summary>
    Unread,
    /// <summary>The SVN log: newer than the working copy, a sync would bring it in.</summary>
    Behind,
    /// <summary>The SVN log: the revision the working copy is at.</summary>
    Current,
}

public sealed class SvnRevRow
{
    public long Revision { get; set; }
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string Subject { get; set; } = "";
    public SvnLogRevision Entry { get; set; } = null!;
    public RevMark Mark { get; set; }
    /// <summary>The working copy the revision came from; "" is the root. The SVN log tags rows with it.</summary>
    public string Group { get; set; } = "";
    /// <summary>Which of the eight repository colours this working copy wears, handed out in load order.</summary>
    public int RepoColor { get; set; }
    /// <summary>What the tag says about the working copy: its URL and how far the checkout is behind it.</summary>
    public string RepoTip { get; set; } = "";
    /// <summary>A list of one repository, like the monitor, leaves the tag off.</summary>
    public bool ShowRepo { get; set; }

    public string Repo => Group.Length == 0 ? "root" : Group;
    public Visibility RepoVisibility => ShowRepo ? Visibility.Visible : Visibility.Collapsed;
    public Brush? RepoBrush => ShowRepo ? Res("RepoBrush" + (RepoColor & 7)) : null;
    public Brush? RepoBackground => ShowRepo ? Res("RepoBackground" + (RepoColor & 7)) : null;

    public string RevText => "r" + Revision;

    /// <summary>Everything this row draws, so a refresh that changed nothing can keep the row it has.</summary>
    public bool SameAs(SvnRevRow o) =>
        Revision == o.Revision && Mark == o.Mark && Author == o.Author && Date == o.Date
        && Subject == o.Subject && Group == o.Group && ShowRepo == o.ShowRepo && RepoColor == o.RepoColor;
    /// <summary>What the row leaves out: the number, the date, and the subject when the column trims it.</summary>
    public string Tip => $"{RevText}   {Date}\n{Subject}";

    /// <summary>
    /// What a screen reader announces. Without it a list of these reads out the class name. A fold line
    /// is not a revision, so it says only what it stands for; a tagged row names its working copy.
    /// </summary>
    public override string ToString() => IsFold
        ? Subject
        : string.Join("  ", new[] { RevText, Date, ShowRepo ? Repo : "", Author, Subject }.Where(s => s.Length > 0));

    /// <summary>
    /// The merge window folds the revisions a working copy has already taken into one line, so the list
    /// is the work still on offer. Every other window leaves these alone.
    /// </summary>
    public bool IsFold { get; set; }

    /// <summary>Already merged into its working copy, so it lives under the fold.</summary>
    public bool Merged { get; set; }

    public bool Expanded { get; set; }
    public Action<SvnRevRow>? Toggled;
    public void Toggle() => Toggled?.Invoke(this);

    /// <summary>What is already taken fades behind what is still on offer.</summary>
    public double RowOpacity => IsFold ? 0.75 : Merged ? 0.5 : 1.0;

    /// <summary>A fold line borrows the mark slot for its chevron; it has no mark of its own.</summary>
    public string MarkGlyph => IsFold ? (Expanded ? "\uE70D" : "\uE76C") : Mark switch
    {
        RevMark.Unread => "\uE7E7",
        RevMark.Behind => "\uE896",
        RevMark.Current => "\uE73E",
        _ => "",
    };

    public Brush? MarkBrush => Mark switch
    {
        RevMark.Unread => Res("SystemFillColorAttentionBrush"),
        RevMark.Behind => Res("SystemFillColorCautionBrush"),
        RevMark.Current => Res("SystemFillColorSuccessBrush"),
        _ => null,
    };

    public string MarkTip => Mark switch
    {
        RevMark.Unread => "New since you last marked this repository as read.",
        RevMark.Behind => "Newer than the working copy. A sync brings it in.",
        RevMark.Current => "The working copy is at this revision.",
        _ => "",
    };

    public Visibility MarkVisibility => IsFold || Mark != RevMark.None ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A revision you have not taken in yet stands out; one you have is plain.</summary>
    public Windows.UI.Text.FontWeight Weight =>
        Mark is RevMark.Unread or RevMark.Behind ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>
/// One line of a blamed file. The mark is the SVN revision that put it there, or the short sha of the
/// branch commit that did. Runs of one revision share a colour, so a change reads as a block.
/// </summary>
public sealed class BlameRow : StatusRow
{
    public BlameLine Line { get; set; } = null!;
    public int Colour { get; set; }

    public override string Code => "";
    public override string PathText => Line.Text;
    public override string TreePath => "";

    public string Mark => Line.Mark;
    public string Author => Line.Author;
    public string Number => Line.Number.ToString();
    public string Text => Line.Text.Length == 0 ? " " : Line.Text.Replace("\t", "    ");

    /// <summary>A line the branch changed has no revision yet, and says so rather than showing a number.</summary>
    public Brush? MarkBrush => (Brush?)Application.Current.Resources["RepoBrush" + (Colour & 7)];
    public Brush? MarkBackground => (Brush?)Application.Current.Resources["RepoBackground" + (Colour & 7)];

    public string Tip => Line.Local
        ? $"{Line.Mark}   {Line.Author}   {Msg.When(Line.Date)}\n{Msg.Subject(Line.Summary)}\nOn the branch: SVN has not seen this line."
        : $"{Line.Mark}   {Line.Author}   {Msg.When(Line.Date)}";

    public override string ToString() => $"{Number}  {Mark}  {Author}  {Text}";
}

/// <summary>A changed path of one SVN revision, in the SVN log and the monitor.</summary>
public sealed class SvnPathRow : StatusRow
{
    public SvnChangedPath Path { get; set; } = null!;
    public override string Code => Path.Action;
    public override string PathText => Path.Path + (Path.CopyFrom != null ? $"  (from {Path.CopyFrom})" : "");
    public override string TreePath => Path.Path;
}

public sealed class CommitRow
{
    public string Sha { get; set; } = "";
    public string Date { get; set; } = "";
    public string Author { get; set; } = "";
    public string Subject { get; set; } = "";
    public bool IsSnapshot { get; set; }

    /// <summary>
    /// The push window only: this commit is above the one the push stops at, so it waits for the next
    /// push. The row dims and wears a tag; every other window leaves this alone.
    /// </summary>
    public bool Staying { get; set; }

    /// <summary>
    /// The one line a run of snapshots folds into. A branch sits on a long tail of them, one per sync,
    /// each titled with every external's revision and URL; that is the log of SVN, not of the branch,
    /// so it is one line until it is asked for.
    /// </summary>
    public bool IsGroup { get; set; }

    /// <summary>How many snapshots the group line stands for.</summary>
    public int GroupCount { get; set; }

    /// <summary>The group line is open, so the snapshots under it are on the list too.</summary>
    public bool Expanded { get; set; }

    /// <summary>The window rebuilds its list when a group line is pressed.</summary>
    public Action<CommitRow>? Toggled;

    public void Toggle() => Toggled?.Invoke(this);

    /// <summary>
    /// What SVN said fades behind what the branch did. The two are the same list and the eye has to
    /// tell them apart before it reads a word of either.
    /// </summary>
    public double RowOpacity => Staying ? 0.45 : IsSnapshot && !IsGroup ? 0.5 : IsGroup ? 0.75 : 1.0;

    public Visibility StaysVisibility => Staying ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChevronVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    public string ChevronGlyph => Expanded ? "\uE70D" : "\uE76C";

    public string ShortSha => Sha.Length >= 8 ? Sha[..8] : Sha;
    /// <summary>What the row leaves out: the sha, the date, and the subject when the column trims it.</summary>
    public string Tip => $"{ShortSha}   {Date}\n{Subject}";

    /// <summary>
    /// What a screen reader announces for the line. A list of these used to read out the class name,
    /// because that is what a row with no name of its own falls back to.
    /// </summary>
    public override string ToString() => $"{ShortSha}  {Date}  {Author}  {Subject}";
    /// <summary>Branch commits are bold, snapshots are not.</summary>
    public Windows.UI.Text.FontWeight Weight => IsSnapshot ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;

    public static CommitRow From(LogEntry e, bool snapshot) => new()
    {
        Sha = e.Sha,
        Date = e.Date,
        Author = snapshot ? "svn" : e.Author,
        Subject = e.Subject,
        IsSnapshot = snapshot,
    };
}

/// <summary>What the Push window can run to clear a failed check, without the user leaving it.</summary>
public enum PushFix
{
    /// <summary>Nothing the window can do. The user has to change the branch itself.</summary>
    None,
    /// <summary>Commit what is uncommitted, on the branch.</summary>
    Commit,
    /// <summary>Finish the rebase that stopped.</summary>
    Resolve,
    /// <summary>Deal with the edits made directly in the checkout.</summary>
    CheckoutChanges,
    /// <summary>Put the branch on the latest snapshot now, rather than during the push.</summary>
    Rebase,
    /// <summary>Take the checkout's edits on exactly the colliding files out of the way, and keep them.</summary>
    Shelve,
}

/// <summary>
/// One failed push pre-check, as the Push window lists it. A check that names a problem the window
/// can solve carries the action too: a refusal used to cost a window close, a hunt and a reopen,
/// while the user held the blocker in their head.
/// </summary>
/// <summary>
/// One working copy the push commits to, as the message flyout lists it. Every one commits under the
/// shared message until its switch is turned, and then under a text of its own, seeded from the shared
/// one at the moment the switch turns.
/// </summary>
public sealed class PushRepoRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    bool _custom;
    string _message = "";

    public string Wc { get; set; } = "";
    public int Files { get; set; }
    public int RepoColor { get; set; }
    /// <summary>The server refuses a shorter message. From the root's config.</summary>
    public int Minimum { get; set; }
    /// <summary>The shared message, read when the switch turns on, so the own text starts from it.</summary>
    internal Func<string>? Shared { get; set; }
    /// <summary>The page's button follows every row.</summary>
    public event Action? Changed;

    public string Repo => Wc.Length == 0 ? "root" : Wc;
    public string FilesText => Files == 1 ? "1 file" : $"{Files} files";
    public Brush? RepoBrush => Res("RepoBrush" + (RepoColor & 7));
    public Brush? RepoBackground => Res("RepoBackground" + (RepoColor & 7));

    public bool Custom
    {
        get => _custom;
        set
        {
            if (_custom == value) return;
            _custom = value;
            if (value && _message.Trim().Length == 0) Message = Shared?.Invoke() ?? "";
            Raise(nameof(Custom));
            Raise(nameof(CustomVisibility));
            Raise(nameof(CountText));
            Raise(nameof(CountBrush));
            Changed?.Invoke();
        }
    }

    public Visibility CustomVisibility => _custom ? Visibility.Visible : Visibility.Collapsed;

    public string Message
    {
        get => _message;
        set
        {
            if (_message == value) return;
            _message = value;
            Raise(nameof(Message));
            Raise(nameof(CountText));
            Raise(nameof(CountBrush));
            Changed?.Invoke();
        }
    }

    int Length => Push.CleanMessage(_message).Length;

    /// <summary>The message this working copy commits under is long enough, or it uses the shared one.</summary>
    public bool Ok => !_custom || Length >= Minimum;

    public string CountText => !_custom ? "" : Length >= Minimum ? $"{Length} characters" : $"{Length} of {Minimum} characters";
    public Brush? CountBrush => Res(Ok ? "TextFillColorTertiaryBrush" : "SystemFillColorCautionBrush");

    static Brush? Res(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : null;

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class CheckRow
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public PushFix Fix { get; set; }
    /// <summary>The words on the button. Empty leaves the row as a statement, which some checks are.</summary>
    public string ActionText { get; set; } = "";
    public string ActionGlyph { get; set; } = "";
    public string ActionTip { get; set; } = "";
    public Visibility ActionVisibility => ActionText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// A second way out of the same check, quieter than the first. The collision has two: put the
    /// checkout's edits aside, which takes one press, or go and read them, which is the longer way.
    /// </summary>
    public PushFix SecondFix { get; set; }
    public string SecondText { get; set; } = "";
    public string SecondGlyph { get; set; } = "";
    public string SecondTip { get; set; } = "";
    public Visibility SecondVisibility => SecondText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The paths the check named, for a fix that acts on exactly them and nothing else.</summary>
    public List<string> Paths { get; set; } = new();
}
