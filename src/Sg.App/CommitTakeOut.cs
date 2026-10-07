using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// One commit out of the middle of a branch, put on the shelf or thrown away, from wherever the commit
/// is listed: Log and Push to SVN ask the same question, run the same core call, and leave the same
/// bar behind, with Undo on it while nothing else has happened on the branch.
/// </summary>
public static class CommitTakeOut
{
    /// <summary>
    /// Asks, then takes the commit out. Shelving asks for the shelf's name, starting from the commit's
    /// subject; discarding asks once and says what keeps it. Null back means the reader cancelled, or it
    /// failed and the strip has already said why.
    /// </summary>
    public static async Task<Ops.TakeOutResult?> RunAsync(object owner, StatusStrip pane, string worktree, CommitRow row, string? branch, bool shelve)
    {
        var on = string.IsNullOrEmpty(branch) ? "the branch" : branch;
        var root = Session.Require();
        if (shelve)
        {
            var name = await ShelfActions.AskName(owner,
                $"Take {row.ShortSha} \"{row.Subject}\" off {on} and put what it changed on the shelf, the way git stash puts work aside. "
                + "The commits over it are replayed onto the one under it, so each of them gets a new sha. Nothing goes to the server.",
                row.Subject);
            if (name == null) return null;
            return await Runner.Run(pane, "shelve commit", () => Ops.Stash(root, worktree, row.Sha, name));
        }
        if (!await Dialogs.Confirm(owner, "Discard this commit",
                $"Take {row.ShortSha} \"{row.Subject}\" out of {on}? The commits over it are replayed onto the one under it, so each of them gets a new sha. "
                + "Nothing goes to the server.\n\nActivity keeps the branch as it was and can make a new branch of it, and Undo on the bar puts it back while nothing else has happened here.",
                "Discard"))
            return null;
        return await Runner.Run(pane, "discard commit", () => Ops.Discard(root, worktree, row.Sha));
    }

    /// <summary>
    /// What happened, on the page's own bar, with Undo on it. Undo puts the branch back on the tip it had
    /// and takes the shelf away again; once the branch has moved on it refuses and says Activity still has it.
    /// </summary>
    public static void Show(InfoBar bar, StatusStrip pane, string worktree, Ops.TakeOutResult r, string server, Func<Task> reload)
    {
        var what = $"{Short(r.Commit)} \"{r.Subject}\"";
        var replayed = r.Replayed == 0 ? "" : r.Replayed == 1 ? " The commit over it was replayed." : $" The {r.Replayed} commits over it were replayed.";
        bar.Severity = InfoBarSeverity.Success;
        bar.Message = r.Shelf != null
            ? $"{what} is off {r.Branch} and on the shelf as {r.Shelf}.{replayed} Put it back from Shelved changes. Nothing went to {server}."
            : $"{what} is out of {r.Branch}.{replayed} Activity keeps the branch as it was. Nothing went to {server}.";
        var undo = new Button { Content = "Undo" };
        ToolTipService.SetToolTip(undo, "Put the commit back where it was" + (r.Shelf != null ? " and take it off the shelf" : "")
                                        + ". Only while nothing else has happened on the branch since.");
        undo.Click += async (_, _) =>
        {
            undo.IsEnabled = false;
            var ok = await Runner.Run(pane, "undo", () => Ops.PutBack(Session.Require(), worktree, r));
            // The bar may be saying something newer by now; that is not this button's to overwrite.
            if (ReferenceEquals(bar.ActionButton, undo))
            {
                bar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                bar.Message = ok
                    ? $"{what} is back on {r.Branch}, where it was."
                    : $"{what} was not put back. See the log; Activity still keeps the branch as it was.";
                bar.IsOpen = true;
                // A refusal can pass, a task holding the root for one, and PutBack checks the tip again,
                // so the button stays for another try. Once it worked there is nothing left to undo.
                if (ok) bar.ActionButton = null;
                else undo.IsEnabled = true;
            }
            await reload();
        };
        bar.ActionButton = undo;
        bar.IsOpen = true;
    }

    static string Short(string sha) => sha.Length >= 8 ? sha[..8] : sha;
}
