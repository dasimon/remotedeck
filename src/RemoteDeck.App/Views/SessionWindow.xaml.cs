using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RemoteDeck.App.Controls;
using RemoteDeck.App.Rdp;
using RemoteDeck.App.Resources;
using RemoteDeck.App.Services;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Settings;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// A window that hosts exactly one detached session and none of the application chrome: a 32 px
/// strip that doubles as the caption, that session's InfoBar, and the area the shell drops the
/// session's <c>WindowsFormsHost</c> into.
///
/// The window owns no protocol. It raises <see cref="ReattachRequested"/>,
/// <see cref="CloseRequested"/> and — while it is being dragged by its caption —
/// <see cref="CaptionDragMoved"/> / <see cref="CaptionDragEnded"/>, then waits:
/// <c>SessionsViewModel</c> is the only thing allowed to move a host between containers, the §6.5
/// close protocol belongs to the shell, and so does the decision that a drag ended over the tab
/// strip. What the window does own is its own geometry — full screen and
/// <see cref="CurrentPlacement"/>.
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

    /// <summary>What the host area looked like before full screen took its margin away.</summary>
    private Thickness _restoreHostMargin;
    private CornerRadius _restoreHostCorner;

    private bool _isFullScreen;

    /// <summary>Set by <see cref="AllowClose"/>. Until then every close is cancelled and answered
    /// with <see cref="CloseRequested"/>.</summary>
    private bool _allowClose;

    /// <summary>True once the cross has been answered: the session actions stop being offered while
    /// the shell runs the close protocol, exactly as the shell's own session bar does.</summary>
    private bool _closeRequested;

    /// <summary>True for the whole of a caption drag, i.e. while <c>DragMove</c>'s modal move loop
    /// runs. It is what turns an ordinary <c>LocationChanged</c> into
    /// <see cref="CaptionDragMoved"/>.</summary>
    private bool _captionDragging;

    public SessionWindow(SessionTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        _tab = tab;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        // Before anything binds: the caption, the status dot and the window's own Title all read
        // from the tab.
        DataContext = tab;

        // These reach this window only while WPF owns the keyboard; the remote session swallows them
        // otherwise, which is what ShortcutInterceptor is for — it routes the same three to whichever
        // window is active. The two never double-fire: the hook swallows what it handles. No
        // canExecute guard as the shell's bindings carry: this window holds no text input at all.
        InputBindings.Add(new KeyBinding(new RelayCommand(ToggleFullScreen), Key.F11, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => ReattachRequested?.Invoke(this)),
            Key.D, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => Close()), Key.W, ModifierKeys.Control));

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

    /// <summary>
    /// The window moved while the user is dragging it by its caption strip. Raised on every step of
    /// the drag, with <see cref="Window.Left"/> and <see cref="Window.Top"/> already updated, so the
    /// shell can decide whether the window is over its tab strip and light the drop band.
    /// </summary>
    public event Action<SessionWindow>? CaptionDragMoved;

    /// <summary>
    /// The caption drag is over — the button came up, wherever the window ended. Raised exactly once
    /// per <see cref="CaptionDragMoved"/> sequence, including when the drag moved nothing, so the
    /// shell always gets to clear what the drag put on screen.
    /// </summary>
    public event Action<SessionWindow>? CaptionDragEnded;

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
    /// The 32 px strip, the InfoBar and the host area's margin all go: full screen means a remote
    /// desktop edge to edge, which is the whole point of the gesture. Nothing is ever laid over it
    /// and its size never changes while it lasts — chrome that came and went with the pointer would
    /// resize the host, and in dynamic display mode that renegotiates the remote resolution because
    /// the pointer brushed the top of the screen.
    ///
    /// Full screen is therefore bound to the connected state: it is only entered from it, and
    /// <see cref="LeaveFullScreenUnlessConnected"/> ends it the moment the session leaves it. That
    /// is what puts the strip and the InfoBar back exactly when they have something to report — the
    /// reason, <em>Reconnect</em>, <em>Copy diagnostics</em> — instead of leaving the user in front
    /// of a black screen with no explanation. F11 and Ctrl+Alt+Pause remain the manual way out at
    /// any time, alongside the control's own <c>RequestLeaveFullScreen</c>.
    /// </summary>
    private void SetFullScreen(bool on)
    {
        if (on == _isFullScreen)
        {
            return;
        }

        // Only a live session is worth showing edge to edge, and it is the state leaving Connected
        // that ends full screen: entering from anywhere else would produce a chromeless window no
        // state change would ever take out again.
        if (on && _tab.State != SessionState.Connected)
        {
            ProbeLog.Write("session", $"'{_tab.Title}': full screen refused, the session is {_tab.State}");
            return;
        }

        if (on)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreBounds = NormalBounds();
            _restoreHostMargin = HostAreaBorder.Margin;
            _restoreHostCorner = HostAreaBorder.CornerRadius;
            _isFullScreen = true;

            // Normal first: a style change on a window that is already maximized is applied to a
            // frame Windows has stopped recomputing, and the caption comes back as a black band.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;

            HostAreaBorder.Margin = new Thickness(0);
            HostAreaBorder.CornerRadius = new CornerRadius(0);
            ShowChrome(false);
        }
        else
        {
            _isFullScreen = false;
            ShowChrome(true);
            HostAreaBorder.Margin = _restoreHostMargin;
            HostAreaBorder.CornerRadius = _restoreHostCorner;

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

    /// <summary>Shows or hides the caption strip and the InfoBar together. Collapsed rather than
    /// hidden: a hidden strip would still reserve its 32 px, which is exactly what full screen is
    /// meant to give back.</summary>
    private void ShowChrome(bool visible) =>
        Chrome.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The remote session asked for full screen itself — <c>ContainerHandledFullScreen</c>
    /// makes that the container's decision, and the container is this window.</summary>
    private void OnFullScreenRequested(bool enter) => SetFullScreen(enter);

    // ---------------------------------------------------------------- caption

    /// <summary>
    /// Drags the window by its strip. Refused on a double-click and on a maximized window: neither
    /// has anything to drag, and <c>DragMove</c> throws when the button is no longer down.
    /// </summary>
    /// <remarks>
    /// <c>DragMove</c> runs Windows' own modal move loop and only returns once the button is up, so
    /// this method <em>is</em> the whole gesture: <see cref="CaptionDragMoved"/> is raised from
    /// <see cref="OnLocationChanged"/> for as long as the loop runs, and
    /// <see cref="CaptionDragEnded"/> exactly where it ends. That is a far steadier signal than
    /// <c>Deactivated</c> or a <c>MouseLeftButtonUp</c> the move loop never delivers.
    /// </remarks>
    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1 || WindowState != WindowState.Normal)
        {
            return;
        }

        _captionDragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released between the event and this call. Nothing to drag, nothing to
            // report: the window simply stays where it is.
        }
        finally
        {
            _captionDragging = false;
        }

        // Raised even when DragMove refused: the drag ended either way, and whoever is listening has
        // something on screen to take down.
        CaptionDragEnded?.Invoke(this);
    }

    /// <summary>Every move of the window; only the ones that belong to a caption drag are passed on.
    /// A programmatic move — full screen, or the shell placing the window — is not a gesture and must
    /// not look like the user offering the session back.</summary>
    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_captionDragging)
        {
            CaptionDragMoved?.Invoke(this);
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
        // First: the two refreshes below write into a caption and an InfoBar that full screen has
        // collapsed, and this is what puts them back on screen.
        LeaveFullScreenUnlessConnected();
        RefreshCaption();
        RefreshInfoBar();
    }

    /// <summary>
    /// Ends full screen as soon as the session stops being connected — a drop, a retry, a failure, a
    /// close. What was hidden is exactly what the user now needs: the reason in the InfoBar, and
    /// <em>Reconnect</em> / <em>Copy diagnostics</em> in the strip.
    /// </summary>
    /// <remarks>
    /// Driven by <see cref="SessionTabViewModel.Changed"/>, which this window already subscribes to —
    /// no polling. Coming back is deliberately not automatic: a session that reconnects stays in the
    /// window it was given, and F11 puts it back edge to edge when the user asks for it.
    /// </remarks>
    private void LeaveFullScreenUnlessConnected()
    {
        if (_isFullScreen && _tab.State != SessionState.Connected)
        {
            SetFullScreen(false);
        }
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

    /// <summary>Reports the session's state in the one place RemoteDeck reports anything. Wording,
    /// severity rule and resource keys are shared with the shell (§6.4): a session says the same
    /// thing whether it is docked or detached.</summary>
    private void RefreshInfoBar() => SessionStatusPresenter.Report(StatusBar, _tab);

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
