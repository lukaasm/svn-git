namespace Sg.Core;

/// <summary>Notes that tell an AI agent how to behave in a worktree. Written on every sg branch. Excluded from git.</summary>
public static class AgentNotes
{
    public static void Write(string worktree, CheckoutConfig co, IEnumerable<string> shared, SharedMode mode = SharedMode.Junction)
    {
        var j = shared.ToList();
        var list = string.Join(", ", j.Select(x => "`" + x + "`"));
        var note = mode == SharedMode.Junction
            ? "8. These folders are junctions into the SVN checkout. Do not edit files in them: " + list
            : "8. These folders are private copies of the checkout's, outside git. `sg rebase` brings them in step with the checkout again, and edits in them do not survive that: " + list;
        var md = $"""
            # sg worktree

            This folder is a git worktree managed by `sg`. The master repository is SVN, not git.
            The base ref is `svn/{co.Name}`. It mirrors the SVN checkout at `{co.Path}`.

            Rules:

            1. Use normal git here: `git status`, `git diff`, `git add`, `git commit`, `git log`.
            2. Never run `git push`, `git pull`, `git fetch`, or `git svn`. They do not work here.
            3. To get the latest SVN state under your work, run `sg rebase`.
            4. When a rebase or an import stops on conflicts, `sg resolve` says what stopped and what is in conflict.
               Then `sg resolve ours|theirs <path>...` to keep one whole version, or edit the file and
               `sg resolve resolved <path>...`. Then `sg resolve continue`, or `sg resolve skip` to drop that one
               commit, or `sg resolve abort` to put it all back.
            5. Do not push to SVN. When the work is committed and ready, say so. A human runs `sg push`.
            6. `sg status --json` shows every checkout and worktree.
            7. Do not create files with reserved Windows names: nul, con, aux, prn, com1-9, lpt1-9.
            {(j.Count > 0 ? note : "")}

            """;
        File.WriteAllText(Path.Combine(worktree, "CLAUDE.local.md"), md);
        var rules = Path.Combine(worktree, ".cursor", "rules");
        Directory.CreateDirectory(rules);
        File.WriteAllText(Path.Combine(rules, "sg.mdc"), "---\ndescription: sg worktree rules\nalwaysApply: true\n---\n" + md);
    }
}
