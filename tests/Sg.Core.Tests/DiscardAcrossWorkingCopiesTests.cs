namespace Sg.Core.Tests;

/// <summary>A discard of edits picked in the checkout root and in its externals reverts every one of them, in one go.</summary>
public sealed class DiscardAcrossWorkingCopiesTests
{
    static readonly string[] Picked = ["CMakeLists.txt", "fort/dev/game.cpp", "fort/tools/tool.py", "schmetterling/engine.cpp"];

    static void EditEverywhere(Fixture f)
    {
        Fixture.Put(f.Checkout, "CMakeLists.txt", "root edit\n");
        Fixture.Put(f.Checkout, "schmetterling/engine.cpp", "engine edit\n");
        Fixture.Put(f.Checkout, "fort/dev/game.cpp", "game edit\n");
        Fixture.Put(f.Checkout, "fort/tools/tool.py", "tool edit\n");
    }

    static List<string> Changed(Fixture f) => Ops.CheckoutChanges(f.Root, f.Co).Select(c => c.Path).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void A_discard_onto_a_recovery_shelf_reverts_every_working_copy_and_undo_brings_all_back()
    {
        using var f = new Fixture(); f.Setup();
        EditEverywhere(f);
        Assert.Equal(Picked, Changed(f));

        var saved = Shelf.Save(f.Root, f.Checkout, Picked, "discarded across working copies");
        Assert.Empty(saved.LeftBehind);
        Assert.Equal(Picked.Length, saved.Shelf.Count);
        Assert.Empty(Changed(f));

        Shelf.Restore(f.Root, saved.Shelf.Id);
        Assert.Equal(Picked, Changed(f));
        Assert.Equal("engine edit\n", File.ReadAllText(Path.Combine(f.Checkout, "schmetterling", "engine.cpp")));
    }

    [Fact]
    public void A_permanent_discard_reverts_every_working_copy()
    {
        using var f = new Fixture(); f.Setup();
        EditEverywhere(f);
        Ops.SvnRevert(f.Root, f.Co, Picked, deleteUnversioned: true);
        Assert.Empty(Changed(f));
        Assert.Equal("int game = 1;\n", File.ReadAllText(Path.Combine(f.Checkout, "fort", "dev", "game.cpp")));
    }
}
