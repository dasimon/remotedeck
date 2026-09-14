using RemoteDeck.Core.Sessions;

namespace RemoteDeck.Core.Tests.Sessions;

/// <summary>
/// Who decides that a tunnel goes up. The state of the profile says whether anything needs doing;
/// the connection's own opt-in says whether the user already answered; and whether the caller may
/// ask at all says what happens when neither is enough.
/// </summary>
public sealed class VpnConsentTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_connection_that_needs_nothing_goes_ahead(bool autoRaise, bool mayAsk)
    {
        Assert.Equal(VpnConsentVerdict.Proceed, VpnConsent.Decide(VpnState.NotRequired, autoRaise, mayAsk));
        Assert.Equal(VpnConsentVerdict.Proceed, VpnConsent.Decide(VpnState.Connected, autoRaise, mayAsk));
    }

    [Fact]
    public void A_tunnel_that_is_down_is_asked_about_by_default()
    {
        Assert.Equal(VpnConsentVerdict.Ask, VpnConsent.Decide(VpnState.NotConnected, autoRaise: false, mayAsk: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_connection_that_opted_in_is_raised_without_a_question(bool mayAsk)
    {
        // The box was ticked in the editor: that is the answer, given once, and asking again on every
        // connect is the ceremony the box exists to remove. It holds where no question could be
        // asked either — a workspace mount — since the consent does not depend on who is asking.
        Assert.Equal(VpnConsentVerdict.RaiseWithoutAsking,
            VpnConsent.Decide(VpnState.NotConnected, autoRaise: true, mayAsk));
    }

    [Fact]
    public void A_caller_that_may_not_ask_leaves_an_unticked_connection_as_it_was()
    {
        // A workspace mount, which opens its sessions in series and must not stop on six dialogs.
        // Without the opt-in it neither asks nor raises: the session goes ahead and fails the ordinary
        // way, exactly as it did before the box existed.
        Assert.Equal(VpnConsentVerdict.Proceed,
            VpnConsent.Decide(VpnState.NotConnected, autoRaise: false, mayAsk: false));
    }

    [Fact]
    public void Nothing_is_ever_raised_without_the_connection_opting_in()
    {
        // The invariant the feature rests on. A connection attempt is not consent to change the
        // machine's network state; only the ticked box is.
        foreach (var state in Enum.GetValues<VpnState>())
        {
            foreach (var mayAsk in new[] { true, false })
            {
                Assert.NotEqual(VpnConsentVerdict.RaiseWithoutAsking, VpnConsent.Decide(state, autoRaise: false, mayAsk));
            }
        }
    }

    [Fact]
    public void A_caller_that_may_not_ask_is_never_told_to()
    {
        foreach (var state in Enum.GetValues<VpnState>())
        {
            foreach (var autoRaise in new[] { true, false })
            {
                Assert.NotEqual(VpnConsentVerdict.Ask, VpnConsent.Decide(state, autoRaise, mayAsk: false));
            }
        }
    }
}
