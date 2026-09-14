using System.Windows;
using RemoteDeck.App.Controls;
using RemoteDeck.App.Resources;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.App.Services;

/// <summary>
/// The one gate a session passes through before it connects or reconnects: is the VPN profile it
/// needs up, and if not, does the user want it raised?
/// </summary>
/// <remarks>
/// <para>
/// It lives here rather than in the shell because two windows ask the same question — the shell for
/// a docked tab, a detached window for its own session — and a second copy of this reasoning is a
/// second place for it to drift.
/// </para>
/// <para>
/// Only ever from something the user started — a connect, a reconnect, a workspace — and never from
/// the retry loop. A connection attempt alone is not consent to change the machine's network state:
/// the tunnel goes up after a yes to the question, or because the connection's own box says the
/// answer is always yes. Which of the two applies is <see cref="VpnConsent"/>'s call; when the box
/// answers, a notice still names the tunnel being raised, because a VPN that goes up silently is a
/// VPN nobody knows is up.
/// </para>
/// </remarks>
internal static class VpnGate
{
    /// <summary>
    /// Whether <paramref name="connection"/> may go ahead: it needs no VPN, the one it needs is up,
    /// or it was down and a dial the user agreed to — now, or once in the editor — brought it up.
    /// </summary>
    /// <param name="owner">The window the question is asked over.</param>
    /// <param name="report">Where a refusal or a notice is written — each window's own status bar.</param>
    /// <param name="mayAsk">False when no question may be put — a workspace mount. An unticked
    /// connection then goes ahead as it always did; a ticked one still has its tunnel raised.</param>
    public static async Task<bool> EnsureReadyAsync(
        Window owner, Connection connection, Action<Wpf.Ui.Controls.InfoBarSeverity, string, string> report,
        bool mayAsk = true)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(report);

        var verdict = VpnConsent.Decide(State(connection), connection.AutoRaiseVpn, mayAsk);
        if (verdict == VpnConsentVerdict.Proceed)
        {
            return true;
        }

        // Not blank: State reads a blank profile as NotRequired, and that always proceeds.
        var profile = connection.VpnProfile!.Trim();

        if (verdict == VpnConsentVerdict.Ask)
        {
            // Fully qualified: UseWindowsForms puts System.Windows.Forms.MessageBox in scope too.
            var answer = System.Windows.MessageBox.Show(owner,
                Text.Of(Strings.Shell_VpnDownMessage, connection.Name, profile),
                Text.Of(Strings.Shell_VpnDownTitle, profile),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
            {
                report(Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    Text.Of(Strings.Shell_VpnDownTitle, profile),
                    Text.Of(Strings.Shell_VpnDownMessage, connection.Name, profile));
                return false;
            }
        }
        else
        {
            // No question, but not silent: the notice goes up before the dial, which can take a few
            // seconds, so the pause has a name — and the log keeps a trace of every tunnel raised
            // without a click on a dialog.
            ProbeLog.Write("vpn", $"'{connection.Name}': raising '{profile}' without asking (the connection opted in)");
            report(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                Text.Of(Strings.Shell_VpnDialingTitle, profile),
                Text.Of(Strings.Shell_VpnAutoRaisingMessage, connection.Name));
        }

        var result = await WindowsVpn.DialAsync(profile);

        switch (result.Outcome)
        {
            case VpnDialOutcome.Connected:
                // The dial was synchronous and the profile is up: there is nothing left to wait for,
                // and asking the user to press connect a second time would be ceremony.
                return true;

            case VpnDialOutcome.NoStoredCredential:
                // RemoteDeck asks for no VPN secret and stores none. Windows is where that belongs,
                // and this says so instead of failing with a code nobody can act on.
                report(Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    Text.Of(Strings.Shell_VpnDialFailedTitle, profile),
                    Text.Of(Strings.Shell_VpnNoCredential, profile));
                return false;

            case VpnDialOutcome.EntryNotFound:
                report(Wpf.Ui.Controls.InfoBarSeverity.Error,
                    Text.Of(Strings.Shell_VpnDialFailedTitle, profile),
                    Text.Of(Strings.Shell_VpnUnknownProfile, profile));
                return false;

            case VpnDialOutcome.Failed:
                // Windows's own words when it refuses, rather than a message of ours guessing at the
                // cause: 691 is a bad credential, 789 an IPsec negotiation that failed, 809 a NAT in
                // the way, and none of that is something RemoteDeck could paraphrase usefully.
                report(Wpf.Ui.Controls.InfoBarSeverity.Error,
                    Text.Of(Strings.Shell_VpnDialFailedTitle, profile), result.Detail);
                return false;

            default:
                // Raised but not visible yet, or still dialling: the tunnel is Windows's business now.
                report(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    Text.Of(Strings.Shell_VpnDialingTitle, profile), Strings.Shell_VpnDialingMessage);
                return false;
        }
    }

    /// <summary>
    /// The state of the profile <paramref name="connection"/> names, for a caller that only wants to
    /// know — the retry loop, which must never ask a question or raise anything.
    /// </summary>
    /// <returns>
    /// <see cref="VpnState.NotRequired"/> when the connection names no profile <em>and</em> when the
    /// enumeration itself fails. A failure to read the state is deliberately not "the tunnel is
    /// down": that would stop a reconnection for a tunnel that may well be up, so a broken check can
    /// never be worse than no check at all.
    /// </returns>
    public static VpnState State(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(connection.VpnProfile))
        {
            return VpnState.NotRequired;
        }

        try
        {
            return VpnRequirement.Check(connection.VpnProfile, WindowsVpn.ConnectedProfiles());
        }
        catch (Exception ex)
        {
            ProbeLog.Write("vpn", $"Could not read the VPN state: {ex.GetType().Name}: {ex.Message}; not treated as down");
            return VpnState.NotRequired;
        }
    }
}
