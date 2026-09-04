using RemoteDeck.Core.Sessions;

namespace RemoteDeck.Core.Tests.Sessions;

/// <summary>
/// The decision behind "this connection needs a VPN": given what a connection asks for and which
/// Windows VPN profiles are up right now, should the shell connect, offer to raise the tunnel, or
/// say nothing at all.
///
/// Pure, and in Core, for the same reason as <see cref="WorkspacePlan"/>: the part that can be wrong
/// is the comparison, not the P/Invoke that lists the profiles.
/// </summary>
public sealed class VpnRequirementTests
{
    private static readonly IReadOnlySet<string> Connected =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VPN FDC", "Autre" };

    [Fact]
    public void A_connection_that_asks_for_nothing_is_never_held_up()
    {
        Assert.Equal(VpnState.NotRequired, VpnRequirement.Check(null, Connected));
        Assert.Equal(VpnState.NotRequired, VpnRequirement.Check("", Connected));
        Assert.Equal(VpnState.NotRequired, VpnRequirement.Check("   ", Connected));
    }

    [Fact]
    public void A_profile_that_is_up_lets_the_session_through()
    {
        Assert.Equal(VpnState.Connected, VpnRequirement.Check("VPN FDC", Connected));
    }

    [Fact]
    public void The_comparison_ignores_case_and_surrounding_space()
    {
        // The profile name is typed by hand in the editor; Windows shows it with the casing its
        // creator chose, and nobody should have to match it exactly.
        Assert.Equal(VpnState.Connected, VpnRequirement.Check("vpn fdc", Connected));
        Assert.Equal(VpnState.Connected, VpnRequirement.Check("  VPN FDC  ", Connected));
    }

    [Fact]
    public void A_profile_that_is_down_holds_the_session()
    {
        Assert.Equal(VpnState.NotConnected, VpnRequirement.Check("VPN Maison", Connected));
    }

    [Fact]
    public void Nothing_connected_at_all_still_answers()
    {
        Assert.Equal(VpnState.NotConnected, VpnRequirement.Check("VPN FDC", new HashSet<string>()));
    }

    [Fact]
    public void Check_rejects_a_null_set_rather_than_guessing()
    {
        // "No set" is not "nothing is connected": a caller that failed to enumerate must not be
        // silently told the tunnel is down, because the shell would then offer to raise one that is
        // already up.
        Assert.Throws<ArgumentNullException>(() => VpnRequirement.Check("VPN FDC", null!));
    }
}
