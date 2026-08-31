using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RemoteDeck.App.Controls;
using RemoteDeck.App.Rdp;
using RemoteDeck.App.Resources;
using RemoteDeck.App.Services;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Diagnostics;
using RemoteDeck.Core.Sessions;
using RemoteDeck.Core.Settings;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// A window that hosts exactly one detached session and none of the application chrome: a 32 px
/// strip that doubles as the caption, that session's InfoBar, and the area the shell drops the
/// session's <c>WindowsFormsHost</c> into.
///
/// The window owns no protocol. It raises <see cref="ReattachRequested"/> and
/// <see cref="CloseRequested"/> and waits: <c>SessionsViewModel</c> is the only thing allowed to
/// move a host between containers, and the §6.5 close protocol belongs to the shell. What the
/// window does own is its own geometry — full screen and <see cref="CurrentPlacement"/>.
/// </summary>
/// <remarks>
/// Internal because the constructor takes a <see cref="SessionTabViewModel"/>, which is itself
/// internal (it exposes <see cref="RdpSession"/>); the XAML carries <c>x:ClassModifier="internal"</c>
/// so the generated half matches.
/// </remarks>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button, TextBox and
// friends ambiguous across the app.
internal sealed partial class SessionWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly SessionTabViewModel _tab;

    /// <summary>What full screen replaced, so leaving it can put all three back (spec §5).</summary>
    private WindowState _restoreState = WindowState.Normal;
    private WindowStyle _restoreStyle = WindowStyle.SingleBorderWindow;
    private Rect _restoreBounds = Rect.Empty;

    private bool _isFullScreen;

    /// <summary>Set by <see cref="AllowClose"/>. Until then every close is cancelled and answered
    /// with <see cref="CloseRequested"/>.</summary>
    private bool _allowClose;

    /// <summary>True once the cross has been answered: the session actions stop being offered while
    /// the shell runs the close protocol, exactly as the shell's own session bar does.</summary>
    private bool _closeRequested;

    public SessionWindow(SessionTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        _tab = tab;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        // Before anything binds: the caption, the status dot and the window's own Title all read
        // from the tab.
        DataContext = tab;

        // F11 reaches this window only while WPF owns the keyboard; the remote session swallows it
        // otherwise, which is what ShortcutInterceptor is for.
        InputBindings.Add(new KeyBinding(new RelayCommand(ToggleFullScreen), Key.F11, ModifierKeys.None));

        _tab.Changed += OnTabChanged;
        _tab.Session.FullScreenRequested += OnFullScreenRequested;

        Closing += OnClosing;

        RefreshCaption();
        RefreshInfoBar();
    }

    /// <summary>The one session this window shows.</summary>
    public SessionTabViewModel Tab => _tab;

    /// <summary>Where the shell puts the session's <c>WindowsFormsHost</c>, and the element the
    /// session measures for its dynamic resolution.</summary>
    public System.Windows.Controls.Decorator HostArea => HostAreaBorder;

    /// <summary>Raised by the <em>Reattach</em> button. The shell moves the host back into the
    /// docked container; the window itself does nothing.</summary>
    public event Action<SessionWindow>? ReattachRequested;

    /// <summary>Raised by the cross — and by any other close — until <see cref="AllowClose"/> has
    /// been called. The shell runs the close protocol and calls it when the session is done.</summary>
    public event Action<SessionWindow>? CloseRequested;

    /// <summary>True while the window covers the screen it sits on.</summary>
    public bool IsFullScreen => _isFullScreen;

    /// <summary>Lets the next close through. Called by the shell once the §6.5 protocol has run;
    /// the window then closes on its own or on the caller's <c>Close()</c>.</summary>
    public void AllowClose()
    {
        _allowClose = true;
    }

    /// <summary>Enters full screen, or leaves it.</summary>
    public void ToggleFullScreen() => SetFullScreen(!_isFullScreen);

    /// <summary>
    /// Where the window is, for <c>settings.json</c>. In full screen the remembered restore bounds
    /// are reported rather than the screen-sized frame, so reopening the connection without full
    /// screen finds a window of a usable size.
    /// </summary>
    public DetachedWindowPlacement CurrentPlacement()
    {
        var bounds = _isFullScreen ? _restoreBounds : NormalBounds();
        if (bounds is not { Width: > 0, Height: > 0 })
        {
            bounds = NormalBounds();
        }

        return new DetachedWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, _isFullScreen);
    }

    /// <summary>The window's un-maximized frame. <c>RestoreBounds</c>, not Left/Top/Width/Height:
    /// those describe the maximized frame, and remembering them would reopen a window that fills
    /// the screen with no way back — the same rule the shell follows for its own geometry.</summary>
    private Rect NormalBounds() =>
        WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

    // ---------------------------------------------------------------- full screen

    /// <summary>
    /// Full screen is a borderless maximized window (spec §5). WPF maximizes onto the screen the
    /// window currently sits on, which is what lets two detached windows fill two monitors at the
    /// same time — no multimon flag, no explicit screen arithmetic.
    ///
    /// The 32 px strip and the InfoBar stay on screen: they are the only way out of full screen
    /// while the remote session holds the keyboard, and they cost the remote desktop 32 px rather
    /// than an exit.
    /// </summary>
    private void SetFullScreen(bool on)
    {
        if (on == _isFullScreen)
        {
            return;
        }

        if (on)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreBounds = NormalBounds();
            _isFullScreen = true;

            // Normal first: a style change on a window that is already maximized is applied to a
            // frame Windows has stopped recomputing, and the caption comes back as a black band.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _isFullScreen = false;
            WindowStyle = _restoreStyle;
            WindowState = _restoreState;
            if (_restoreState == WindowState.Normal && _restoreBounds is { Width: > 0, Height: > 0 })
            {
                Left = _restoreBounds.Left;
                Top = _restoreBounds.Top;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
            }
        }

        ProbeLog.Write("session", $"'{_tab.Title}': full screen {(_isFullScreen ? "entered" : "left")}");
    }

    /// <summary>The remote session asked for full screen itself — <c>ContainerHandledFullScreen</c>
    /// makes that the container's decision, and the container is this window.</summary>
    private void OnFullScreenRequested(bool enter) => SetFullScreen(enter);

    // ---------------------------------------------------------------- caption

    /// <summary>
    /// Drags the window by its strip. Refused on a double-click and on a maximized window: neither
    /// has anything to drag, and <c>DragMove</c> throws when the button is no longer down.
    /// </summary>
    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || WindowState != WindowState.Normal)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released between the event and this call. Nothing to drag, nothing to
            // report: the window simply stays where it is.
        }
    }

    private void OnReattachClick(object sender, RoutedEventArgs e) => ReattachRequested?.Invoke(this);

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    /// <summary>The cross. It goes through <see cref="OnClosing"/> like every other close, so the
    /// protocol runs exactly once wherever the close came from.</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnReconnectClick(object sender, RoutedEventArgs e) => ReconnectSession();

    /// <summary><em>Reconnect</em>. <c>async void</c> is the only shape an awaiting handler can
    /// take, hence the fully guarded body.</summary>
    private async void ReconnectSession()
    {
        try
        {
            await _tab.Session.ReconnectNowAsync();
        }
        catch (Exception ex)
        {
            // RdpSession swallows attempt failures itself; this only covers the unexpected.
            ProbeLog.Write("session", $"Reconnect failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private void OnCancelRetryClick(object sender, RoutedEventArgs e) => _tab.Session.CancelReconnect();

    /// <summary>Copies this session's diagnostics. The clipboard is owned by whatever currently
    /// holds it, so <c>SetText</c> is allowed to fail — and must not cost the window.</summary>
    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Qualified: UseWindowsForms puts System.Windows.Forms.Clipboard in scope too.
            System.Windows.Clipboard.SetText(_tab.Session.BuildDiagnostics());
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success, Strings.Shell_DiagnosticsCopiedTitle,
                Text.Of(Strings.Shell_DiagnosticsCopiedMessage, _tab.Title));
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"Clipboard.SetText failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DiagnosticsNotCopiedTitle,
                Text.Of(Strings.Shell_DiagnosticsNotCopiedMessage, ex.GetType().Name));
        }
    }

    // ---------------------------------------------------------------- state

    private void OnTabChanged(SessionTabViewModel tab)
    {
        RefreshCaption();
        RefreshInfoBar();
    }

    /// <summary><em>Reconnect</em> is offered exactly where a new attempt is legal (Failed, or Idle
    /// after a normal disconnect) and <em>Cancel</em> exactly where a retry is pending, so the two
    /// are never both on screen — the same rule as the shell's session bar.</summary>
    private void RefreshCaption()
    {
        bool live = !_closeRequested;
        ReconnectButton.Visibility = live && _tab.State is SessionState.Failed or SessionState.Idle
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelRetryButton.Visibility = live && _tab.State is SessionState.Interrupted or SessionState.Reconnecting
            ? Visibility.Visible
            : Visibility.Collapsed;
        DiagnosticsButton.IsEnabled = live;
    }

    /// <summary>
    /// Reports the session's state in the one place RemoteDeck reports anything. Wording, severity
    /// rule and resource keys are the shell's (§6.4): a session says the same thing whether it is
    /// docked or detached.
    /// </summary>
    private void RefreshInfoBar()
    {
        var session = _tab.Session;
        var disconnect = session.LastDisconnect;

        switch (_tab.State)
        {
            case SessionState.Idle when disconnect is null:
                // Freshly detached, nothing has happened yet: leave whatever is on screen alone.
                break;

            case SessionState.Idle:
                // disconnect.Title comes from RemoteDeck.Core and stays English in v1 (spec §9):
                // only the wording around it is localised.
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    Text.Of(Strings.Session_DisconnectedTitle, _tab.Title), disconnect!.Title);
                break;

            case SessionState.Connecting:
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    Text.Of(Strings.Session_ConnectingTitle, _tab.Title), _tab.Subtitle);
                break;

            case SessionState.Connected:
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
                    Text.Of(Strings.Session_ConnectedTitle, _tab.Title), _tab.Subtitle);
                break;

            case SessionState.Interrupted:
                // The countdown is empty for the tick between the drop and the first timer tick;
                // the attempt then stands on its own rather than behind a leading space.
                string progress = Text.Of(Strings.Session_AttemptProgress, session.Attempt, ReconnectPolicy.MaxAttempts);
                StatusBar.Show(SeverityFor(disconnect),
                    Text.Of(Strings.Session_InterruptedTitle, _tab.Title,
                        disconnect?.Title ?? Strings.Session_ConnectionLost),
                    Join(_tab.CountdownText.Length == 0
                            ? progress
                            : Text.Of(Strings.Session_CountdownWithProgress, _tab.CountdownText, progress),
                        WindowsWording(session)));
                break;

            case SessionState.Reconnecting:
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    Text.Of(Strings.Session_ReconnectingTitle, _tab.Title),
                    Text.Of(Strings.Session_ReconnectingMessage, session.Attempt, ReconnectPolicy.MaxAttempts));
                break;

            case SessionState.Failed:
                StatusBar.Show(SeverityFor(disconnect),
                    Text.Of(Strings.Session_FailedTitle, _tab.Title,
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

    // ---------------------------------------------------------------- shutdown

    /// <summary>
    /// Closing this window means closing the session it holds, and that protocol is the shell's
    /// (§6.5). So the first pass cancels and asks: the shell disconnects the session, calls
    /// <see cref="AllowClose"/> and closes the window, and this handler then lets it go. Same
    /// two-pass shape the shell uses for itself.
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            _tab.Changed -= OnTabChanged;
            _tab.Session.FullScreenRequested -= OnFullScreenRequested;
            return;
        }

        e.Cancel = true;
        if (_closeRequested)
        {
            // The cross can be clicked again while the shell disconnects; asking twice would start
            // a second protocol over the same session.
            return;
        }

        _closeRequested = true;
        RefreshCaption();
        ProbeLog.Write("session", $"'{_tab.Title}': detached window close requested");
        CloseRequested?.Invoke(this);
    }
}
