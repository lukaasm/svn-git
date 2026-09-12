using System.Text;
using Microsoft.UI.Xaml.Controls;
using Sg.Core;

namespace Sg.App;

/// <summary>
/// Discarding, with a way back. Discard was the one action on the two commit pages with nothing behind
/// it: the change was never committed anywhere, so once the file was written back there was nothing to
/// take it from. Now a whole-file discard is a shelf first - the same shelf Shelve makes, titled with
/// the moment - and the file goes back the same way it would have; a block discard keeps the text it
/// wrote over. Either way the bar over the page offers Undo until the bar is closed, and a discard
/// shelf nobody asked back for in a week is dropped the next time one is made.
/// </summary>
public static class Discards
{
    /// <summary>What a discard's shelf is called, before the moment it was made.</summary>
    public const string TitlePrefix = "discarded ";

    /// <summary>How long a discard waits on the shelf before it is dropped for good.</summary>
    static readonly TimeSpan Keep = TimeSpan.FromDays(7);

    /// <summary>
    /// Takes the picked paths out of the working copy onto a shelf, which puts each file back the way
    /// the last commit, or BASE, has it. What a shelf cannot hold - a file carrying an svn property
    /// change, a path the checkout skips - goes to plain, which discards it the old way, with nothing
    /// behind it. The shelf comes back, or null when nothing went onto one.
    /// </summary>
    public static async Task<ShelfInfo?> ShelveAsync(StatusStrip pane, string folder, IReadOnlyList<string> paths, Action<IReadOnlyList<string>> plain)
    {
        var root = Session.Require();
        ShelfInfo? shelf = null;
        await Runner.Run(pane, "discard", () =>
        {
            var rest = paths;
            try
            {
                var saved = Shelf.Save(root, folder, paths, TitlePrefix + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                shelf = saved.Shelf;
                rest = saved.LeftBehind;
            }
            catch (SgException ex)
            {
                // A shelf refuses whole, before it writes anything: nothing among the picked files it can
                // hold, or one of them in conflict. The old discard takes all of them, and the log says why.
                root.Log.Info("not kept before the discard: " + ex.Message.Split('\n')[0]);
            }
            if (rest.Count > 0) plain(rest);
            Prune(root);
        });
        return shelf;
    }

    /// <summary>A discard shelf a week old was not wanted back. It goes, so the shelf list is not a graveyard.</summary>
    static void Prune(SgRoot root)
    {
        foreach (var s in Shelf.List(root))
        {
            if (!s.Title.StartsWith(TitlePrefix, StringComparison.Ordinal) || DateTimeOffset.Now - s.Created < Keep) continue;
            try { Shelf.Drop(root, s.Id); }
            catch (SgException ex) { root.Log.Warn($"could not drop the old discard {s.Id}: {ex.Message}"); }
        }
    }

    /// <summary>The shelf goes back where it came from, and is dropped with it. False when it could not.</summary>
    public static Func<Task<bool>> Restore(StatusStrip pane, ShelfInfo shelf) => async () =>
    {
        var root = Session.Require();
        var r = await Runner.Run(pane, "undo discard", () => Shelf.Restore(root, shelf.Id));
        if (r == null) return false;
        pane.Append($"{r.Written.Count + r.Deleted.Count + r.Merged.Count} file(s) back from the shelf"
                    + (r.Merged.Count > 0 ? ", merged into what changed meanwhile: " + string.Join(", ", r.Merged) : ""));
        return true;
    };

    /// <summary>
    /// The text a block discard wrote over goes back, for as long as the file still holds exactly what
    /// the discard left: over anything newer it would be a second loss, so then it refuses and says so.
    /// </summary>
    public static Func<Task<bool>> Rewrite(StatusStrip pane, string abs, string before, string after, Encoding encoding) => () =>
        Runner.Run(pane, "undo discard", () =>
        {
            var file = TextFile.Read(abs);
            if (file.Text != after)
                throw new SgException("the file changed since the block was discarded, so the old text would write over something newer. Nothing was written.");
            TextFile.Write(abs, before, encoding);
        });

    /// <summary>
    /// Says what went, on the page's own bar, with the one button that brings it back. The bar is the
    /// same one a commit reports on, so whoever writes a result there next clears the button.
    /// </summary>
    public static void Announce(InfoBar bar, string what, string where, Func<Task<bool>> undo, Func<Task> reload)
    {
        var button = new Button { Content = "Undo" };
        ToolTipService.SetToolTip(button, "Put the discarded change back where it was.");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            var ok = await undo();
            if (ok) Clear(bar);
            else button.IsEnabled = true;
            await reload();
        };
        bar.Severity = InfoBarSeverity.Informational;
        bar.Message = $"Discarded {what}. {where}";
        bar.ActionButton = button;
        bar.IsOpen = true;
    }

    /// <summary>Takes the Undo off a bar that is about to say something else, or has nothing to undo any more.</summary>
    public static void Clear(InfoBar bar)
    {
        bar.ActionButton = null;
        bar.IsOpen = false;
    }
}
