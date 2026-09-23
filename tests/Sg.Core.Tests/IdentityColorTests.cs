namespace Sg.Core.Tests;

public sealed class IdentityColorTests
{
    [Theory]
    [InlineData("lukaa", " LUKAA ")]
    [InlineData("Alice", "aLICE")]
    [InlineData("Review agent", " Review Agent ")]
    public void Equivalent_names_use_the_same_palette_slot(string first, string second)
    {
        Assert.Equal(IdentityColor.Index(first), IdentityColor.Index(second));
    }

    [Fact]
    public void Identity_is_independent_of_order_and_uses_the_shared_eight_color_palette()
    {
        var names = Enumerable.Range(0, 100).Select(i => "reviewer" + i).ToArray();
        var slots = names.ToDictionary(n => n, IdentityColor.Index);
        foreach (var name in names.Reverse()) Assert.Equal(slots[name], IdentityColor.Index(name));
        Assert.Equal(8, slots.Values.Distinct().Count());
        Assert.All(slots.Values, value => Assert.InRange(value, 0, 7));
    }
}
