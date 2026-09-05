namespace RemoteDeck.Core.Sessions;

/// <summary>What a connection's VPN requirement amounts to right now.</summary>
public enum VpnState
{
    /// <summary>The connection names no profile. Most connections, and the shell does nothing.</summary>
    NotRequired = 0,

    /// <summary>The profile it names is up. The session opens with no interruption at all.</summary>
    Connected = 1,

    /// <summary>The profile it names is down. The shell says so and offers to raise it.</summary>
    NotConnected = 2,
}

/// <summary>
/// Decides whether a connection can go ahead, given the Windows VPN profiles that are up.
///
/// Pure, and in <c>Core</c>, for the same reason as <see cref="WorkspacePlan"/>: what can be wrong
/// here is the comparison — blank means "no requirement", the name is matched loosely — and that is
/// exactly the part a test can hold. Enumerating the live profiles is a P/Invoke and stays in the
/// application.
/// </summary>
/// <remarks>
/// RemoteDeck stores no VPN credential and never will. It reads a state and, at most, asks Windows
/// to dial a profile Windows already knows — the credentials stay where the user put them, in the
/// profile itself. Adding a second secret store beside the DPAPI vault, for tunnels that are usually
/// behind MFA anyway, would buy nothing and cost the one thing this application is careful about.
/// </remarks>
public static class VpnRequirement
{
    /// <param name="requiredProfile">The Windows VPN profile the connection names, or <c>null</c>
    /// / blank when it names none.</param>
    /// <param name="connectedProfiles">The profiles that are up right now. Never <c>null</c>: a
    /// caller whose enumeration failed must say so rather than pass an empty set, which would read
    /// as "the tunnel is down" and make the shell offer to raise one that may already be up.</param>
    public static VpnState Check(string? requiredProfile, IReadOnlySet<string> connectedProfiles)
    {
        ArgumentNullException.ThrowIfNull(connectedProfiles);

        if (string.IsNullOrWhiteSpace(requiredProfile))
        {
            return VpnState.NotRequired;
        }

        // Trimmed and case-insensitive: the name is typed by hand in the editor, while Windows shows
        // it with whatever casing its creator chose. Making the user match it exactly would turn a
        // convenience into a trap.
        var wanted = requiredProfile.Trim();
        foreach (var profile in connectedProfiles)
        {
            if (string.Equals(profile?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return VpnState.Connected;
            }
        }

        return VpnState.NotConnected;
    }
}
