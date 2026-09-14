namespace RemoteDeck.Core.Sessions;

/// <summary>What the caller should do about the VPN before a session goes ahead.</summary>
public enum VpnConsentVerdict
{
    /// <summary>Connect: nothing is needed, or the caller may not ask and nobody agreed to a dial.</summary>
    Proceed = 0,

    /// <summary>The tunnel is down: say which one and let the user decide whether it goes up.</summary>
    Ask = 1,

    /// <summary>The tunnel is down and the connection already agreed: raise it, and say so.</summary>
    RaiseWithoutAsking = 2,
}

/// <summary>
/// Decides who gets to raise a tunnel: nobody, the user through a question, or the connection's own
/// opt-in, given once in the editor.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and in <c>Core</c>, like <see cref="VpnRequirement"/>: the part that can go wrong is which
/// of three answers wins, and the one that matters most — nothing goes up unless the connection
/// opted in — is an invariant a test can hold for every combination.
/// </para>
/// <para>
/// The retry loop never comes here. A session that drops because its tunnel dropped still stops
/// (<see cref="ReconnectGate"/>): a VPN that fell mid-session may have been brought down on purpose,
/// and raising it again behind the user's back would be the "VPN nobody knows is up" this product
/// refuses. The opt-in covers the moment the user starts something — a connect, a reconnect, a
/// workspace — and nothing after it.
/// </para>
/// </remarks>
public static class VpnConsent
{
    /// <param name="state">The state of the profile the connection names.</param>
    /// <param name="autoRaise">The connection's opt-in: raise the tunnel without asking.</param>
    /// <param name="mayAsk">Whether the caller can put a question to the user. False when mounting a
    /// workspace, which opens its sessions in series and must not stop on one dialog per session.</param>
    public static VpnConsentVerdict Decide(VpnState state, bool autoRaise, bool mayAsk)
    {
        if (state != VpnState.NotConnected)
        {
            return VpnConsentVerdict.Proceed;
        }

        if (autoRaise)
        {
            return VpnConsentVerdict.RaiseWithoutAsking;
        }

        // Down, and nobody agreed to a dial. Where no question can be asked, the session goes ahead
        // and fails the ordinary way — the behaviour a workspace mount had before the opt-in existed.
        return mayAsk ? VpnConsentVerdict.Ask : VpnConsentVerdict.Proceed;
    }
}
