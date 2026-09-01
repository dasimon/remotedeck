using RemoteDeck.App.Rdp;
using RemoteDeck.App.Resources;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Diagnostics;
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.App.Controls;

/// <summary>
/// Turns one session's state into the InfoBar line that reports it (spec §6.4). The shell writes it
/// into its own status bar for the active tab, and a detached <c>SessionWindow</c> writes it into
/// its own for the single session it holds: a session says exactly the same thing, with the same
/// severity and the same resource keys, whether it is docked or in a window of its own.
/// </summary>
/// <remarks>
/// A pure function of the tab it is handed, writing into the bar it is handed: it keeps no state of
/// its own and never reads the caller's, which is what lets the two windows share it without
/// sharing anything else.
/// </remarks>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button, TextBox and
// friends ambiguous across the app.
internal static class SessionStatusPresenter
{
    /// <summary>
    /// Reports <paramref name="tab"/>'s state in <paramref name="bar"/>. Severity follows the
    /// disconnect family (§6.4): codes 0–3 are informational, a network drop is a warning — it is
    /// being retried — and everything else is an error, with Windows' own wording attached because
    /// that is the only text that names the actual cause.
    /// </summary>
    /// <remarks>
    /// Two states write nothing: a session that has not done anything yet, and one on its way out.
    /// Both leave whatever is on screen alone rather than blanking the bar.
    /// </remarks>
    internal static void Report(Wpf.Ui.Controls.InfoBar bar, SessionTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(tab);

        var session = tab.Session;
        var disconnect = session.LastDisconnect;

        switch (tab.State)
        {
            case SessionState.Idle when disconnect is null:
                // Freshly opened or freshly detached, nothing has happened yet.
                break;

            case SessionState.Idle:
                // disconnect.Title comes from RemoteDeck.Core and stays English in v1 (spec §9):
                // only the wording around it is localised.
                bar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    Text.Of(Strings.Session_DisconnectedTitle, tab.Title), disconnect!.Title);
                break;

            case SessionState.Connecting:
                bar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    Text.Of(Strings.Session_ConnectingTitle, tab.Title), tab.Subtitle);
                break;

            case SessionState.Connected:
                bar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
                    Text.Of(Strings.Session_ConnectedTitle, tab.Title), tab.Subtitle);
                break;

            case SessionState.Interrupted:
                // The countdown is empty for the tick between the drop and the first timer tick;
                // the attempt then stands on its own rather than behind a leading space.
                string progress = Text.Of(Strings.Session_AttemptProgress, session.Attempt, ReconnectPolicy.MaxAttempts);
                bar.Show(SeverityFor(disconnect),
                    Text.Of(Strings.Session_InterruptedTitle, tab.Title,
                        disconnect?.Title ?? Strings.Session_ConnectionLost),
                    Join(tab.CountdownText.Length == 0
                            ? progress
                            : Text.Of(Strings.Session_CountdownWithProgress, tab.CountdownText, progress),
                        WindowsWording(session)));
                break;

            case SessionState.Reconnecting:
                bar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    Text.Of(Strings.Session_ReconnectingTitle, tab.Title),
                    Text.Of(Strings.Session_ReconnectingMessage, session.Attempt, ReconnectPolicy.MaxAttempts));
                break;

            case SessionState.Failed:
                bar.Show(SeverityFor(disconnect),
                    Text.Of(Strings.Session_FailedTitle, tab.Title,
                        disconnect?.Title ?? Strings.Session_CouldNotConnect),
                    WindowsWording(session));
                break;

            default:
                // Closing and Closed: the session is on its way out, the bar has nothing to add.
                break;
        }
    }

    /// <summary>
    /// Tone of a disconnect. A network family is a warning because it is being — or can be —
    /// retried; authentication, security, licensing and internal failures need the user, so they
    /// are errors. No description at all means the attempt never reached the wire: also an error.
    /// </summary>
    private static Wpf.Ui.Controls.InfoBarSeverity SeverityFor(DisconnectDescription? disconnect) => disconnect?.Category switch
    {
        null => Wpf.Ui.Controls.InfoBarSeverity.Error,
        DisconnectCategory.NotAnError => Wpf.Ui.Controls.InfoBarSeverity.Informational,
        DisconnectCategory.Network => Wpf.Ui.Controls.InfoBarSeverity.Warning,
        _ => Wpf.Ui.Controls.InfoBarSeverity.Error,
    };

    /// <summary>
    /// Windows' own description of the failure, or an empty string when there is none to show.
    /// Deliberately withheld for codes 0–3: <c>GetErrorDescription()</c> answers "an internal error
    /// has occurred" for them, which would turn an ordinary log-off into an alarming message.
    /// </summary>
    private static string WindowsWording(RdpSession session) =>
        session.LastDisconnect is { Category: DisconnectCategory.NotAnError }
            ? ""
            : session.LastWindowsDescription ?? "";

    private static string Join(string first, string second) =>
        second.Length == 0 ? first : Text.Of(Strings.Session_DetailSeparator, first, second);
}
