namespace RemoteDeck.Core.Sessions;

/// <summary>What a session should do about a drop.</summary>
public enum ReconnectVerdict
{
    /// <summary>Try again after the schedule's delay.</summary>
    Retry = 0,

    /// <summary>
    /// Stop: the profile this connection needs is not up, and no number of attempts will change
    /// that. The user is told which tunnel, and their next click decides — the retry loop raises
    /// none, even for a connection that opted in to <see cref="VpnConsent"/>.
    /// </summary>
    VpnDown = 1,

    /// <summary>Stop: either the code was never retryable, or the schedule is spent.</summary>
    Fail = 2,
}

/// <summary>The verdict, and the delay to wait when it is <see cref="ReconnectVerdict.Retry"/>.</summary>
public readonly record struct ReconnectDecision(ReconnectVerdict Verdict, TimeSpan Delay);

/// <summary>
/// Decides what happens after a session drops, given the disconnect code, how many attempts have
/// been spent, and the state of the VPN profile the connection names.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and in <c>Core</c>, for the same reason as <see cref="VpnRequirement"/>: what can be wrong
/// here is the order of the questions, and that is exactly what a test can hold.
/// </para>
/// <para>
/// It exists because of a real failure. <see cref="ReconnectPolicy"/> alone never looked at the
/// tunnel, and on a connection that names a profile the most likely cause of a network drop <em>is
/// the tunnel</em>. The schedule therefore spent 2 + 5 + 10 + 30 + 60 seconds on five attempts
/// against a host it could not reach, and ended on a message about RDP instead of one about the
/// VPN — the one thing the user could have acted on.
/// </para>
/// </remarks>
public static class ReconnectGate
{
    /// <param name="reason">The <c>OnDisconnected</c> code.</param>
    /// <param name="attempt">Attempts already spent; 0 on the first drop.</param>
    /// <param name="vpn">The state of the profile the connection names, or
    /// <see cref="VpnState.NotRequired"/> when it names none — which is most connections, and which
    /// leaves the schedule exactly as it was.</param>
    public static ReconnectDecision Decide(int reason, int attempt, VpnState vpn)
    {
        // The code comes first, deliberately. A tunnel that is down is not what refused a session
        // dropped for a reason the policy never retried — a rejected credential, a server that
        // closed the door — and blaming it would send the user to fix the wrong thing.
        if (!ReconnectPolicy.ShouldReconnect(reason))
        {
            return new ReconnectDecision(ReconnectVerdict.Fail, TimeSpan.Zero);
        }

        // A network drop, and the profile this connection needs is not up: that is the explanation,
        // and it is worth more than five attempts that cannot succeed. No delay is returned because
        // nothing is scheduled — there is no countdown to show and nothing to wait for.
        if (vpn == VpnState.NotConnected)
        {
            return new ReconnectDecision(ReconnectVerdict.VpnDown, TimeSpan.Zero);
        }

        if (attempt >= ReconnectPolicy.MaxAttempts || ReconnectPolicy.DelayFor(attempt + 1) is not { } delay)
        {
            return new ReconnectDecision(ReconnectVerdict.Fail, TimeSpan.Zero);
        }

        return new ReconnectDecision(ReconnectVerdict.Retry, delay);
    }
}
