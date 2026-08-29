using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Rdp;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Rdp;
using RemoteDeck.Core.Security;
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
    /// <summary>Sentinel first item of the credential combo. <c>Id == 0</c> is never a stored row, so it also
    /// marks "manual entry" after a reload rebuilds the list.</summary>
    private static readonly Credential ManualEntry = new() { Label = "Type credentials manually", UserName = "", SecretBlob = [], Entropy = [] };

    private CredentialRepository? _credentials;
    private ICredentialVault? _vault;
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
        // Filled first, and deliberately before the RDP control is picked: the two early returns
        // below leave a window without a session, and the credential combo must still be usable
        // there. GetService, not GetRequiredService — both are absent when the database failed to
        // open (spec §6.6), which is a degraded mode, not a crash.
        _credentials = App.Current.Services.GetService<CredentialRepository>();
        _vault = App.Current.Services.GetService<ICredentialVault>();
        ReloadCredentials();

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

        // The control is hosted; from here nothing may take the shell down. Creating the session
        // façade casts the OCX to IMsRdpClient10, which an older-than-expected control would
        // refuse, and arming the interceptor calls SetWindowsHookEx, which EDR or a GPO is
        // allowed to deny (spec §7.3 names that an expected outcome). Both are reported in the
        // InfoBar and leave a usable — if reduced — window behind.
        try
        {
            _session = new RdpSessionHost(_ax);
            // BeginInvoke, not Invoke: these are raised from a COM event sink, and a synchronous
            // marshal back to the UI thread would deadlock if the control ever raised off-thread.
            _session.StatusChanged += status => Dispatcher.BeginInvoke(() =>
                ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, status, ""));
            _session.Disconnected += info => Dispatcher.BeginInvoke(() =>
            {
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                // 0/1/2/3 = disconnectReasonNoInfo / LocalNotError / RemoteByUser / ByServer:
                // none of them is an error (spec §6.4). GetErrorDescription() answers "an internal
                // error has occurred" for those codes, so its text is deliberately not shown.
                bool normal = info.Reason is 0 or 1 or 2 or 3;
                var severity = normal
                    ? Wpf.Ui.Controls.InfoBarSeverity.Informational
                    : Wpf.Ui.Controls.InfoBarSeverity.Error;
                ShowStatus(severity, $"Disconnected (reason {info.Reason}, extended {info.ExtendedReason})",
                    normal ? "" : info.Description);
            });
        }
        catch (Exception ex)
        {
            _session = null;
            ProbeLog.Write("startup", $"Session host creation failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "RDP session unavailable",
                $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}. See {ProbeLog.Path}.");
        }

        // Connect needs a session; without one the window stays open for the log path and the
        // control version report, but cannot start anything.
        ConnectButton.IsEnabled = _session is not null;

        // R6 probe: which of the four §7.3 mechanisms actually sees Ctrl+K / Ctrl+Tab while the
        // remote session holds keyboard focus. REMOTEDECK_PROBE_SHORTCUTS switches between them;
        // LowLevelKeyboardHook is the default because it is the only one the lot-0 probe found to
        // intercept anything — the three thread-scoped ones never see the keystrokes.
        // TryParse, not Parse: a typo in the environment variable must not cost the shell.
        var mechanismName = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_SHORTCUTS");
        if (!Enum.TryParse<ShortcutInterceptor.Mechanism>(mechanismName, ignoreCase: true, out var mechanism))
        {
            if (!string.IsNullOrWhiteSpace(mechanismName))
            {
                ProbeLog.Write("startup", $"REMOTEDECK_PROBE_SHORTCUTS=\"{mechanismName}\" is not a known mechanism; falling back to LowLevelKeyboardHook");
            }

            mechanism = ShortcutInterceptor.Mechanism.LowLevelKeyboardHook;
        }

        try
        {
            _shortcuts = new ShortcutInterceptor(mechanism);
            // BeginInvoke for the same reason as the session events: never block the thread that
            // raised the notification, here the message pump itself.
            _shortcuts.Triggered += shortcut => Dispatcher.BeginInvoke(() =>
                ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Success, $"{shortcut} intercepted", $"via {mechanism} — command palette arrives in lot 5"));
        }
        catch (Exception ex)
        {
            // A locked-down machine can refuse the hook. Documented outcome (spec §7.3): carry on
            // without application shortcuts; OnFocusReleased remains the way out of the session.
            _shortcuts = null;
            ProbeLog.Write("startup", $"ShortcutInterceptor({mechanism}) failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "Keyboard shortcuts unavailable",
                $"{ex.Message} Ctrl+Alt+Left / Ctrl+Alt+Right still release the focus. See {ProbeLog.Path}.");
        }

        // R5 probe: one reflection pass over the interop assembly, once per launch, to record
        // whether any member at all could hand us the server certificate.
        RdpSessionHost.LogCertificateSurface();

        if (_session is not null && _shortcuts is not null)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, $"RDP control v{version.Label} ready", "Enter a host and press Connect.");
        }
    }

    /// <summary>Rebuilds the credential combo from the repository, keeping the current selection when
    /// that row still exists. Safe with no database: the sentinel is then the only entry.</summary>
    private void ReloadCredentials()
    {
        var items = new List<Credential> { ManualEntry };
        if (_credentials is not null)
        {
            items.AddRange(_credentials.GetAll());
        }

        var selectedId = (CredentialInput.SelectedItem as Credential)?.Id;
        CredentialInput.ItemsSource = items;
        // GetAll() hands out fresh instances, so the previous selection is matched by Id, never by
        // reference; Id != 0 keeps the sentinel out of that match.
        CredentialInput.SelectedItem = items.FirstOrDefault(c => c.Id == selectedId && c.Id != 0) ?? ManualEntry;
    }

    private void OnCredentialChanged(object sender, SelectionChangedEventArgs e)
    {
        bool manual = ReferenceEquals(CredentialInput.SelectedItem, ManualEntry) || CredentialInput.SelectedItem is null;
        UserInput.IsEnabled = manual;
        DomainInput.IsEnabled = manual;
        PasswordInput.IsEnabled = manual;
        if (!manual && CredentialInput.SelectedItem is Credential c)
        {
            // The boxes become a read-only echo of the stored identity; the secret itself stays
            // sealed and is only ever borrowed at Connect time.
            UserInput.Text = c.UserName;
            DomainInput.Text = c.Domain ?? "";
            PasswordInput.Clear();
        }
    }

    private void OnManageCredentials(object sender, RoutedEventArgs e)
    {
        if (_credentials is null)
        {
            // CredentialsWindow resolves the repository with GetRequiredService: opening it without
            // a database would throw instead of showing anything.
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Database unavailable", "Credentials cannot be managed until the database opens.");
            return;
        }

        new CredentialsWindow { Owner = this }.ShowDialog();
        ReloadCredentials();
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
                if (CredentialInput.SelectedItem is Credential stored && !ReferenceEquals(stored, ManualEntry) && _vault is not null)
                {
                    // Vault path: DPAPI blob -> UTF-8 bytes -> native BSTR lent to the control -> zeroed.
                    // No managed string; the vault owns the lifetime of both buffers.
                    _vault.UseSecret(stored, bstr => _session.PutPassword(bstr));
                    ProbeLog.Write("vault", $"Password supplied from credential '{stored.Label}'");
                }
                else
                {
                    // R1 probe: SecureString -> native BSTR -> vtable put -> zero+free. No managed string.
                    // SecurePassword hands out a fresh copy on every read; dispose it.
                    using var secure = PasswordInput.SecurePassword;
                    nint bstr = Marshal.SecureStringToBSTR(secure);
                    // The finally only frees the BSTR; PasswordInput.Clear() is deliberately left
                    // outside it, so a failing PutPassword keeps what the user typed and lets them retry.
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
