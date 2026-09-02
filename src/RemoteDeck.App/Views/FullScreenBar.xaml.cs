using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteDeck.App.ViewModels;

namespace RemoteDeck.App.Views;

/// <summary>
/// The connection bar of a full-screen session: a small window of its own that slides in when the
/// pointer reaches the top of the screen its <see cref="SessionWindow"/> occupies, and retracts when
/// the pointer leaves it. It carries the status dot, the session's name and host, and the three
/// actions the caption strip offers — reattach, leave full screen, close.
///
/// It is a top-level window and not a piece of <see cref="SessionWindow"/> on purpose: chrome
/// revealed inside the window would shrink the host area, and in <c>Dynamic</c> display mode that
/// renegotiates the remote resolution — the remote image would jump because the pointer brushed the
/// top of the screen. The session is also a <c>WindowsFormsHost</c>, which wins every airspace
/// fight, so a WPF overlay inside the window could not paint over the remote desktop at all. A
/// separate window passes over it without ever touching the host's size.
/// </summary>
/// <remarks>
/// <para>
/// The bar lives exactly as long as one full-screen episode: <see cref="SessionWindow"/> creates it
/// when full screen is entered and <see cref="Dismiss"/>es it when full screen ends, when the
/// session stops being connected, and when the window closes. On top of that it is
/// <see cref="Window.Owner"/>ed by that window, so WPF closes it with its owner whatever happens —
/// a <see cref="Window.Topmost"/> window left behind would float over everything with nothing left
/// to dismiss it.
/// </para>
/// <para>
/// The pointer is sampled on a timer rather than watched through <c>MouseMove</c>: the RDP control
/// captures the mouse, so WPF sees no move at all over the remote desktop. The timer runs only
/// while full screen lasts and only reads the cursor position — it never resizes anything, which is
/// what the earlier in-window attempt got wrong.
/// </para>
/// </remarks>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button, TextBox and
// friends ambiguous across the app.
internal sealed partial class FullScreenBar : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>How close to the top edge of the screen the pointer has to come, in the same
    /// device-independent units as <see cref="Window.Top"/>.</summary>
    private const double RevealBand = 4;

    /// <summary>Where the window is parked for the single frame between its first
    /// <see cref="Window.Show"/> — which is what finally measures it — and the placement that
    /// centres it. Far outside any virtual screen, so nothing is ever painted there.</summary>
    private const double Parking = -32000;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>How long the bar stays up when full screen is entered, so that two sessions on two
    /// monitors say which is which before both retract.</summary>
    private static readonly TimeSpan EntryHold = TimeSpan.FromSeconds(3);

    private readonly SessionWindow _owner;
    private readonly DispatcherTimer _timer;

    private DateTime _holdUntil = DateTime.MinValue;
    private bool _revealed;
    private bool _everShown;
    private bool _dismissed;

    public FullScreenBar(SessionWindow owner, SessionTabViewModel tab, SessionsViewModel sessions)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(sessions);

        _owner = owner;
        InitializeComponent();

        // The dot, the name and the host all read from the tab, exactly as the caption strip does.
        DataContext = tab;

        // Every session but this one, as chips beside the title. A view of its own rather than the
        // collection itself: the filter has to drop exactly one item — the session this bar already
        // names — and a ListCollectionView over the ObservableCollection follows opens and closes on
        // its own. Filtering on identity means nothing can ever invalidate it, so it is never
        // refreshed; a tab's *state* changing is a property change the chip's bindings already see.
        OtherSessions.ItemsSource = new ListCollectionView(sessions.Tabs)
        {
            Filter = item => !ReferenceEquals(item, tab),
        };

        // Ownership is the safety net: whatever path takes the session window down — its own close,
        // the shell's, application shutdown — WPF closes this one with it.
        Owner = owner;

        // SizeToContent measures the bar only once it has a source, so the first placement can run
        // with a width of zero; this is what corrects it, on the same layout pass and before
        // anything is rendered.
        SizeChanged += (_, _) => Place();

        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = SampleInterval };
        _timer.Tick += OnTick;
    }

    /// <summary>The <em>Reattach</em> button.</summary>
    public event Action? ReattachRequested;

    /// <summary>The <em>Full screen</em> button — from this bar it can only mean leaving it.</summary>
    public event Action? LeaveFullScreenRequested;

    /// <summary>The cross.</summary>
    public event Action? CloseSessionRequested;

    /// <summary>One of the other sessions was clicked. The bar knows nothing about where that session
    /// lives — docked in the shell or full screen on another monitor — so it names it and lets the
    /// shell, which owns that map, decide what bringing it forward means.</summary>
    public event Action<SessionTabViewModel>? SessionRequested;

    /// <summary>
    /// Starts following the pointer, and shows the bar for <see cref="EntryHold"/> so the user can
    /// see which session just took which screen. If the pointer is on the bar when that delay
    /// expires the ordinary rule keeps it there until the pointer leaves.
    /// </summary>
    public void Begin()
    {
        if (_dismissed)
        {
            return;
        }

        _holdUntil = DateTime.UtcNow + EntryHold;
        Reveal();
        _timer.Start();
    }

    /// <summary>Takes the bar down and closes it for good. Idempotent: every path out of full
    /// screen leads here, and several of them can run one after the other.</summary>
    public void Dismiss()
    {
        if (_dismissed)
        {
            return;
        }

        _dismissed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _revealed = false;
        Close();
    }

    /// <summary>Mirrors the caption strip: <em>Reattach</em> stops being offered once the cross has
    /// been answered, because the shell is running the close protocol over this session and that
    /// protocol must not have its control moved to another window underneath it.</summary>
    public void SetLive(bool live) => ReattachButton.IsEnabled = live;

    /// <summary>
    /// The bar must never take the keyboard away from the remote session: one that revealed itself
    /// because the pointer brushed the top of the screen and stole the focus would swallow the next
    /// keystrokes, and would also deactivate the very window whose bar it is.
    /// <see cref="Window.ShowActivated"/> only covers the first <see cref="Window.Show"/>;
    /// <c>WS_EX_NOACTIVATE</c> covers every one of them, and clicks on the three buttons still
    /// arrive because mouse input goes to the window under the pointer whether it is active or not.
    /// <c>WS_EX_TOOLWINDOW</c> keeps it out of Alt+Tab for the same reason it is out of the taskbar.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        nint style = GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    // ---------------------------------------------------------------- pointer

    /// <summary>
    /// One sample of the cursor. Three rules, in order: the bar belongs to a full-screen window that
    /// is active, it appears when the pointer reaches the top edge of that window's screen, and it
    /// goes once the pointer leaves its own rectangle.
    /// </summary>
    /// <remarks>
    /// The active check is what keeps two full-screen sessions on two monitors apart: the pointer
    /// crossing an inactive session's screen must not pop that session's bar up. The bar's own
    /// <see cref="Window.IsActive"/> counts as the owner being active — belt and braces, since
    /// <c>WS_EX_NOACTIVATE</c> already means clicking the bar leaves the session window active.
    /// </remarks>
    private void OnTick(object? sender, EventArgs e)
    {
        if (_dismissed)
        {
            return;
        }

        if (!_owner.IsFullScreen || !(_owner.IsActive || IsActive))
        {
            Retract();
            return;
        }

        if (OwnerScreen() is not { } screen)
        {
            Retract();
            return;
        }

        var cursor = CursorPosition();

        if (_revealed)
        {
            if (DateTime.UtcNow < _holdUntil)
            {
                return;
            }

            if (!new Rect(Left, Top, ActualWidth, ActualHeight).Contains(cursor))
            {
                Retract();
            }

            return;
        }

        bool atTopEdge = cursor.X >= screen.Left && cursor.X <= screen.Right
            && cursor.Y >= screen.Top && cursor.Y <= screen.Top + RevealBand;
        if (atTopEdge)
        {
            Reveal();
        }
    }

    private void Reveal()
    {
        if (!_revealed)
        {
            if (!_everShown)
            {
                // Nothing has measured the bar yet, so it cannot be centred before it is shown.
                _everShown = true;
                Left = Parking;
                Top = Parking;
            }

            _revealed = true;
            Show();
        }

        Place();

        // A window that was hidden and shown again can lose its place in the z-order; the bar is
        // only useful in front of the remote desktop.
        Topmost = true;
    }

    private void Retract()
    {
        if (!_revealed)
        {
            return;
        }

        _revealed = false;
        Hide();
    }

    /// <summary>Centres the bar on the top edge of the screen its owner is filling.</summary>
    private void Place()
    {
        if (_dismissed || OwnerScreen() is not { } screen)
        {
            return;
        }

        Left = screen.Left + ((screen.Width - ActualWidth) / 2);
        Top = screen.Top;
    }

    /// <summary>
    /// The screen the full-screen window occupies, in the device-independent units
    /// <see cref="Window.Left"/> and <see cref="Window.Top"/> are expressed in.
    /// </summary>
    /// <remarks>
    /// <c>Screen</c> reports physical pixels, hence the division by the owner's DPI scale — the same
    /// conversion <c>ShellWindow.Screens</c> makes, and exact for the same reason: full screen is
    /// one window on one monitor, so the scale that applies is the one that window is running at.
    /// The full bounds rather than the working area, because full screen means edge to edge.
    /// </remarks>
    private Rect? OwnerScreen()
    {
        nint handle = new WindowInteropHelper(_owner).Handle;
        if (handle == 0)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(_owner);
        // Qualified: UseWindowsForms puts System.Windows.Forms.Screen in scope through the implicit
        // usings, and RemoteDeck never imports that namespace into a WPF file.
        var area = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
        return new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
            area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
    }

    /// <summary>Where the pointer is, in the same units as <see cref="OwnerScreen"/> — the RDP
    /// control has the mouse captured, so this is the only way to know.</summary>
    // Qualified for the same reason as Screen above: System.Drawing.Point is in scope too.
    private System.Windows.Point CursorPosition()
    {
        var dpi = VisualTreeHelper.GetDpi(_owner);
        var position = System.Windows.Forms.Cursor.Position;
        return new System.Windows.Point(position.X / dpi.DpiScaleX, position.Y / dpi.DpiScaleY);
    }

    // ---------------------------------------------------------------- actions

    private void OnReattachClick(object sender, RoutedEventArgs e) => ReattachRequested?.Invoke();

    private void OnLeaveFullScreenClick(object sender, RoutedEventArgs e) => LeaveFullScreenRequested?.Invoke();

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseSessionRequested?.Invoke();

    /// <summary>A session chip. On button-up rather than button-down, the way a menu entry commits:
    /// the press is what tells the user the chip is live, and a pointer that slides off before
    /// releasing has changed nothing.</summary>
    private void OnSessionChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionTabViewModel tab })
        {
            return;
        }

        e.Handled = true;
        SessionRequested?.Invoke(tab);
    }

    // ---------------------------------------------------------------- interop

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    // The *Ptr entry points exist under those names on x64 only, which is what this project pins
    // PlatformTarget to.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
}
