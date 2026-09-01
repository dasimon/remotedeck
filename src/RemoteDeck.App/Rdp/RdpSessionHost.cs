using System.Reflection;
using MSTSCLib;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Model;

namespace RemoteDeck.App.Rdp;

internal sealed record RdpDisconnectInfo(int Reason, int ExtendedReason, string Description);

/// <summary>
/// Thin façade over one RDP control instance: configuration, connect/disconnect, event
/// forwarding. State machine and reconnect policy arrive in lot 4; this is the probe-grade version.
/// </summary>
internal sealed class RdpSessionHost : IDisposable
{
    private readonly RdpAxHost _host;
    private readonly IMsRdpClient10 _client;
    private readonly IMsTscAxEvents_Event _events;
    private readonly IMsTscAxEvents_OnConfirmCloseEventHandler _onConfirmClose;
    private TaskCompletionSource? _closed;
    private bool _disposed;
    // See LogDisplayFailure: outside the logged-on window, one [display] line per session rather
    // than one per failed resize.
    private bool _displayFailureLogged;
    // True between OnLoginComplete and the next OnConnected/OnDisconnected.
    private bool _loggedOn;

    public event Action<string>? StatusChanged;
    public event Action<RdpDisconnectInfo>? Disconnected;

    /// <summary>Raised from the OnConnected sink, for the state machine. StatusChanged is unaffected.</summary>
    public event Action? Connected;

    /// <summary>
    /// Raised from the OnLoginComplete sink: the remote desktop exists from here on. It is the
    /// earliest moment at which <see cref="UpdateDisplay"/> is accepted — asking at OnConnected
    /// answers <c>E_UNEXPECTED</c> (0x8000FFFF), which is what made every Dynamic session fall back
    /// to SmartSizing during the lot-4 probe.
    /// </summary>
    public event Action? LoggedOn;

    /// <summary>Raised when the user asks for full screen (Ctrl+Alt+Break) and the container owns the switch.</summary>
    public event Action? RequestGoFullScreen;

    /// <summary>Raised when the user asks to leave full screen and the container owns the switch.</summary>
    public event Action? RequestLeaveFullScreen;

    public bool IsConnected => _client.Connected != 0;

    public RdpSessionHost(RdpAxHost host)
    {
        _host = host;
        _client = (IMsRdpClient10)host.Ocx;
        _events = (IMsTscAxEvents_Event)host.Ocx;

        // R2 probe: does subscribing to the COM event interface work through the generated interop?
        // Every sink body goes through Sink(): an exception escaping into the control's callback
        // is undefined behaviour, so it is logged and swallowed instead.
        _events.OnConnecting += () => Sink("OnConnecting", () => Raise("Connecting…"));
        _events.OnConnected += () => Sink("OnConnected", () =>
        {
            // Each new session is entitled to its own single [display] failure line, and the
            // desktop it will get is not logged on yet.
            _displayFailureLogged = false;
            _loggedOn = false;
            Raise("Connected");
            Connected?.Invoke();
        });
        _events.OnLoginComplete += () => Sink("OnLoginComplete", () =>
        {
            // The remote desktop is up: resolution changes start being accepted here, and the
            // [display] budget is re-armed so the resizes that follow are all visible.
            _loggedOn = true;
            _displayFailureLogged = false;
            Raise("Logged on");
            LoggedOn?.Invoke();
        });
        _events.OnAuthenticationWarningDisplayed += () => Sink("OnAuthenticationWarningDisplayed", () => ProbeLog.Write("R5", "OnAuthenticationWarningDisplayed fired (certificate warning shown by the control)"));
        _events.OnAuthenticationWarningDismissed += () => Sink("OnAuthenticationWarningDismissed", () => ProbeLog.Write("R5", "OnAuthenticationWarningDismissed fired"));
        _events.OnLogonError += error => Sink("OnLogonError", () => ProbeLog.Write("session", $"OnLogonError lError={error}"));
        _events.OnFatalError += code => Sink("OnFatalError", () => ProbeLog.Write("session", $"OnFatalError errorCode={code}"));
        _events.OnDisconnected += OnDisconnected;

        // Container-handled full screen: Ctrl+Alt+Break stops toggling the control's own full-screen
        // window and raises these instead, so RemoteDeck keeps its own chrome (design §5).
        _events.OnRequestGoFullScreen += () => Sink("OnRequestGoFullScreen", () => RequestGoFullScreen?.Invoke());
        _events.OnRequestLeaveFullScreen += () => Sink("OnRequestLeaveFullScreen", () => RequestLeaveFullScreen?.Invoke());

        // RequestClose contract: if the user is logged on, the control asks before closing.
        // Returning true lets it disconnect; OnDisconnected then completes the close.
        // Kept in a field so Dispose() can unsubscribe it.
        _onConfirmClose = () => Sink("OnConfirmClose", () =>
        {
            ProbeLog.Write("close", "OnConfirmClose → allowing");
            return true;
        }, fallback: true);
        _events.OnConfirmClose += _onConfirmClose;

        ProbeLog.Write("R2", "Subscribed to IMsTscAxEvents_Event via TlbImp-generated interop");
    }

    /// <summary>
    /// Applies one connection's settings to the control. <paramref name="hostWidth"/> and
    /// <paramref name="hostHeight"/> are the physical pixel size of the surface hosting the control;
    /// they are the requested remote resolution unless the connection pins one.
    /// </summary>
    /// <remarks>
    /// The initial resolution is decided here; following the window as it is resized afterwards
    /// is <see cref="UpdateDisplay"/>'s job.
    /// <para>
    /// Safe to call again before a reconnect: it only assigns properties. The two that may be set
    /// on a disconnected control only — <c>SecuredSettings2.KeyboardHookMode</c> and
    /// <c>SecuredSettings2.AudioRedirectionMode</c> — are fine here, because that is exactly the
    /// state the control is in between two connection attempts.
    /// </para>
    /// </remarks>
    public void Configure(RdpConnectionSettings settings, int hostWidth, int hostHeight)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Both non-Dynamic modes pin the remote resolution to the saved one — ConnectionRules
        // already requires FixedWidth/FixedHeight there. What separates them is what happens when
        // the window does not match: Fixed scrolls, Scaled fits the image through SmartSizing.
        // Only Dynamic asks the host surface for its size. The fallback to the host size covers a
        // row written before that rule existed, or one whose stored size is unusable.
        bool pinned = settings.DisplayMode != DisplayMode.Dynamic;

        _client.Server = settings.Host;
        _client.UserName = settings.UserName;
        _client.Domain = settings.Domain ?? string.Empty;
        _client.DesktopWidth = pinned && settings.FixedWidth is > 0 ? settings.FixedWidth.Value : hostWidth;
        _client.DesktopHeight = pinned && settings.FixedHeight is > 0 ? settings.FixedHeight.Value : hostHeight;
        _client.ColorDepth = 32;

        var advanced = _client.AdvancedSettings9;      // IMsRdpClientAdvancedSettings8, inherits all previous
        advanced.RDPPort = settings.Port;
        // Kept unconditionally true: with no credential attached this is what makes the control put
        // up its own CredSSP prompt instead of failing, and RemoteDeck has no manual entry any more.
        advanced.EnableCredSspSupport = true;
        // Off, deliberately. The control ships with a reconnection loop of its own, on by default:
        // when a session drops it puts up its "Reconnecting... 1 of 5" dialog and only reports
        // OnDisconnected once its own five attempts are spent. Left enabled, the two mechanisms
        // stack (5 + 5) and RemoteDeck's UI lies for the whole of the control's phase — the tab
        // still reads "Connected" while the session is in fact down. ReconnectPolicy (spec §6.3) is
        // therefore the single reconnection mechanism: its six retryable codes, its 2/5/10/30/60 s
        // backoff, its visible and cancellable countdown, and — something the control's own loop
        // cannot do — the secret re-lent by the vault for every attempt (§5.2).
        // IMsRdpClientAdvancedSettings2::EnableAutoReconnect; settable on a disconnected control
        // only, which is exactly the state Configure runs in.
        // https://learn.microsoft.com/windows/win32/termserv/imsrdpclientadvancedsettings2-enableautoreconnect
        advanced.EnableAutoReconnect = false;
        EnableContainerHandledFullScreen();
        advanced.RedirectClipboard = settings.RedirectClipboard;   // IMsRdpClientAdvancedSettings5
        advanced.RedirectDrives = settings.RedirectDrives;         // IMsRdpClientAdvancedSettings
        advanced.RedirectPrinters = settings.RedirectPrinters;     // IMsRdpClientAdvancedSettings
        advanced.ConnectToAdministerServer = settings.AdminSession; // IMsRdpClientAdvancedSettings6
        advanced.SmartSizing = settings.DisplayMode == DisplayMode.Scaled;

        if (settings.AuthenticationLevel is int level)
        {
            // 0 = connect and don't warn, 1 = do not connect if authentication fails,
            // 2 = attempt authentication and prompt on failure (verified value, see plan constraints).
            advanced.AuthenticationLevel = (uint)level;
        }

        // Windowed use: Windows key combos stay local unless full screen (documented value 2 = default).
        _client.SecuredSettings2.KeyboardHookMode = 2;
        // 0 = redirect sounds to the client (default), 1 = play them at the remote computer,
        // 2 = do not play. RemoteDeck offers the on/off pair only, hence 0 or 2.
        // https://learn.microsoft.com/windows/win32/termserv/imsrdpclientsecuredsettings-autoredirectionmode
        _client.SecuredSettings2.AudioRedirectionMode = settings.RedirectAudio ? 0 : 2;

        if (settings.UseWebAccount)
        {
            TryEnableWebAccount();
        }
    }

    /// <summary>
    /// Hands the full-screen switch to the container. The documented "no effect" caveat applies to the
    /// scripting-safe coclass only; RemoteDeck uses MsRdpClient12NotSafeForScripting.
    /// https://learn.microsoft.com/windows/win32/termserv/imstscadvancedsettings-containerhandledfullscreen
    /// </summary>
    public bool EnableContainerHandledFullScreen()
    {
        try
        {
            // The interop types this COM BOOL as int (verified by reflection on
            // Interop.MSTSCLib.dll: IMsRdpClientAdvancedSettings8.ContainerHandledFullScreen is
            // System.Int32), hence 1 rather than true.
            _client.AdvancedSettings9.ContainerHandledFullScreen = 1;
            return true;
        }
        catch (Exception ex)
        {
            ProbeLog.Write("display", $"ContainerHandledFullScreen refused: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return false;
        }
    }

    /// <summary>R7 probe. Property name is NOT in Microsoft's documented list; we only observe.</summary>
    private void TryEnableWebAccount()
    {
        try
        {
            var extended = (IMsRdpExtendedSettings)_host.Ocx;
            object value = true;
            extended.set_Property("EnableRdsAadAuth", ref value);
            ProbeLog.Write("R7", "set_Property(\"EnableRdsAadAuth\", true) returned without error");
        }
        catch (Exception ex)
        {
            ProbeLog.Write("R7", $"set_Property(\"EnableRdsAadAuth\") failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    /// <summary>
    /// Resizes the running session to <paramref name="width"/> × <paramref name="height"/> physical
    /// pixels without reconnecting; <paramref name="scalePercent"/> is the host surface's DPI scale
    /// (100, 125, 150…). Returns false — and never throws — when the control is not connected or the
    /// call is refused, so the caller can fall back to <see cref="EnableSmartSizingFallback"/>.
    /// </summary>
    /// <remarks>
    /// <c>IMsRdpClient9::UpdateSessionDisplaySettings(ulDesktopWidth, ulDesktopHeight,
    /// ulPhysicalWidth, ulPhysicalHeight, ulOrientation, ulDesktopScaleFactor, ulDeviceScaleFactor)</c>
    /// https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/mt703457(v=vs.85)
    /// The TlbImp-generated wrapper returns void, so a failing HRESULT arrives as a COMException —
    /// hence the try/catch rather than a status check. <c>_client</c> is <c>IMsRdpClient10</c>, which
    /// derives from <c>IMsRdpClient9</c>, so the call is direct and needs no cast.
    /// Orientation is 0 (landscape); both scale factors carry the same host scale.
    /// </remarks>
    public bool UpdateDisplay(int width, int height, uint scalePercent)
    {
        try
        {
            if (!IsConnected)
            {
                LogDisplayFailure($"UpdateDisplay({width}x{height} @ {scalePercent}%) skipped: not connected");
                return false;
            }

            _client.UpdateSessionDisplaySettings(
                (uint)width,
                (uint)height,
                ToMillimetres(width, scalePercent),
                ToMillimetres(height, scalePercent),
                0,
                scalePercent,
                scalePercent);
            return true;
        }
        catch (Exception ex)
        {
            LogDisplayFailure($"UpdateDisplay({width}x{height} @ {scalePercent}%) failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Physical size, in millimetres, of a surface <paramref name="pixels"/> physical pixels wide
    /// at <paramref name="scalePercent"/> scaling: device-independent pixels = pixels ÷ (scale ÷ 100),
    /// inches = dip ÷ 96, millimetres = inches × 25.4 — hence pixels × 25.4 ÷ (96 × scale ÷ 100).
    /// A computed size is sent rather than 0 because the session derives its own DPI from it.
    /// </summary>
    private static uint ToMillimetres(int pixels, uint scalePercent)
    {
        // A caller with no scale information means "unscaled"; this also guards the division.
        double scale = scalePercent == 0 ? 100.0 : scalePercent;
        return (uint)Math.Round(Math.Max(0, pixels) * 25.4 / (96.0 * scale / 100.0));
    }

    /// <summary>
    /// Fallback when <see cref="UpdateDisplay"/> is refused: the control scales the remote image to
    /// the window instead of resizing the session. SmartSizing is settable while connected, so this
    /// takes effect without a reconnect.
    /// </summary>
    public void EnableSmartSizingFallback()
    {
        _client.AdvancedSettings9.SmartSizing = true;
        ProbeLog.Write("display", "SmartSizing fallback enabled");
    }

    /// <summary>
    /// While the session is logged on, every failure is written: the caller only ever makes two
    /// attempts before switching to SmartSizing and the second one is the interesting one.
    /// Outside that window — not connected, desktop not up — a refusal repeats on every drag of the
    /// window, so one line per session is enough. The budget is re-armed by OnConnected and by
    /// OnLoginComplete.
    /// </summary>
    private void LogDisplayFailure(string message)
    {
        if (!_loggedOn && _displayFailureLogged)
        {
            return;
        }

        _displayFailureLogged = true;
        ProbeLog.Write("display", message);
    }

    /// <summary>
    /// R5 probe: list every interop member that might expose server certificate data.
    /// Microsoft's documentation names no thumbprint API; rather than assert that, this
    /// measures it by enumerating the TlbImp-generated assembly. Nothing is instantiated —
    /// only member names are read — and every failure is logged, never thrown: the probe
    /// runs at startup and must not be able to take the shell down with it.
    /// </summary>
    public static void LogCertificateSurface()
    {
        try
        {
            var assembly = typeof(IMsRdpClient10).Assembly;

            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A COM interop assembly can fail to load individual types. Partial results
                // still answer R5, so keep the types that did load and say how many were lost.
                types = ex.Types;
                ProbeLog.Write("R5", $"GetTypes() partially failed: {types.Count(t => t is null)} of {types.Length} type(s) unloadable; scanning the rest");
            }

            var hits = types
                .Where(t => t is not null)
                .SelectMany(t => t!.GetMembers().Select(m => $"{t.Name}.{m.Name}"))
                .Where(n => n.Contains("Cert", StringComparison.OrdinalIgnoreCase)
                         || n.Contains("Thumb", StringComparison.OrdinalIgnoreCase)
                         || n.Contains("AuthenticationWarning", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            ProbeLog.Write("R5", hits.Length == 0
                ? "No interop member mentions Cert/Thumb/AuthenticationWarning"
                : $"{hits.Length} candidate member(s): {string.Join(", ", hits)}");
        }
        catch (Exception ex)
        {
            ProbeLog.Write("R5", $"Certificate-surface scan failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    public void PutPassword(nint bstr) => ComSecretPut.PutClearTextPassword(_host.Ocx, bstr);

    public void Connect()
    {
        Raise("Connect requested");
        _client.Connect();
    }

    public void Disconnect()
    {
        if (IsConnected)
        {
            _client.Disconnect();
        }
    }

    private void OnDisconnected(int reason) => Sink("OnDisconnected", () =>
    {
        _loggedOn = false;

        // Both reads are inside the try: a COM throw here must not skip the log line nor the
        // Disconnected event, or the UI would stay stuck with Connect disabled forever.
        int extended = 0;
        string description;
        try
        {
            extended = (int)_client.ExtendedDisconnectReason;
            description = _client.GetErrorDescription((uint)reason, (uint)extended);
        }
        catch (Exception ex)
        {
            description = $"(error details unavailable: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message})";
        }

        ProbeLog.Write("session", $"OnDisconnected reason={reason} extended={extended} \"{description}\"");
        Disconnected?.Invoke(new RdpDisconnectInfo(reason, extended, description));

        // Releases a CloseAsync waiter, if any. No-op when the disconnect was not requested by us.
        _closed?.TrySetResult();
    });

    /// <summary>
    /// Graceful shutdown per IMsRdpClient::RequestClose:
    /// controlCloseCanProceed (0) → nothing else to do; controlCloseWaitForEvents (1) → the
    /// control asks the session (OnConfirmClose) and disconnects, so wait for OnDisconnected up
    /// to <paramref name="timeout"/>, then force Disconnect().
    /// https://learn.microsoft.com/windows/win32/termserv/imsrdpclient-requestclose
    /// </summary>
    public async Task CloseAsync(TimeSpan timeout)
    {
        if (!IsConnected)
        {
            ProbeLog.Write("close", "Not connected; nothing to close");
            return;
        }

        // Defence in depth: the UI already refuses a second close while one is pending, but a
        // second RequestClose() would orphan the first waiter (spurious timeout + extra
        // Disconnect). Join the close already running instead of starting another one.
        var pending = _closed;
        if (pending is not null && !pending.Task.IsCompleted)
        {
            ProbeLog.Write("close", "CloseAsync already running; awaiting the existing one");
            if (!await WaitForCloseAsync(pending.Task, timeout).ConfigureAwait(true))
            {
                // No second Disconnect() here: the call that owns this waiter forces it.
                ProbeLog.Write("close", $"Timed out after {timeout.TotalSeconds:F0}s waiting for the close in flight");
            }

            return;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closed = closed;
        var status = _client.RequestClose();
        ProbeLog.Write("close", $"RequestClose → {status} ({(int)status})");

        if (status == ControlCloseStatus.controlCloseCanProceed)
        {
            // Nothing will complete this waiter — the control is not going to raise
            // OnDisconnected for a close it just told us can proceed. Leaving it pending would
            // make the *next* CloseAsync take the "already running" branch and wait out the full
            // timeout on a close that already happened.
            closed.TrySetResult();
            _closed = null;
            return;
        }

        if (await WaitForCloseAsync(closed.Task, timeout).ConfigureAwait(true))
        {
            ProbeLog.Write("close", "Closed gracefully (OnDisconnected received)");
        }
        else
        {
            ProbeLog.Write("close", $"Timed out after {timeout.TotalSeconds:F0}s; forcing Disconnect()");
            _client.Disconnect();
        }

        // Either way this close is over: clear the field so a later CloseAsync starts a fresh
        // one instead of joining a waiter nobody owns any more.
        if (ReferenceEquals(_closed, closed))
        {
            _closed = null;
        }
    }

    /// <summary>Waits for <paramref name="closed"/>; true if it completed within the timeout.</summary>
    private static async Task<bool> WaitForCloseAsync(Task closed, TimeSpan timeout)
    {
        var finished = await Task.WhenAny(closed, Task.Delay(timeout)).ConfigureAwait(true);
        return finished == closed;
    }

    private void Raise(string status) => Sink("StatusChanged", () =>
    {
        ProbeLog.Write("session", status);
        StatusChanged?.Invoke(status);
    });

    /// <summary>
    /// Runs one COM event sink body. Letting an exception unwind into the control's callback is
    /// undefined behaviour, so anything thrown here is logged and swallowed.
    /// </summary>
    private static void Sink(string name, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            try
            {
                ProbeLog.Write("sink", $"{name} handler threw {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            }
            catch
            {
                // Logging must never turn a swallowed sink exception back into a crash.
            }
        }
    }

    /// <summary>
    /// Same contract as <see cref="Sink(string, Action)"/> for a sink that must return a value
    /// to the control: on failure it answers <paramref name="fallback"/> instead of unwinding.
    /// </summary>
    private static T Sink<T>(string name, Func<T> body, T fallback)
    {
        T result = fallback;
        Sink(name, () => result = body());
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.OnDisconnected -= OnDisconnected;
        _events.OnConfirmClose -= _onConfirmClose;
    }
}
