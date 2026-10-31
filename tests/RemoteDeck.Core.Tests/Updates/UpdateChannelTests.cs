using RemoteDeck.Core.Updates;

namespace RemoteDeck.Core.Tests.Updates;

/// <summary>Which repository, if any, a channel setting allows the app to update from.</summary>
public sealed class UpdateChannelTests
{
    [Theory]
    [InlineData("release", "dasimon/remotedeck")]
    [InlineData("test", "dasimon/remotedeck-update-test")]
    public void A_known_channel_points_at_its_repository(string channel, string repository) =>
        Assert.Equal(repository, UpdateChannel.RepositoryFor(channel));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" release")]
    [InlineData("Release")]
    [InlineData("TEST")]
    [InlineData("dev")]
    public void Anything_else_means_no_update_at_all(string? channel) =>
        Assert.Null(UpdateChannel.RepositoryFor(channel));

    [Fact]
    public void The_channel_names_are_the_ones_the_setting_uses()
    {
        Assert.Equal("release", UpdateChannel.Release);
        Assert.Equal("test", UpdateChannel.Test);
    }
}
