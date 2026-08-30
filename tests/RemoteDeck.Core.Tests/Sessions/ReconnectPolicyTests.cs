using RemoteDeck.Core.Sessions;

namespace RemoteDeck.Core.Tests.Sessions;

public sealed class ReconnectPolicyTests
{
    [Fact]
    public void Delays_are_two_five_ten_thirty_and_sixty_seconds()
    {
        var seconds = ReconnectPolicy.Delays.Select(d => d.TotalSeconds).ToArray();

        Assert.Equal(new double[] { 2, 5, 10, 30, 60 }, seconds);
        Assert.Equal(ReconnectPolicy.MaxAttempts, ReconnectPolicy.Delays.Count);
    }

    [Fact]
    public void DelayFor_maps_attempts_one_to_five_and_nothing_else()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ReconnectPolicy.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(60), ReconnectPolicy.DelayFor(5));
        Assert.Null(ReconnectPolicy.DelayFor(6));
        Assert.Null(ReconnectPolicy.DelayFor(0));
    }

    [Fact]
    public void ShouldReconnect_is_true_for_transient_network_codes()
    {
        foreach (var reason in new[] { 264, 516, 772, 1028, 1796, 2308 })
        {
            Assert.True(ReconnectPolicy.ShouldReconnect(reason), $"code {reason} should reconnect");
        }
    }

    [Fact]
    public void ShouldReconnect_is_false_for_intentional_disconnections()
    {
        foreach (var reason in new[] { 0, 1, 2, 3 })
        {
            Assert.False(ReconnectPolicy.ShouldReconnect(reason), $"code {reason} must not reconnect");
        }
    }

    [Fact]
    public void ShouldReconnect_is_false_for_authentication_failures()
    {
        foreach (var reason in new[] { 2055, 3335, 8455 })
        {
            Assert.False(ReconnectPolicy.ShouldReconnect(reason), $"code {reason} must not reconnect");
        }
    }

    [Fact]
    public void ShouldReconnect_is_false_for_name_resolution_failures()
    {
        foreach (var reason in new[] { 260, 520, 2052 })
        {
            Assert.False(ReconnectPolicy.ShouldReconnect(reason), $"code {reason} must not reconnect");
        }
    }

    [Fact]
    public void ShouldReconnect_is_false_for_security_and_licensing_failures()
    {
        foreach (var reason in new[] { 1286, 2056 })
        {
            Assert.False(ReconnectPolicy.ShouldReconnect(reason), $"code {reason} must not reconnect");
        }
    }
}
