using MSTSCLib;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Services;

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
    private bool _disposed;

    public event Action<string>? StatusChanged;
    public event Action<RdpDisconnectInfo>? Disconnected;

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
        _events.OnConnected += () => Sink("OnConnected", () => Raise("Connected"));
        _events.OnLoginComplete += () => Sink("OnLoginComplete", () => Raise("Logged on"));
        _events.OnAuthenticationWarningDisplayed += () => Sink("OnAuthenticationWarningDisplayed", () => ProbeLog.Write("R5", "OnAuthenticationWarningDisplayed fired (certificate warning shown by the control)"));
        _events.OnAuthenticationWarningDismissed += () => Sink("OnAuthenticationWarningDismissed", () => ProbeLog.Write("R5", "OnAuthenticationWarningDismissed fired"));
        _events.OnLogonError += error => Sink("OnLogonError", () => ProbeLog.Write("session", $"OnLogonError lError={error}"));
        _events.OnFatalError += code => Sink("OnFatalError", () => ProbeLog.Write("session", $"OnFatalError errorCode={code}"));
        _events.OnDisconnected += OnDisconnected;
        ProbeLog.Write("R2", "Subscribed to IMsTscAxEvents_Event via TlbImp-generated interop");
    }

    public void Configure(RdpConnectionProbeSettings settings, int desktopWidth, int desktopHeight)
    {
        _client.Server = settings.Host;
        _client.UserName = settings.UserName;
        _client.Domain = settings.Domain ?? string.Empty;
        _client.DesktopWidth = desktopWidth;
        _client.DesktopHeight = desktopHeight;
        _client.ColorDepth = 32;

        var advanced = _client.AdvancedSettings9;      // IMsRdpClientAdvancedSettings8, inherits all previous
        advanced.RDPPort = settings.Port;
        advanced.EnableCredSspSupport = true;
        advanced.RedirectClipboard = true;             // spec §2 default: clipboard on, everything else off
        advanced.RedirectDrives = false;
        advanced.RedirectPrinters = false;
        advanced.AuthenticationLevel = 2;              // attempt + prompt (verified value, see plan constraints)
        advanced.SmartSizing = false;

        // Windowed use: Windows key combos stay local unless full screen (documented value 2 = default).
        _client.SecuredSettings2.KeyboardHookMode = 2;

        if (settings.UseWebAccount)
        {
            TryEnableWebAccount();
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
    });

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _events.OnDisconnected -= OnDisconnected;
    }
}
