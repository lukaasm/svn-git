using System.Xml;

namespace Sg.Core;

/// <summary>
/// One line of a file, and who last changed it. Sha is set for a line the branch itself changed;
/// Revision for one that came from SVN. A line has one or the other, never both.
/// </summary>
public sealed record BlameLine(int Number, string Text, string Author, string Date, string Summary, string? Sha, long? Revision)
{
    /// <summary>What the gutter says: the SVN revision, or the short sha of the commit on the branch.</summary>
    public string Mark => Revision.HasValue ? "r" + Revision.Value
        : Sha is { Length: >= 8 } ? Sha[..8]
        : Sha ?? "";

    /// <summary>The branch changed this line, so it is not in SVN yet.</summary>
    public bool Local => Sha != null;
}

/// <summary>The blamed file, and what it had to say about reading it.</summary>
public sealed class BlameResult
{
    public string Path = "";
    public List<BlameLine> Lines = new();

    /// <summary>Lines the branch changed, which SVN has never seen.</summary>
    public int LocalLines => Lines.Count(l => l.Local);

    public List<string> Warnings = new();
}

/// <summary>
/// Who last touched each line, answered the way this bridge has to answer it. A worktree's git history
/// is one commit per sync, so plain `git blame` says "wc r266" for nearly every line, which names the
/// sync and not the change. Here the branch's own commits are answered by git, and every line that came
/// in with a snapshot is handed to `svn blame`, which knows the revision and the person.
/// </summary>
public static class Blame
{
    /// <summary>Who last changed each line of a file in the checkout. Straight from svn.</summary>
    public static BlameResult OfCheckout(SgRoot root, CheckoutConfig co, string relPath)
    {
        var abs = PathUtil.Join(co.Path, relPath);
        if (!File.Exists(abs)) throw new SgException("no such file in the checkout: " + relPath);
        var text = ReadLines(abs);
        var svn = root.Svn.Blame(co.Path, relPath);
        var res = new BlameResult { Path = relPath };
        for (var i = 0; i < text.Count; i++)
        {
            var b = i < svn.Count ? svn[i] : null;
            res.Lines.Add(b == null
                ? new BlameLine(i + 1, text[i], "", "", "", null, null)
                : new BlameLine(i + 1, text[i], b.Author, b.Date, "", null, b.Revision));
        }
        if (svn.Count == 0) res.Warnings.Add("svn had nothing to say about this file. It may be added but not committed yet.");
        return res;
    }

    /// <summary>
    /// Who last changed each line of a file in a worktree. A line the branch changed is answered by git;
    /// a line that came in with a snapshot is answered by svn, through the line it had in that snapshot.
    /// git blame gives that original line number, so the two answers line up without guessing.
    /// </summary>
    public static BlameResult OfWorktree(SgRoot root, string worktree, string relPath)
    {
        var git = root.Git;
        worktree = git.Toplevel(worktree);
        var abs = PathUtil.Join(worktree, relPath);
        if (!File.Exists(abs)) throw new SgException("no such file in the worktree: " + relPath);

        var branch = git.CurrentBranch(worktree);
        var co = Ops.BaseCheckout(root, branch);
        var snapRef = root.SnapshotRef(co);

        var res = new BlameResult { Path = relPath };
        var lines = git.BlamePorcelain(worktree, relPath);
        if (lines.Count == 0) return res;

        // Every commit the snapshots are made of. Anything else is the branch's own work.
        var snapshots = git.RevList(worktree, snapRef).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // svn is asked once, for the same file in the checkout, and only when a line needs it.
        List<SvnBlameLine>? svn = null;
        var wanted = lines.Any(l => snapshots.Contains(l.Sha));
        if (wanted)
        {
            var inCheckout = PathUtil.Join(co.Path, relPath);
            if (File.Exists(inCheckout))
            {
                try { svn = root.Svn.Blame(co.Path, relPath); }
                catch (SgException ex) { res.Warnings.Add("svn blame failed for " + relPath + ": " + ex.Message.Split('\n')[0]); }
            }
            else res.Warnings.Add(relPath + " is not in the checkout, so the lines that came from SVN cannot be dated.");
        }

        foreach (var l in lines)
        {
            if (snapshots.Contains(l.Sha))
            {
                // The line as it was in the snapshot, which is the line svn blamed in the checkout.
                var b = svn != null && l.OriginalLine >= 1 && l.OriginalLine <= svn.Count ? svn[l.OriginalLine - 1] : null;
                res.Lines.Add(b == null
                    ? new BlameLine(l.Number, l.Text, "svn", "", "came in with a snapshot", null, null)
                    : new BlameLine(l.Number, l.Text, b.Author, b.Date, "", null, b.Revision));
            }
            else
            {
                res.Lines.Add(new BlameLine(l.Number, l.Text, l.Author, l.Date, l.Summary, l.Sha, null));
            }
        }
        return res;
    }

    static List<string> ReadLines(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var lines = text.Split('\n').ToList();
        // A file that ends with a newline has no empty last line to blame.
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}

/// <summary>One line as svn blames it. A line svn has never seen has no revision.</summary>
public sealed record SvnBlameLine(int Number, long Revision, string Author, string Date);

/// <summary>One line as git blames it, with the line it had in the commit that last changed it.</summary>
public sealed record GitBlameLine(int Number, int OriginalLine, string Sha, string Author, string Date, string Summary, string Text);
