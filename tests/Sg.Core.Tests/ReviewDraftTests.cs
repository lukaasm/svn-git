namespace Sg.Core.Tests;

public sealed class ReviewDraftTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "sg-drafts-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    static ReviewDraft Draft(string body = "Explain this") => new(Guid.NewGuid().ToString("N"), "file.cs", body, "original", 2, 3, "version");

    [Fact]
    public void Draft_survives_restart_with_range_and_incomplete_input()
    {
        var store = new ReviewDrafts(directory);
        var draft = Draft() with { Last = null };
        var saved = store.Write("worktree", "comment:file.cs", draft, null);
        Assert.Equal(saved, new ReviewDrafts(directory).Read("worktree", "comment:file.cs"));
        Assert.Equal(draft, Assert.Single(store.List("worktree")).Value);
    }

    [Fact]
    public void Discard_is_scoped_to_worktree_file_and_action()
    {
        var store = new ReviewDrafts(directory);
        var draft = Draft();
        var saved = store.Write("a", "comment:file.cs", draft, null);
        store.Write("b", "comment:file.cs", draft, null);
        store.Write("a", "thread:reply", draft, null);
        store.Write("a", "comment:file.cs", null, saved.Revision);
        Assert.Null(store.Read("a", "comment:file.cs").Draft);
        Assert.Equal(draft, store.Read("b", "comment:file.cs").Draft);
        Assert.Equal(draft, store.Read("a", "thread:reply").Draft);
    }

    [Fact]
    public void Another_window_cannot_overwrite_or_discard_newer_text()
    {
        var first = new ReviewDrafts(directory);
        var second = new ReviewDrafts(directory);
        var initial = first.Write("a", "reply", Draft(), null);
        var latest = second.Write("a", "reply", initial.Draft! with { Body = "Newer text" }, initial.Revision);
        Assert.Throws<SgException>(() => first.Write("a", "reply", initial.Draft, initial.Revision));
        Assert.Throws<SgException>(() => first.Write("a", "reply", null, initial.Revision));
        Assert.Equal(latest, first.Read("a", "reply"));
    }

    [Fact]
    public void Failed_write_retains_previous_text_and_corruption_is_not_overwritten()
    {
        var store = new ReviewDrafts(directory);
        var initial = store.Write("a", "comment:file.cs", Draft(), null);
        Assert.Throws<SgException>(() => store.Write("a", "comment:file.cs", Draft(new string('a', 32001)), initial.Revision));
        Assert.Equal(initial, store.Read("a", "comment:file.cs"));
        var path = Assert.Single(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories));
        File.WriteAllText(path, "corrupt original");
        Assert.Throws<SgException>(() => store.Read("a", "comment:file.cs"));
        Assert.Throws<SgException>(() => store.Write("a", "comment:file.cs", Draft(), initial.Revision));
        Assert.Equal("corrupt original", File.ReadAllText(path));
    }
}
