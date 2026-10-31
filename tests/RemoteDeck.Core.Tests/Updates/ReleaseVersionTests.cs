using RemoteDeck.Core.Updates;

namespace RemoteDeck.Core.Tests.Updates;

/// <summary>Reading and comparing the versions of releases and of the running build.</summary>
public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.9.0", 0, 9, 0)]
    [InlineData("v1.20.300", 1, 20, 300)]
    // Leading zeros are digits like any other.
    [InlineData("v01.2.3", 1, 2, 3)]
    public void A_release_tag_is_a_v_and_three_numbers(string tag, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParseTag(tag, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("V0.9.0")]
    [InlineData("v0.9")]
    [InlineData("v0.9.0-beta")]
    [InlineData("v0.9.0.1")]
    [InlineData(" v0.9.0")]
    [InlineData("v0.9.0 ")]
    [InlineData("v0.9.0\n")]
    [InlineData("v0.-9.0")]
    [InlineData("v0.+9.0")]
    [InlineData("v0..0")]
    [InlineData("v٠.٩.٠")]
    // Too big for an int: refused, not wrapped around.
    [InlineData("v0.9.99999999999")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_a_release_tag(string? tag)
    {
        Assert.False(ReleaseVersion.TryParseTag(tag, out var version));
        Assert.Equal(default, version);
    }

    [Theory]
    // The build metadata after '+' (the commit) is not part of the version.
    [InlineData("0.8.0+7859f064", 0, 8, 0)]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("0.9.0+", 0, 9, 0)]
    public void The_running_version_is_read_without_its_build_metadata(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParseInformational(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    // A pre-release is never compared with a release.
    [InlineData("0.9.0-beta+abc")]
    [InlineData("0.9.0-beta")]
    [InlineData("0.9")]
    [InlineData("v0.9.0")]
    [InlineData("+abc")]
    [InlineData("")]
    [InlineData(null)]
    public void A_version_that_is_not_plain_numbers_is_refused(string? text)
    {
        Assert.False(ReleaseVersion.TryParseInformational(text, out var version));
        Assert.Equal(default, version);
    }

    [Theory]
    [InlineData(0, 10, 0, 0, 9, 9)]
    [InlineData(1, 0, 0, 0, 99, 99)]
    [InlineData(0, 9, 1, 0, 9, 0)]
    public void Versions_are_compared_number_by_number_not_as_text(
        int major, int minor, int patch, int otherMajor, int otherMinor, int otherPatch)
    {
        var newer = new ReleaseVersion(major, minor, patch);
        var older = new ReleaseVersion(otherMajor, otherMinor, otherPatch);

        Assert.True(newer > older);
        Assert.True(older < newer);
        Assert.False(older > newer);
        Assert.False(newer < older);
        Assert.True(newer.CompareTo(older) > 0);
    }

    [Fact]
    public void An_equal_version_is_not_greater_nor_smaller()
    {
        var a = new ReleaseVersion(0, 9, 0);
        var b = new ReleaseVersion(0, 9, 0);

        Assert.False(a > b);
        Assert.False(a < b);
        Assert.True(a >= b);
        Assert.True(a <= b);
        Assert.Equal(0, a.CompareTo(b));
    }

    [Fact]
    public void It_prints_as_dotted_numbers() =>
        Assert.Equal("0.9.0", new ReleaseVersion(0, 9, 0).ToString());
}
