using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Diagnostics;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Rdp;
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.App.Rdp;

/// <summary>
/// One RDP session — one tab: its own hosting surface, its own control instance, its own state
/// machine, its own reconnection countdown and its own dynamic-resolution loop.
/// </summary>
/// <remarks>
/// <para>
/// Composition: the session owns one <c>WindowsFormsHost</c> (<see cref="Host"/>, handed to the
/// shell so it can put it in its grid), one <see cref="RdpAxHost"/> parented to it, and one
/// <see cref="RdpSessionHost"/> built on top. The first two exist from construction — the shell
/// needs a visual to insert — while the OCX itself is created in <see cref="StartAsync"/>: an
/// <c>AxHost</c> only produces its COM object once it has a window handle, which requires
/// <see cref="Host"/> to already be in the visual tree. Hence the contract: add
/// <see cref="Host"/> to a container first, call <see cref="StartAsync"/> second.
/// </para>
/// <para>
/// Connecting is not done here. The <c>supplyAndConnect</c> delegate handed to the constructor is
/// the shell's: it applies the settings, lends the secret through the vault and calls
/// <see cref="RdpSessionHost.Connect"/>. It is re-invoked identically for every retry, so the
/// session never has to keep a credential — or anything derived from one — alive between attempts.
/// </para>
/// <para>
/// Threading: everything below runs on the UI thread. The two events coming from the control's COM
/// sinks are re-posted through <see cref="Dispatcher.BeginInvoke(Delegate, object?[])"/> — not
/// because <see cref="RdpSessionHost"/> raises them off-thread (it does not), but so that state
/// changes are ordered by a single queue whatever the control does.
/// </para>
/// <para>
/// Accessibility note: <see cref="RdpSessionHost"/> is internal, so the constructor's delegate type
/// is internal and this class cannot be public (CS0051). It lives in the same assembly as its only
/// consumer, the shell.
/// </para>
/// </remarks>
internal sealed class RdpSession : IDisposable
{
    /// <summary>How long the window must stay still before the remote resolution follows it.</summary>
    private static readonly TimeSpan ResizeDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>Countdown granularity: the UI shows whole seconds.</summary>
    private static readonly TimeSpan CountdownTick = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How close to zero the countdown must get for the retry to fire. A <c>DispatcherTimer</c> never
    /// ticks early but regularly ticks a few milliseconds late, so the tick that lands on the due date
    /// sees a few milliseconds still to go; comparing against zero would push a 2 s backoff to 3 s.
    /// Half a tick is the break-even point — anything below it is closer to now than to the next tick.
    /// </summary>
    private static readonly TimeSpan CountdownTolerance = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Smallest remote desktop this session will ask for, mirroring the floor
    /// <c>ShellWindow</c> already applies to the initial size: a pane dragged almost shut must
    /// not turn into a 120×40 desktop, nor burn the one-shot SmartSizing fallback on a refusal.
    /// </summary>
    private const int MinimumRemoteWidth = 640;
    private const int MinimumRemoteHeight = 480;

    /// <summary>
    /// Ceiling on either side of the remote desktop. Not a limit quoted from the control's
    /// documentation — an enforced bound, so an absurd layout or a bogus DPI scale cannot ask for a
    /// desktop no server would grant and cost the session its dynamic resolution.
    /// </summary>
    private const int MaximumRemoteSide = 8192;

    /// <summary>
    /// Resizes tried before giving up on dynamic resolution. One refusal is not a verdict: the
    /// remote desktop can still be settling just after logon, so the first failure buys a retry.
    /// </summary>
    private const int MaxDisplayAttempts = 2;

    /// <summary>Wait between a refused resize and the single retry that decides the fallback.</summary>
    private static readonly TimeSpan DisplayRetryDelay = TimeSpan.FromSeconds(2);

    private readonly RdpControlVersion _version;
    private readonly Func<RdpSessionHost, Task> _supplyAndConnect;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _resizeTimer;
    private readonly RdpAxHost _ax;

    private RdpSessionHost? _sessionHost;
    private DateTime _retryDueUtc;
    private int _lastExtendedReason;

    /// <summary>Set once <see cref="RdpSessionHost.UpdateDisplay"/> has been refused twice: from then
    /// on the control scales the image instead, and no further resize is attempted for this session.</summary>
    private bool _smartSizingFallback;

    /// <summary>
    /// True between <c>OnLoginComplete</c> and the next disconnect or attempt — the only window in
    /// which the control accepts a resolution change. Asking earlier answers <c>E_UNEXPECTED</c>.
    /// </summary>
    private bool _loggedOn;

    /// <summary>Consecutive refused resizes; at <see cref="MaxDisplayAttempts"/> the session gives up.</summary>
    private int _displayFailures;

    /// <summary>Remote size the running attempt was configured with, so the logon can tell whether
    /// the window has moved since — and only then spend a resize on it.</summary>
    private int _requestedWidth;
    private int _requestedHeight;

    private bool _disposed;

    /// <param name="connection">The saved row this session serves. Read-only here.</param>
    /// <param name="version">Control version the shell selected from the catalog.</param>
    /// <param name="supplyAndConnect">
    /// Configures the control, lends the secret and calls <c>Connect()</c>. Invoked for the first
    /// attempt and for every retry. Throwing out of it fails the attempt (state
    /// <see cref="SessionState.Failed"/>, message kept in <see cref="LastWindowsDescription"/>).
    /// </param>
    public RdpSession(Connection connection, RdpControlVersion version, Func<RdpSessionHost, Task> supplyAndConnect)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(supplyAndConnect);

        Connection = connection;
        _version = version;
        _supplyAndConnect = supplyAndConnect;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _ax = new RdpAxHost(version);
        Host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = _ax };
        Host.SizeChanged += OnHostSizeChanged;

        _retryTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = CountdownTick };
        _retryTimer.Tick += OnRetryTick;

        _resizeTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = ResizeDebounce };
        _resizeTimer.Tick += OnResizeTick;
    }

    /// <summary>The saved connection this session serves.</summary>
    public Connection Connection { get; }

    /// <summary>
    /// The session's visual. Available from construction and stable for the session's whole life;
    /// the shell adds it to its container before <see cref="StartAsync"/> and removes it after
    /// <see cref="CloseAsync"/>.
    /// </summary>
    public System.Windows.Forms.Integration.WindowsFormsHost Host { get; }

    /// <summary>Current state. Written on the UI thread only.</summary>
    public SessionState State { get; private set; } = SessionState.Idle;

    /// <summary>
    /// Automatic reconnection attempts consumed since the last successful connection, 0 to
    /// <see cref="ReconnectPolicy.MaxAttempts"/>. Reset by a successful connection and by a
    /// user-requested reconnection that is not skipping a running countdown.
    /// </summary>
    public int Attempt { get; private set; }

    /// <summary>Time left before the scheduled retry, rounded up to the second; <c>null</c> when no
    /// countdown is running.</summary>
    public TimeSpan? NextRetryIn { get; private set; }

    /// <summary>
    /// What the last disconnect code meant, or <c>null</c> if the session never dropped. Deliberately
    /// <em>not</em> cleared by a successful reconnection: a user copying diagnostics after the session
    /// came back still wants to know what took it down.
    /// </summary>
    public DisconnectDescription? LastDisconnect { get; private set; }

    /// <summary>
    /// Windows' own wording for the last failure: <c>GetErrorDescription()</c> after a disconnect,
    /// or the exception message when <c>supplyAndConnect</c> itself threw.
    /// </summary>
    public string? LastWindowsDescription { get; private set; }

    /// <summary>Raised on every state change and on every countdown tick. UI thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Creates the control and runs the first connection attempt. <see cref="Host"/> must already be
    /// in the visual tree, otherwise the OCX has no handle to be created on.
    /// </summary>
    public Task StartAsync()
    {
        if (_disposed || State is SessionState.Closing or SessionState.Closed)
        {
            return Task.CompletedTask;
        }

        if (State is SessionState.Connecting or SessionState.Connected or SessionState.Reconnecting)
        {
            // Already on its way; a second Connect() on a connecting control is a COM error.
            return Task.CompletedTask;
        }

        Attempt = 0;
        return RunAttemptAsync(SessionState.Connecting);
    }

    /// <summary>
    /// Reconnects now: cancels any countdown and attempts immediately. Requesting it from
    /// <see cref="SessionState.Failed"/> or <see cref="SessionState.Idle"/> hands the session a fresh
    /// retry budget; requesting it during a countdown only skips the wait and keeps the budget.
    /// </summary>
    public Task ReconnectNowAsync()
    {
        if (_disposed || State is SessionState.Closing or SessionState.Closed)
        {
            return Task.CompletedTask;
        }

        if (_sessionHost?.IsConnected == true)
        {
            // Nothing to reconnect. Guard, not policy: the shell only offers the button in
            // Failed/Idle, but Connect() on a live control is undefined behaviour.
            ProbeLog.Write("session", $"'{Connection.Name}': reconnect ignored, already connected");
            return Task.CompletedTask;
        }

        StopCountdown();

        if (State != SessionState.Interrupted)
        {
            Attempt = 0;
        }

        return RunAttemptAsync(SessionState.Reconnecting);
    }

    /// <summary>
    /// Gives up on the scheduled reconnection: the countdown stops and the session is declared
    /// failed. Harmless outside a countdown.
    /// </summary>
    /// <remarks>
    /// Cancelling while a retry is already in flight cannot recall it — if that attempt succeeds,
    /// <c>OnConnected</c> still moves the session to <see cref="SessionState.Connected"/>, which is
    /// the outcome the user wanted anyway.
    /// </remarks>
    public void CancelReconnect()
    {
        if (State is not (SessionState.Interrupted or SessionState.Reconnecting))
        {
            return;
        }

        StopCountdown();
        ProbeLog.Write("session", $"'{Connection.Name}': reconnection cancelled by the user");
        SetState(SessionState.Failed);
    }

    /// <summary>
    /// Closes the session following the §6.5 protocol (<see cref="RdpSessionHost.CloseAsync"/>) and
    /// disposes everything it owns. Terminal: the session is <see cref="SessionState.Closed"/>
    /// afterwards and cannot be restarted.
    /// </summary>
    public async Task CloseAsync(TimeSpan timeout)
    {
        if (State is SessionState.Closed)
        {
            return;
        }

        StopCountdown();
        _resizeTimer.Stop();
        SetState(SessionState.Closing);

        var host = _sessionHost;
        if (host is not null)
        {
            try
            {
                await host.CloseAsync(timeout).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // A control that refuses to close must not keep the tab — or the shell's
                // close-all — hostage. Dispose below tears it down regardless.
                ProbeLog.Write("close", $"'{Connection.Name}': CloseAsync failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        SetState(SessionState.Closed);
        Dispose();
    }

    /// <summary>
    /// Everything known about this session's last failure, as plain text for <em>Copy diagnostics</em>.
    /// </summary>
    public string BuildDiagnostics()
    {
        var lines = new List<string>
        {
            "RemoteDeck session diagnostics",
            $"Connection          : {Connection.Name}",
            $"Host                : {Connection.Host}:{Connection.Port}",
            $"Display mode        : {Connection.DisplayMode}",
            $"Control version     : {_version.Label}",
            $"State               : {State}",
            $"Reconnect attempts  : {Attempt} of {ReconnectPolicy.MaxAttempts}",
        };

        if (NextRetryIn is { } remaining)
        {
            lines.Add($"Next retry in       : {remaining.TotalSeconds:F0} s");
        }

        if (LastDisconnect is { } disconnect)
        {
            lines.Add($"Last disconnect code: {disconnect.Reason} ({disconnect.Category})");
            lines.Add($"Meaning             : {disconnect.Title}");
            lines.Add($"Extended reason     : {_lastExtendedReason}");
        }
        else
        {
            lines.Add("Last disconnect code: none recorded");
        }

        lines.Add($"Windows description : {LastWindowsDescription ?? "(none)"}");
        lines.Add($"Logged on           : {(_loggedOn ? "yes" : "no")}");
        lines.Add($"Requested desktop   : {_requestedWidth}x{_requestedHeight}");
        lines.Add($"Display failures    : {_displayFailures} of {MaxDisplayAttempts}");
        lines.Add($"SmartSizing fallback: {(_smartSizingFallback ? "active" : "not needed")}");
        lines.Add($"Generated (UTC)     : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Runs one attempt: makes sure the control exists, then hands it to the shell's
    /// <c>supplyAndConnect</c>. Never throws — it is awaited by <see cref="StartAsync"/> but also
    /// started fire-and-forget by the countdown.
    /// </summary>
    private async Task RunAttemptAsync(SessionState phase)
    {
        SetState(phase);

        // A new attempt means a new remote desktop: nothing known about the previous one carries
        // over, and the geometry the shell is about to configure is recorded so the logon can tell
        // whether the window moved in between.
        _loggedOn = false;
        _displayFailures = 0;
        _resizeTimer.Stop();
        (_requestedWidth, _requestedHeight) = TargetSize();

        RdpSessionHost host;
        try
        {
            host = EnsureSessionHost();
        }
        catch (Exception ex)
        {
            FailAttempt("control creation", ex);
            return;
        }

        try
        {
            await _supplyAndConnect(host).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            FailAttempt("connect", ex);
        }
    }

    /// <summary>
    /// Creates the OCX and the façade on first use. Requires <see cref="Host"/> to be in the visual
    /// tree: <c>AxHost</c> materialises its COM object with its window handle.
    /// </summary>
    private RdpSessionHost EnsureSessionHost()
    {
        if (_sessionHost is not null)
        {
            return _sessionHost;
        }

        _ax.CreateControl();
        var host = new RdpSessionHost(_ax);
        host.Connected += OnHostConnected;
        host.LoggedOn += OnHostLoggedOn;
        host.Disconnected += OnHostDisconnected;
        _sessionHost = host;
        ProbeLog.Write("session", $"'{Connection.Name}': control v{_version.Label} created");
        return host;
    }

    /// <summary>An attempt that never reached the wire: no disconnect code exists, so the exception
    /// message is what the user gets.</summary>
    private void FailAttempt(string stage, Exception ex)
    {
        // A throw out of supplyAndConnect consumes an attempt. For a retry it was already counted
        // when the countdown was armed; for the very first connection it is counted here.
        if (Attempt == 0)
        {
            Attempt = 1;
        }

        LastWindowsDescription = ex.Message;
        StopCountdown();
        ProbeLog.Write("session", $"'{Connection.Name}': {stage} failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        SetState(SessionState.Failed);
    }

    private void OnHostConnected() => Post(() =>
    {
        StopCountdown();
        Attempt = 0;
        SetState(SessionState.Connected);

        // Deliberately no resize here. At OnConnected the remote desktop does not exist yet and
        // UpdateSessionDisplaySettings answers E_UNEXPECTED (0x8000FFFF) — observed on every single
        // connection during the lot-4 human probe, which is what silently degraded every Dynamic
        // session to a stretched image. OnLoginComplete is the earliest usable moment.
    });

    /// <summary>
    /// The remote desktop is up. This — not <c>OnConnected</c> — is where the first resize is
    /// attempted, and only if the window has actually moved since the attempt was configured: in
    /// the common case the session already asked for the right size at connection time.
    /// </summary>
    private void OnHostLoggedOn() => Post(() =>
    {
        _loggedOn = true;
        _displayFailures = 0;

        if (_smartSizingFallback || Connection.DisplayMode != DisplayMode.Dynamic)
        {
            return;
        }

        var (width, height) = TargetSize();
        if (width == _requestedWidth && height == _requestedHeight)
        {
            return;
        }

        ProbeLog.Write("display", $"'{Connection.Name}': window is {width}x{height}, session was asked for {_requestedWidth}x{_requestedHeight}; updating");
        ApplyDisplaySize();
    });

    private void OnHostDisconnected(RdpDisconnectInfo info) => Post(() => HandleDisconnected(info));

    private void HandleDisconnected(RdpDisconnectInfo info)
    {
        var description = DisconnectReason.Describe(info.Reason);
        LastDisconnect = description;
        _lastExtendedReason = info.ExtendedReason;
        LastWindowsDescription = info.Description;
        _loggedOn = false;
        _resizeTimer.Stop();

        // Closed as well as Closing: this handler is posted, so CloseAsync's await may already have
        // resumed and moved the session on by the time it runs.
        if (State is SessionState.Closing or SessionState.Closed)
        {
            SetState(SessionState.Closed);
            return;
        }

        if (!description.IsError)
        {
            // Codes 0–3 (spec §6.4): the session ended on purpose. The tab stays, with Reconnect.
            StopCountdown();
            ProbeLog.Write("session", $"'{Connection.Name}': disconnected normally (code {info.Reason} — {description.Title})");
            SetState(SessionState.Idle);
            return;
        }

        if (ReconnectPolicy.ShouldReconnect(info.Reason)
            && Attempt < ReconnectPolicy.MaxAttempts
            && ReconnectPolicy.DelayFor(Attempt + 1) is { } delay)
        {
            Attempt++;
            _retryDueUtc = DateTime.UtcNow + delay;
            NextRetryIn = delay;
            ProbeLog.Write("session", $"'{Connection.Name}': dropped (code {info.Reason} — {description.Title}); attempt {Attempt}/{ReconnectPolicy.MaxAttempts} in {delay.TotalSeconds:F0} s");
            SetState(SessionState.Interrupted);
            _retryTimer.Start();
            return;
        }

        StopCountdown();
        ProbeLog.Write("session", $"'{Connection.Name}': failed (code {info.Reason} — {description.Title}, extended {info.ExtendedReason})");
        SetState(SessionState.Failed);
    }

    private void OnRetryTick(object? sender, EventArgs e)
    {
        var remaining = _retryDueUtc - DateTime.UtcNow;
        if (remaining > CountdownTolerance)
        {
            // Rounded up so the countdown shows "1 s" rather than "0 s" for its last second.
            NextRetryIn = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
            Changed?.Invoke();
            return;
        }

        StopCountdown();

        // Fire and forget: RunAttemptAsync swallows everything and reports through the state.
        _ = RunAttemptAsync(SessionState.Reconnecting);
    }

    private void StopCountdown()
    {
        _retryTimer.Stop();
        NextRetryIn = null;
    }

    /// <summary>
    /// Dynamic display mode only: every resize restarts a 300 ms debounce, so a window dragged
    /// across the screen produces one resolution change instead of a hundred.
    /// </summary>
    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_smartSizingFallback || !_loggedOn || Connection.DisplayMode != DisplayMode.Dynamic)
        {
            return;
        }

        // The interval is reassigned because the same timer also serves the post-failure retry.
        _resizeTimer.Stop();
        _resizeTimer.Interval = ResizeDebounce;
        _resizeTimer.Start();
    }

    private void OnResizeTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        ApplyDisplaySize();
    }

    /// <summary>
    /// Asks the live session to match the hosting surface. A refusal buys one retry
    /// <see cref="DisplayRetryDelay"/> later on the same timer; only a second refusal switches the
    /// session to SmartSizing for good, since a control that has settled and still refuses will
    /// refuse every drag of the window afterwards.
    /// </summary>
    private void ApplyDisplaySize()
    {
        if (_disposed || _smartSizingFallback || _sessionHost is null)
        {
            return;
        }

        if (!_loggedOn || Connection.DisplayMode != DisplayMode.Dynamic || State != SessionState.Connected)
        {
            return;
        }

        var (width, height) = TargetSize();
        uint scalePercent = (uint)Math.Max(100, Math.Round(VisualTreeHelper.GetDpi(Host).DpiScaleX * 100));

        if (_sessionHost.UpdateDisplay(width, height, scalePercent))
        {
            _displayFailures = 0;
            _requestedWidth = width;
            _requestedHeight = height;
            return;
        }

        _displayFailures++;
        if (_displayFailures < MaxDisplayAttempts)
        {
            ProbeLog.Write("display", $"'{Connection.Name}': display update retry in {DisplayRetryDelay.TotalSeconds:F0} s");
            _resizeTimer.Stop();
            _resizeTimer.Interval = DisplayRetryDelay;
            _resizeTimer.Start();
            return;
        }

        _smartSizingFallback = true;
        try
        {
            _sessionHost.EnableSmartSizingFallback();
            ProbeLog.Write("display", $"'{Connection.Name}': fallback to SmartSizing after {_displayFailures} failures");
        }
        catch (Exception ex)
        {
            // Setting SmartSizing is a COM call like any other: it may refuse too. The session then
            // simply keeps the resolution it negotiated at connection time.
            ProbeLog.Write("display", $"'{Connection.Name}': SmartSizing fallback failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    /// <summary>
    /// Remote size this session should ask for: the hosting surface in physical pixels, floored at
    /// <see cref="MinimumRemoteWidth"/>×<see cref="MinimumRemoteHeight"/> (as <c>ShellWindow</c>
    /// already does for the initial size, so a pane dragged almost shut does not become a 120×40
    /// desktop), capped at <see cref="MaximumRemoteSide"/>, and rounded <em>down</em> to even
    /// numbers on both sides.
    /// </summary>
    /// <remarks>
    /// The even rounding is empirical, not quoted: the refusal seen during the probe asked for
    /// 640×609, and an odd side is the one unusual thing about that request. Even sides cost
    /// nothing; the retry above covers the case where they were not the cause.
    /// </remarks>
    private (int Width, int Height) TargetSize()
    {
        var dpi = VisualTreeHelper.GetDpi(Host);
        return (EvenSide(Host.ActualWidth * dpi.DpiScaleX, MinimumRemoteWidth),
                EvenSide(Host.ActualHeight * dpi.DpiScaleY, MinimumRemoteHeight));
    }

    /// <summary>Clamps one side to the allowed range and rounds it down to an even number.</summary>
    private static int EvenSide(double physicalPixels, int minimum)
    {
        // A surface that has never been laid out measures NaN, which survives Math.Clamp and lands
        // on int.MinValue through the cast: it is worth the floor, not a negative desktop.
        int value = double.IsFinite(physicalPixels) ? (int)Math.Floor(physicalPixels) : minimum;
        value = Math.Clamp(value, minimum, MaximumRemoteSide);
        return value - (value % 2);
    }

    /// <summary>Queues <paramref name="action"/> on the session's dispatcher; see the threading note
    /// on the class.</summary>
    private void Post(Action action) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed)
        {
            return;
        }

        action();
    });

    /// <summary>Moves to <paramref name="state"/> and notifies. Notification is unconditional — the
    /// countdown and the attempt counter change without the state doing so — logging is not.</summary>
    private void SetState(SessionState state)
    {
        if (State != state)
        {
            State = state;
            ProbeLog.Write("session", $"'{Connection.Name}' → {state}");
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _retryTimer.Stop();
        _retryTimer.Tick -= OnRetryTick;
        _resizeTimer.Stop();
        _resizeTimer.Tick -= OnResizeTick;
        Host.SizeChanged -= OnHostSizeChanged;

        if (_sessionHost is { } host)
        {
            host.Connected -= OnHostConnected;
            host.LoggedOn -= OnHostLoggedOn;
            host.Disconnected -= OnHostDisconnected;
            host.Dispose();
            _sessionHost = null;
        }

        try
        {
            // Detach before disposing: the shell removes Host from its container on its own
            // schedule, and an emptied WindowsFormsHost left in the tree is harmless whereas one
            // holding a disposed child is not.
            Host.Child = null;
            _ax.Dispose();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"'{Connection.Name}': control disposal failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }
}
