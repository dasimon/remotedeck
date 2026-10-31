using RemoteDeck.Core.Updates;

namespace RemoteDeck.Core.Tests.Updates;

/// <summary>Finding the expected hash of a file in a <c>SHA256SUMS</c> listing.</summary>
public sealed class Sha256SumsTests
{
    private const string Hash = "bee805430933bdf830ed5f168e2484309c285eb9ef941f8b9d780af4164c6b07";

    [Theory]
    // What release.yml produces: binary marker, no end of line.
    [InlineData(Hash + " *RemoteDeck.exe")]
    // Text-mode form of sha256sum.
    [InlineData(Hash + "  RemoteDeck.exe")]
    [InlineData(Hash + " *RemoteDeck.exe\n")]
    [InlineData(Hash + " *RemoteDeck.exe\r\n")]
    public void The_hash_of_the_file_is_found(string content)
    {
        Assert.Equal(Hash, Sha256Sums.Find(content, "RemoteDeck.exe"));
    }

    [Fact]
    public void An_upper_case_hash_is_returned_in_lower_case()
    {
        Assert.Equal(Hash, Sha256Sums.Find(Hash.ToUpperInvariant() + " *RemoteDeck.exe", "RemoteDeck.exe"));
    }

    [Fact]
    public void The_right_line_is_picked_among_several()
    {
        var other = new string('a', 64);
        var content = $"{other} *RemoteDeck.exe.bak\r\n{other} *Other.zip\r\n{Hash} *RemoteDeck.exe\r\n";

        Assert.Equal(Hash, Sha256Sums.Find(content, "RemoteDeck.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("not a listing at all")]
    // A name that only starts or ends the same is another file.
    [InlineData(Hash + " *RemoteDeck.exe.bak")]
    [InlineData(Hash + " *MyRemoteDeck.exe")]
    // 63 characters.
    [InlineData("ee805430933bdf830ed5f168e2484309c285eb9ef941f8b9d780af4164c6b07 *RemoteDeck.exe")]
    // 65 characters.
    [InlineData("0" + Hash + " *RemoteDeck.exe")]
    // Not hexadecimal ('g').
    [InlineData("gee805430933bdf830ed5f168e2484309c285eb9ef941f8b9d780af4164c6b07 *RemoteDeck.exe")]
    // No separator between hash and name.
    [InlineData(Hash + "RemoteDeck.exe")]
    public void Anything_else_gives_nothing(string content)
    {
        Assert.Null(Sha256Sums.Find(content, "RemoteDeck.exe"));
    }
}
