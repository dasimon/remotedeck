using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Tests.Model;

/// <summary>Which of the palette's hues a group gets.</summary>
public sealed class GroupHueTests
{
    [Fact]
    public void A_group_keeps_its_hue_across_runs()
    {
        // Pinned values: string.GetHashCode is randomised per process, and a group whose colour
        // changed at every start would teach the user nothing.
        Assert.Equal(GroupHue.Of("FDC", 8), GroupHue.Of("FDC", 8));
        Assert.Equal(4, GroupHue.Of("FDC", 8));
        Assert.Equal(2, GroupHue.Of("Maison", 8));
    }

    [Fact]
    public void Case_and_surrounding_spaces_do_not_change_the_hue()
    {
        // The group combo is free text: "Prod", "prod" and "Prod " are the same group to the user.
        Assert.Equal(GroupHue.Of("Prod", 8), GroupHue.Of(" prod ", 8));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_group_has_no_hue(string? group) => Assert.Null(GroupHue.Of(group, 8));

    [Fact]
    public void Every_hue_is_inside_the_palette()
    {
        foreach (var name in new[] { "a", "b", "Prod", "Recette", "Dev", "Clients", "Labo", "Maison", "FDC" })
        {
            Assert.InRange(GroupHue.Of(name, 8)!.Value, 0, 7);
        }
    }
}
