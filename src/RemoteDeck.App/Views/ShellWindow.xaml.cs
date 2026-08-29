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
        if (_closeConfirmed || _session is null)
        {
            // Second pass, or nothing was ever created: let the OCX go with the window.
            _session?.Dispose();
            _ax?.Dispose();
            return;
        }

        e.Cancel = true;
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
