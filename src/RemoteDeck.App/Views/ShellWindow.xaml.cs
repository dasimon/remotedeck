using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Rdp;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Rdp;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// Lot-0 shell: a Fluent window hosting the Remote Desktop ActiveX control through a
/// <c>WindowsFormsHost</c>. Probes R4 (custom title bar + HWND host coexistence) and
/// R3 (per-monitor DPI), and drives one <see cref="RdpSessionHost"/> for R1, R2 and R7.
/// </summary>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in
// scope through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button,
// TextBox and friends ambiguous here.
public partial class ShellWindow : Wpf.Ui.Controls.FluentWindow
{
    private RdpAxHost? _ax;
    private RdpSessionHost? _session;
    private ShortcutInterceptor? _shortcuts;
    private bool _closeInProgress;
    private bool _reentrantCloseLogged;
    private bool _closeConfirmed;

    public ShellWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        HostInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_HOST") ?? "";
        UserInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_USER") ?? "";
        DomainInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_DOMAIN") ?? "";

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var version = RdpControlCatalog.Select(ClsidRegistry.IsUsable);
        if (version is null)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "No Remote Desktop control found",
                "None of the known mstscax.dll CLSIDs is registered on this machine.");
            ConnectButton.IsEnabled = false;
            return;
        }

        var ax = new RdpAxHost(version);
        try
        {
            RdpHost.Child = ax;
            ax.CreateControl();
        }
        catch (Exception ex)
        {
            // Being listed in HKCR\CLSID is not the same as being creatable: mstscax.dll
            // registers control versions its class factory then refuses
            // (CLASS_E_CLASSNOTAVAILABLE, 0x80040111). Surface it, never crash the shell.
            ProbeLog.Write("R4", $"control version {version.Label} ({version.Clsid:D}) is registered but not creatable: {ex.GetType().Name} 0x{ex.HResult:X8}");
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, $"RDP control v{version.Label} could not be created",
                $"The CLSID is registered but its class factory refused it (0x{ex.HResult:X8}). See {ProbeLog.Path}.");
            ConnectButton.IsEnabled = false;
            return;
        }

        _ax = ax;

        var dpi = VisualTreeHelper.GetDpi(this);
        ProbeLog.Write("R4", $"FluentWindow + WindowsFormsHost created; control version {version.Label} ({version.Clsid:D})");
        ProbeLog.Write("R3", $"Window DPI scale X={dpi.DpiScaleX:F2} Y={dpi.DpiScaleY:F2}");

        _session = new RdpSessionHost(_ax);
        // BeginInvoke, not Invoke: these are raised from a COM event sink, and a synchronous
        // marshal back to the UI thread would deadlock if the control ever raised off-thread.
        _session.StatusChanged += status => Dispatcher.BeginInvoke(() =>
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, status, ""));
        _session.Disconnected += info => Dispatcher.BeginInvoke(() =>
        {
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
            var severity = info.Reason == 3 // disconnectReasonByServer: not an error (spec §6.4)
                ? Wpf.Ui.Controls.InfoBarSeverity.Informational
                : Wpf.Ui.Controls.InfoBarSeverity.Error;
            ShowStatus(severity, $"Disconnected (reason {info.Reason}, extended {info.ExtendedReason})", info.Description);
        });

        // R6 probe: which of the three §7.3 mechanisms actually sees Ctrl+K / Ctrl+Tab while the
        // remote session holds keyboard focus. REMOTEDECK_PROBE_SHORTCUTS switches between them;
        // WpfThreadFilter is the default because it is the native WPF message pump, the one
        // WindowsFormsHost already routes through.
        var mechanismName = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_SHORTCUTS") ?? "WpfThreadFilter";
        var mechanism = Enum.Parse<ShortcutInterceptor.Mechanism>(mechanismName, ignoreCase: true);
        _shortcuts = new ShortcutInterceptor(mechanism);
        // BeginInvoke for the same reason as the session events: never block the thread that
        // raised the notification, here the message pump itself.
        _shortcuts.Triggered += shortcut => Dispatcher.BeginInvoke(() =>
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Success, $"{shortcut} intercepted", $"via {mechanism} — command palette arrives in lot 5"));

        // R5 probe: one reflection pass over the interop assembly, once per launch, to record
        // whether any member at all could hand us the server certificate.
        RdpSessionHost.LogCertificateSurface();

        ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, $"RDP control v{version.Label} ready", "Enter a host and press Connect.");
    }

    private void ShowStatus(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _ax is null)
        {
            return;
        }

        var settings = new RdpConnectionProbeSettings(
            Host: HostInput.Text.Trim(),
            Port: (int)(PortInput.Value ?? 3389),
            UserName: UserInput.Text.Trim(),
            Domain: string.IsNullOrWhiteSpace(DomainInput.Text) ? null : DomainInput.Text.Trim(),
            UseWebAccount: WebAccountInput.IsChecked == true);

        if (settings.Host.Length == 0)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Host required", "Enter a host name or address.");
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(640, (int)(RdpHost.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(480, (int)(RdpHost.ActualHeight * dpi.DpiScaleY));

        try
        {
            _session.Configure(settings, width, height);

            if (!settings.UseWebAccount)
            {
                // R1 probe: SecureString -> native BSTR -> vtable put -> zero+free. No managed string.
                // SecurePassword hands out a fresh copy on every read; dispose it.
                using var secure = PasswordInput.SecurePassword;
                nint bstr = Marshal.SecureStringToBSTR(secure);
                try
                {
                    _session.PutPassword(bstr);
                    ProbeLog.Write("R1", "ClearTextPassword set through IMsTscNonScriptable vtable with a native BSTR");
                }
                finally
                {
                    Marshal.ZeroFreeBSTR(bstr);
                }

                PasswordInput.Clear();
            }

            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;
            _session.Connect();
        }
        catch (Exception ex)
        {
            ConnectButton.IsEnabled = true;
            DisconnectButton.IsEnabled = false;
            ProbeLog.Write("session", $"Connect failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "Connect failed", $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
        }
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        _session?.Disconnect();
    }

    /// <summary>
    /// Closing the window is a two-pass affair (spec §6.5): the first pass cancels the close and
    /// runs the graceful <c>RequestClose</c> protocol so the server is told to end the session
    /// instead of being left with a zombie one; the second pass releases the COM objects.
    /// <c>async void</c> is the only shape available to an event handler that must await, so the
    /// body is fully guarded — whatever happens, the window still closes.
    /// </summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // The window stays interactive for up to the CloseAsync timeout, so the close box can be
        // clicked again while the protocol runs. Re-entering the first pass would start a second
        // RequestClose, orphan the first waiter and let Dispose run under the pending await.
        if (_closeInProgress && !_closeConfirmed)
        {
            e.Cancel = true;
            if (!_reentrantCloseLogged)
            {
                _reentrantCloseLogged = true;
                ProbeLog.Write("close", "Close already in progress; ignoring");
            }

            return;
        }

        if (_closeConfirmed || _session is null)
        {
            try
            {
                // Second pass, or nothing was ever created: let the OCX go with the window.
                // The graceful path reaches this branch on its second pass, so unhooking here
                // covers both routes out of the window.
                _shortcuts?.Dispose();
                _session?.Dispose();
                _ax?.Dispose();
            }
            catch (Exception ex)
            {
                // Same rule as below: a failure on the way out must not keep the window open.
                ProbeLog.Write("close", $"Dispose failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            return;
        }

        e.Cancel = true;
        // Set synchronously, before the first await: this is what the guard above tests.
        _closeInProgress = true;
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = false;
        ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, "Closing session…", "");

        try
        {
            // A no-op that says so in the log when nothing is connected.
            await _session.CloseAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            // A failed close must never trap the user in the window.
            ProbeLog.Write("close", $"CloseAsync failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }

        _closeConfirmed = true;
        // BeginInvoke, not a direct Close(): CloseAsync can complete synchronously (nothing
        // connected, or RequestClose answering controlCloseCanProceed), and closing from inside
        // the Closing handler that just set e.Cancel would re-enter it. Let this pass unwind.
        // (discarded: the returned DispatcherOperation is deliberately not awaited)
        _ = Dispatcher.BeginInvoke(() => Close());
    }
}
