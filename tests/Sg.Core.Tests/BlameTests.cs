using Xunit;

namespace Sg.Core.Tests;

/// <summary>
/// Who last touched each line. A worktree's git history is one commit per sync, so plain git blame
/// answers "wc r1" for nearly every line; these say the tool gives the SVN answer instead, and keeps
/// git's answer for the lines the branch itself changed.
/// </summary>
public sealed class BlameTests : IDisposable
{
    readonly Fixture f = new();
    public void Dispose() => f.Dispose();

    const string Original = "one\ntwo\nthree\nfour\n";

    /// <summary>
    /// Commits a file into SVN through a second working copy, so it has a real author and revision.
    /// The revision comes from the commit itself: the working copy root keeps the one it had.
    /// </summary>
    long PutInSvn(string text, string message)
    {
        var wc = f.OtherWc(f.GameUrl + "/branches/fort/dev");
        File.WriteAllText(Path.Combine(wc, "blamed.cpp"), text);
        f.Svn.Run(wc, "add", "--non-interactive", "blamed.cpp");
        return f.Svn.Commit(wc, new[] { "blamed.cpp" }, message);
    }

    [Fact]
    public void A_file_in_the_checkout_is_blamed_by_svn()
    {
        f.Setup();
        PutInSvn(Original, "the first four lines");
        f.Root.Svn.Update(f.Checkout);

        var r = Blame.OfCheckout(f.Root, f.Co, "fort/dev/blamed.cpp");

        Assert.Equal(4, r.Lines.Count);
        Assert.Equal(new[] { "one", "two", "three", "four" }, r.Lines.Select(l => l.Text));
        Assert.All(r.Lines, l => Assert.NotNull(l.Revision));
        Assert.All(r.Lines, l => Assert.False(l.Local));
        Assert.All(r.Lines, l => Assert.StartsWith("r", l.Mark));
    }

    [Fact]
    public void A_line_the_branch_changed_is_blamed_by_git_and_the_rest_by_svn()
    {
        f.Setup();
        var rev = PutInSvn(Original, "the first four lines");
        f.Root.Svn.Update(f.Checkout);
        Ops.Sync(f.Root, f.Co);

        var wt = Ops.Branch(f.Root, "feature-blame", f.Co).Path;
        f.Root.Git.Config("user.name", "Branch Author");
        f.Root.Git.Config("user.email", "branch@localhost");
        var file = Path.Combine(wt, "fort", "dev", "blamed.cpp");
        File.WriteAllText(file, "one\nCHANGED HERE\nthree\nfour\n");
        f.Root.Git.AddPaths(wt, new[] { "fort/dev/blamed.cpp" });
        f.Root.Git.CommitAsUser(wt, "change the second line\n");

        var r = Blame.OfWorktree(f.Root, wt, "fort/dev/blamed.cpp");

        Assert.Equal(4, r.Lines.Count);
        // The branch's own line: git's answer, with the sha and the commit's subject.
        var mine = r.Lines[1];
        Assert.Equal("CHANGED HERE", mine.Text);
        Assert.True(mine.Local, "the changed line belongs to the branch");
        Assert.Null(mine.Revision);
        Assert.Equal("Branch Author", mine.Author);
        Assert.Equal("change the second line", mine.Summary);

        // Every other line came in with a snapshot, and svn says which revision put it there.
        foreach (var i in new[] { 0, 2, 3 })
        {
            Assert.False(r.Lines[i].Local, $"line {i + 1} came from SVN");
            Assert.Equal(rev, r.Lines[i].Revision);
            Assert.Equal("r" + rev, r.Lines[i].Mark);
        }
        Assert.Equal(1, r.LocalLines);
    }

    [Fact]
    public void The_svn_answer_survives_lines_being_added_above_it()
    {
        f.Setup();
        var rev = PutInSvn(Original, "the first four lines");
        f.Root.Svn.Update(f.Checkout);
        Ops.Sync(f.Root, f.Co);

        var wt = Ops.Branch(f.Root, "feature-insert", f.Co).Path;
        f.Root.Git.Config("user.name", "Branch Author");
        f.Root.Git.Config("user.email", "branch@localhost");
        var file = Path.Combine(wt, "fort", "dev", "blamed.cpp");
        // Two lines on top push everything down; the old lines must still name their own revision.
        File.WriteAllText(file, "added one\nadded two\none\ntwo\nthree\nfour\n");
        f.Root.Git.AddPaths(wt, new[] { "fort/dev/blamed.cpp" });
        f.Root.Git.CommitAsUser(wt, "two lines on top\n");

        var r = Blame.OfWorktree(f.Root, wt, "fort/dev/blamed.cpp");

        Assert.Equal(6, r.Lines.Count);
        Assert.True(r.Lines[0].Local);
        Assert.True(r.Lines[1].Local);
        Assert.Equal("one", r.Lines[2].Text);
        Assert.Equal(rev, r.Lines[2].Revision);
        Assert.Equal("four", r.Lines[5].Text);
        Assert.Equal(rev, r.Lines[5].Revision);
    }

    [Fact]
    public void Two_svn_revisions_are_told_apart_line_by_line()
    {
        f.Setup();
        var first = PutInSvn(Original, "the first four lines");
        var second = PutInSvn("one\ntwo\nthree\nfour\nfive\n", "a fifth line, later");
        Assert.NotEqual(first, second);
        f.Root.Svn.Update(f.Checkout);

        var r = Blame.OfCheckout(f.Root, f.Co, "fort/dev/blamed.cpp");

        Assert.Equal(5, r.Lines.Count);
        Assert.Equal(first, r.Lines[0].Revision);
        Assert.Equal(second, r.Lines[4].Revision);
    }

    [Fact]
    public void A_file_that_is_not_there_says_so()
    {
        f.Setup();
        Assert.Throws<SgException>(() => Blame.OfCheckout(f.Root, f.Co, "fort/dev/nothing.cpp"));
    }

    [Fact]
    public void A_line_nobody_has_committed_yet_carries_no_revision()
    {
        f.Setup();
        PutInSvn(Original, "the first four lines");
        f.Root.Svn.Update(f.Checkout);
        // A local edit in the checkout: svn has never seen this line.
        var file = Path.Combine(f.Checkout, "fort", "dev", "blamed.cpp");
        File.WriteAllText(file, "one\ntwo\nthree\nfour\nnot committed yet\n");

        var r = Blame.OfCheckout(f.Root, f.Co, "fort/dev/blamed.cpp");

        Assert.Equal(5, r.Lines.Count);
        Assert.Equal("not committed yet", r.Lines[4].Text);
        Assert.True(r.Lines[4].Revision is null or 0, "svn has no revision for a line it has not seen");
    }
}
