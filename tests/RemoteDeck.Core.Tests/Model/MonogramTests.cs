using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Tests.Model;

/// <summary>The letters that stand for a connection on its badge.</summary>
public sealed class MonogramTests
{
    [Theory]
    // Names in a fleet share their beginning ("FDC-VM-…"); the badge takes the end, where they differ.
    [InlineData("FDC-VM-WIN07", "W7")]
    [InlineData("FDC-VM-WIN10", "W10")]
    [InlineData("FDC-VM-AD002", "A2")]
    [InlineData("FDCSQL00003", "F3")]
    [InlineData("FDC-HYPERV-03", "H3")]
    [InlineData("FDC-WIN02", "W2")]
    // No number at the end: the first two letters of the last word.
    [InlineData("FDCSQL-RECETTE", "RE")]
    [InlineData("FDC-VM-WEB", "WE")]
    [InlineData("nas parnassium", "PA")]
    [InlineData("x", "X")]
    [InlineData("été", "ÉT")]
    // Only digits: the number itself, at most three of its last digits.
    [InlineData("10.199.199.1", "1")]
    [InlineData("server-0042", "S42")]
    public void Takes_the_distinctive_end_of_the_name(string name, string expected) =>
        Assert.Equal(expected, Monogram.Of(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--")]
    [InlineData(null)]
    public void A_name_with_nothing_to_draw_gives_nothing(string? name) =>
        Assert.Equal("", Monogram.Of(name));
}
