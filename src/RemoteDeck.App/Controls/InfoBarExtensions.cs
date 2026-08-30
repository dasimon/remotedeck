namespace RemoteDeck.App.Controls;

/// <summary>
/// The one way RemoteDeck reports status: a Fluent <c>InfoBar</c>, never a <c>MessageBox</c>.
///
/// Every window used to carry its own private <c>ShowStatus</c> helper with exactly the same four
/// assignments; this replaces them so the order (severity and text before <c>IsOpen</c>, so the bar
/// never flashes its previous message) lives in a single place.
/// </summary>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button, TextBox and
// friends ambiguous across the app.
internal static class InfoBarExtensions
{
    /// <summary>Fills the bar in and opens it. Severity and text are set first, so a bar that is
    /// already open never shows the new severity next to the old message.</summary>
    internal static void Show(this Wpf.Ui.Controls.InfoBar bar, Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        ArgumentNullException.ThrowIfNull(bar);

        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        bar.IsOpen = true;
    }

    /// <summary>Closes the bar. The text is left in place: it is invisible while closed, and
    /// clearing it would make the bar collapse through an empty frame on the next open.</summary>
    internal static void Hide(this Wpf.Ui.Controls.InfoBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);

        bar.IsOpen = false;
    }
}
