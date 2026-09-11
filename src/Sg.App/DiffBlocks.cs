using Sg.Core;

namespace Sg.App;

/// <summary>
/// The buttons that act on one block of the diff on show. Both commit windows put the same ones over
/// their diff, so the ids, the icons and the words they take are written once here. A "block" is a
/// hunk of the unified patch the window read, which is the same cut git stages and svn reverts by.
/// Discard is the word for work that was never committed, wherever it is; Revert is for what was.
/// </summary>
public static class DiffBlocks
{
    public const string Save = "save";
    public const string Stage = "stage";
    public const string Unstage = "unstage";
    public const string Discard = "discard";
    public const string DiscardToBase = "discard-base";
    public const string Undo = "undo";

    public static DiffView.DiffAction SaveAction => new(Save, "Save", "\uE74E",
        "Write what is in the editor to the file on disk. The diff is read again after it.", "ctrl+s");

    public static DiffView.DiffAction UndoAction => new(Undo, "Undo edits", "\uE7A7",
        "Throw away what was typed here and read the file off disk again.");

    public static DiffView.DiffAction StageAction => new(Stage, "Stage", "\uE710",
        "Put this block in the index. A commit then holds it and leaves the rest of the file for later.", "ctrl+shift+s");

    public static DiffView.DiffAction UnstageAction => new(Unstage, "Unstage", "\uE738",
        "Take this block out of the index. The file on disk is untouched.", "ctrl+u");

    public static DiffView.DiffAction DiscardAction => new(Discard, "Discard", "\uE7A7",
        "Put this block back the way the left side has it, and write the file. The rest of the file stays as it is.", "ctrl+r");

    public static DiffView.DiffAction DiscardToBaseAction => new(DiscardToBase, "Discard", "\uE7A7",
        "Put this block back the way BASE has it, and write the file. The rest of the file stays as it is.", "ctrl+r");

    /// <summary>Every block the cursor or the selection touches, in the order the patch names them.</summary>
    public static List<PatchHunk> Under(PatchFile? file, DiffView.LineRange? selection) =>
        file == null || selection == null ? new() : Patch.HunksIn(file, selection.Value.First, selection.Value.Last);

    /// <summary>What the button says about what it would act on: the plain verb, one block named, or a count.</summary>
    public static string Label(string verb, IReadOnlyList<PatchHunk> blocks) =>
        blocks.Count == 0 ? verb
        : blocks.Count == 1 ? verb + " " + blocks[0].Describe()
        : $"{verb} {blocks.Count} blocks";
}
