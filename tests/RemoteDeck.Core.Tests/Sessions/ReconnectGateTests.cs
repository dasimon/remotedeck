using RemoteDeck.Core.Sessions;

namespace RemoteDeck.Core.Tests.Sessions;

/// <summary>
/// What to do when a session drops: retry, stop because the tunnel it needs is down, or give up.
///
/// The gate exists because of a real failure. On a connection that names a VPN profile, the most
/// likely cause of a network drop is the tunnel itself — and the retry schedule spent 107 seconds
/// on five attempts against a host it could not reach, to arrive at a message about RDP rather
/// than about the VPN.
/// </summary>
public sealed class ReconnectGateTests
{
    /// <summary>264 is <c>disconnectReasonConnectionTimedOut</c>: a code the policy retries.</summary>
    private const int Transient = 264;

    /// <summary>1032 is not in the transient set — a code the policy has never retried.</summary>
    private const int Hard = 1032;

    [Fact]
    public void A_connection_with_no_VPN_keeps_the_schedule_it_always_had()
    {
        for (var attempt = 0; attempt < ReconnectPolicy.MaxAttempts; attempt++)
        {
            var decision = ReconnectGate.Decide(Transient, attempt, VpnState.NotRequired);

            Assert.Equal(ReconnectVerdict.Retry, decision.Verdict);
            Assert.Equal(ReconnectPolicy.DelayFor(attempt + 1), decision.Delay);
        }
    }

    [Fact]
    public void A_tunnel_that_is_up_changes_nothing()
    {
        var decision = ReconnectGate.Decide(Transient, 0, VpnState.Connected);

        Assert.Equal(ReconnectVerdict.Retry, decision.Verdict);
        Assert.Equal(TimeSpan.FromSeconds(2), decision.Delay);
    }

    [Fact]
    public void A_network_drop_with_the_tunnel_down_stops_at_once()
    {
        // At the first attempt and at the last: the point is not to spend the schedule finding out
        // what one call already knows.
        Assert.Equal(ReconnectVerdict.VpnDown, ReconnectGate.Decide(Transient, 0, VpnState.NotConnected).Verdict);
        Assert.Equal(ReconnectVerdict.VpnDown, ReconnectGate.Decide(Transient, 4, VpnState.NotConnected).Verdict);
    }

    [Fact]
    public void Stopping_for_the_tunnel_asks_for_no_delay()
    {
        // Nothing is scheduled: there is no countdown to show, and the user's next click is what
        // decides. The retry loop never raises a tunnel, whatever the connection opted in to.
        Assert.Equal(TimeSpan.Zero, ReconnectGate.Decide(Transient, 0, VpnState.NotConnected).Delay);
    }

    [Fact]
    public void A_failure_that_is_not_a_network_drop_is_never_blamed_on_the_tunnel()
    {
        // The tunnel may well be down, but it is not what refused this session, and saying so would
        // send the user to fix the wrong thing.
        Assert.Equal(ReconnectVerdict.Fail, ReconnectGate.Decide(Hard, 0, VpnState.NotConnected).Verdict);
        Assert.Equal(ReconnectVerdict.Fail, ReconnectGate.Decide(Hard, 0, VpnState.Connected).Verdict);
    }

    [Fact]
    public void The_schedule_still_runs_out()
    {
        Assert.Equal(ReconnectVerdict.Fail, ReconnectGate.Decide(Transient, ReconnectPolicy.MaxAttempts, VpnState.Connected).Verdict);
        Assert.Equal(ReconnectVerdict.Fail, ReconnectGate.Decide(Transient, ReconnectPolicy.MaxAttempts + 3, VpnState.NotRequired).Verdict);
    }

    [Fact]
    public void The_delays_are_the_policys_own()
    {
        // The gate decides whether to retry; the schedule stays where it was.
        Assert.Equal(TimeSpan.FromSeconds(2), ReconnectGate.Decide(Transient, 0, VpnState.NotRequired).Delay);
        Assert.Equal(TimeSpan.FromSeconds(60), ReconnectGate.Decide(Transient, 4, VpnState.NotRequired).Delay);
    }

    [Fact]
    public void A_verdict_that_is_not_a_retry_carries_no_delay()
    {
        Assert.Equal(TimeSpan.Zero, ReconnectGate.Decide(Hard, 0, VpnState.Connected).Delay);
        Assert.Equal(TimeSpan.Zero, ReconnectGate.Decide(Transient, ReconnectPolicy.MaxAttempts, VpnState.Connected).Delay);
    }
}
