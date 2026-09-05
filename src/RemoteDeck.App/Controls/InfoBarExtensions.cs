using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace RemoteDeck.App.Controls;

// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button, TextBox and
// friends ambiguous across the app.

/// <summary>
/// The one way an InfoBar is shown and hidden in this application, and therefore the one place
/// its motion lives: it slides in from just above and fades, and fades out.
/// </summary>
/// <remarks>
/// Only the bar's arrival is animated. A message replacing one already on screen swaps its text in
/// place — animating that would make the bar flicker on every status update during a reconnect —
/// and the bar's own close button, when the window makes it closable, closes it at once: the
/// user's click is the gesture there.
/// </remarks>
internal static class InfoBarExtensions
{
    /// <summary>How far above its place the bar starts. Small on purpose: it is a notice, not a sheet.</summary>
    private const double ArrivalOffset = -8;

    /// <summary>
    /// How long an informational or success notice stays before retiring on its own. Warnings and
    /// errors stay until acted on: they ask for something. A notice that only tells does not need
    /// to be dismissed by hand — and the launch notice used to sit on screen for the whole session.
    /// </summary>
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(8);

    private static readonly ConditionalWeakTable<Wpf.Ui.Controls.InfoBar, DispatcherTimer> Retirements = new();

    internal static void Show(this Wpf.Ui.Controls.InfoBar bar, Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        ArgumentNullException.ThrowIfNull(bar);
        var arriving = !bar.IsOpen;

        bar.Severity = severity;
        bar.Title = title;
        bar.Message = message;
        Retire(bar, severity is Wpf.Ui.Controls.InfoBarSeverity.Informational or Wpf.Ui.Controls.InfoBarSeverity.Success);

        if (arriving)
        {
            bar.Opacity = 0;
            bar.IsOpen = true;
            Motion.Arrive(bar, ArrivalOffset, Motion.Normal);
        }
        else
        {
            // Already on screen, possibly mid-departure from a Hide a moment ago: whatever gesture
            // was running, the bar is wanted now, whole.
            Motion.Settle(bar);
        }
    }

    internal static void Hide(this Wpf.Ui.Controls.InfoBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        Retire(bar, false);
        if (!bar.IsOpen)
        {
            return;
        }

        Motion.Leave(bar, Motion.Fast, () =>
        {
            bar.IsOpen = false;
            // Opacity is left at 1 for the next Show, which sets it before opening anyway; a bar
            // that is closed and transparent is one that a future direct IsOpen = true would show
            // invisible.
            bar.Opacity = 1;
        });
    }

    /// <summary>Arms, or disarms, the bar's own retirement. A newer message restarts the clock.</summary>
    private static void Retire(Wpf.Ui.Controls.InfoBar bar, bool later)
    {
        var timer = Retirements.GetValue(bar, b =>
        {
            var t = new DispatcherTimer(DispatcherPriority.Normal, b.Dispatcher) { Interval = NoticeLifetime };
            t.Tick += (_, _) => { t.Stop(); b.Hide(); };
            return t;
        });
        timer.Stop();
        if (later)
        {
            timer.Start();
        }
    }
}
