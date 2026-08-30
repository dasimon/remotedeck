using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Controls;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Rdp;
using RemoteDeck.App.Services;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Rdp;
using RemoteDeck.Core.Security;
using RemoteDeck.Core.Settings;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// The application shell: a connection pane on the left, one RDP session area on the right, and a
/// splitter between them whose position — like the window's own geometry — survives a restart.
///
/// The window owns the plumbing the pane deliberately does not: the repositories, the vault, the
/// single <see cref="RdpSessionHost"/>, the editors, the two-step delete and the settings file.
/// <see cref="ConnectionListViewModel"/> only raises intents; everything they cost happens here.
/// </summary>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in
// scope through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button,
// TextBox and friends ambiguous here.
public partial class ShellWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>How long an armed delete stays armed before it disarms itself.</summary>
    private static readonly TimeSpan DeleteConfirmationWindow = TimeSpan.FromSeconds(5);

    /// <summary>Narrowest usable pane; mirrors <c>PaneColumn.MinWidth</c> in the XAML and is what
    /// unfolding restores when the stored width is unusable.</summary>
    private const double MinimumPaneWidth = 220;

    private readonly SettingsStore _settingsStore = new(SettingsStore.DefaultPath());
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _deleteDisarm;

    private ConnectionRepository? _connections;
    private CredentialRepository? _credentials;
    private ICredentialVault? _vault;
    private ConnectionListViewModel? _list;
    private RdpAxHost? _ax;
    private RdpSessionHost? _session;
    private ShortcutInterceptor? _shortcuts;

    /// <summary>The connection behind the session currently open, or <c>null</c> when there is none.</summary>
    private Connection? _current;

    /// <summary>Armed by a first Delete; a second one on the same row within
    /// <see cref="DeleteConfirmationWindow"/> performs the deletion.</summary>
    private Connection? _pendingDelete;

    private bool _paneCollapsed;
    private double _paneWidth;
    private bool _connecting;
    private bool _settingsSaved;
    private bool _closeInProgress;
    private bool _reentrantCloseLogged;
    private bool _closeConfirmed;

    public ShellWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        // Loaded before anything reads the layout: the geometry has to be applied while the window
        // is still unshown, and the pane column width is part of the first measure pass.
        _settings = _settingsStore.Load();
        _paneWidth = _settings.PaneWidth >= MinimumPaneWidth ? _settings.PaneWidth : MinimumPaneWidth;
        RestoreWindowPlacement();
        ApplyPaneState(_settings.PaneCollapsed);

        _deleteDisarm = new DispatcherTimer { Interval = DeleteConfirmationWindow };
        _deleteDisarm.Tick += OnDeleteDisarmTick;

        // Window-level shortcuts. They fire whenever the WPF side owns the keyboard; while the RDP
        // control has focus nothing reaches WPF at all, which is what ShortcutInterceptor is for —
        // and why Ctrl+B is wired in both places. The two never double-fire: the low-level hook
        // swallows the keystroke it handles, so WPF never sees it.
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusSearch), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(NewConnection), Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(TogglePane), Key.B, ModifierKeys.Control));

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Resolved first, and deliberately before the RDP control is picked: the early returns
        // below leave a window without a session, and the pane must still be usable there.
        // GetService, not GetRequiredService — the repositories are absent when the database
        // failed to open (spec §6.6), which is a degraded mode, not a crash.
        _connections = App.Current.Services.GetService<ConnectionRepository>();
        _credentials = App.Current.Services.GetService<CredentialRepository>();
        _vault = App.Current.Services.GetService<ICredentialVault>();
        BuildPane();

        var version = RdpControlCatalog.Select(ClsidRegistry.IsUsable);
        if (version is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "No Remote Desktop control found",
                "None of the known mstscax.dll CLSIDs is registered on this machine.");
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
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, $"RDP control v{version.Label} could not be created",
                $"The CLSID is registered but its class factory refused it (0x{ex.HResult:X8}). See {ProbeLog.Path}.");
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
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, status, ""));
            _session.Disconnected += info => Dispatcher.BeginInvoke(() =>
            {
                _current = null;
                UpdateSessionBar();
                // 0/1/2/3 = disconnectReasonNoInfo / LocalNotError / RemoteByUser / ByServer:
                // none of them is an error (spec §6.4). GetErrorDescription() answers "an internal
                // error has occurred" for those codes, so its text is deliberately not shown.
                bool normal = info.Reason is 0 or 1 or 2 or 3;
                var severity = normal
                    ? Wpf.Ui.Controls.InfoBarSeverity.Informational
                    : Wpf.Ui.Controls.InfoBarSeverity.Error;
                StatusBar.Show(severity, $"Disconnected (reason {info.Reason}, extended {info.ExtendedReason})",
                    normal ? "" : info.Description);
            });
        }
        catch (Exception ex)
        {
            _session = null;
            ProbeLog.Write("startup", $"Session host creation failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "RDP session unavailable",
                $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}. See {ProbeLog.Path}.");
        }

        // R6 probe: which of the four §7.3 mechanisms actually sees the application shortcuts while
        // the remote session holds keyboard focus. REMOTEDECK_PROBE_SHORTCUTS switches between them;
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
            {
                if (shortcut == "Ctrl+B")
                {
                    TogglePane();
                    return;
                }

                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success, $"{shortcut} intercepted",
                    $"via {mechanism} — command palette arrives in lot 5");
            });
        }
        catch (Exception ex)
        {
            // A locked-down machine can refuse the hook. Documented outcome (spec §7.3): carry on
            // without application shortcuts; OnFocusReleased remains the way out of the session.
            _shortcuts = null;
            ProbeLog.Write("startup", $"ShortcutInterceptor({mechanism}) failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "Keyboard shortcuts unavailable",
                $"{ex.Message} Ctrl+Alt+Left / Ctrl+Alt+Right still release the focus. See {ProbeLog.Path}.");
        }

        // R5 probe: one reflection pass over the interop assembly, once per launch, to record
        // whether any member at all could hand us the server certificate.
        RdpSessionHost.LogCertificateSurface();

        if (_session is not null && _shortcuts is not null && _list is not null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, $"RDP control v{version.Label} ready",
                "Pick a connection and press Enter, or press Ctrl+N to create one.");
        }
    }

    // ---------------------------------------------------------------- pane

    /// <summary>
    /// Gives the pane its view-model and subscribes to the three intents it raises. Without a
    /// database there is no repository and therefore no view-model: the pane is replaced by a
    /// message and nothing can be connected, which is the whole of the degraded mode.
    /// </summary>
    private void BuildPane()
    {
        if (_connections is null)
        {
            Pane.Visibility = Visibility.Collapsed;
            PaneUnavailable.Visibility = Visibility.Visible;
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Database unavailable",
                $"Saved connections cannot be read, so nothing can be connected. See {ProbeLog.Path}.");
            return;
        }

        _list = new ConnectionListViewModel(_connections);
        _list.ConnectRequested += OnConnectRequested;
        _list.EditRequested += OnEditRequested;
        _list.DeleteRequested += OnDeleteRequested;
        Pane.ViewModel = _list;

        // Re-select what was selected when the app last closed, when that row still exists.
        if (_settings.LastConnectionId is long lastId
            && _list.Items.FirstOrDefault(i => i.Connection.Id == lastId) is { } previous)
        {
            _list.Selected = previous;
        }
    }

    private void OnPaneToggleClick(object sender, RoutedEventArgs e) => ApplyPaneState(PaneToggle.IsChecked != true);

    private void TogglePane() => ApplyPaneState(!_paneCollapsed);

    /// <summary>
    /// Folds or unfolds the pane. Folding zeroes both the column width and its minimum — the
    /// minimum alone would hold the column open — and hides the splitter with it, so the session
    /// area really does take the whole window.
    /// </summary>
    private void ApplyPaneState(bool collapsed)
    {
        if (collapsed)
        {
            // Remember the width the user had chosen, so unfolding restores it. ActualWidth is 0
            // before the first layout pass (the constructor calls this), hence the guard.
            if (PaneColumn.ActualWidth >= MinimumPaneWidth)
            {
                _paneWidth = PaneColumn.ActualWidth;
            }

            PaneColumn.MinWidth = 0;
            PaneColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            PaneHost.Visibility = Visibility.Collapsed;
            PaneSplitter.Visibility = Visibility.Collapsed;
        }
        else
        {
            PaneHost.Visibility = Visibility.Visible;
            PaneSplitter.Visibility = Visibility.Visible;
            SplitterColumn.Width = new GridLength(4);
            PaneColumn.MinWidth = MinimumPaneWidth;
            PaneColumn.Width = new GridLength(Math.Max(MinimumPaneWidth, _paneWidth));
        }

        _paneCollapsed = collapsed;
        PaneToggle.IsChecked = !collapsed;
    }

    /// <summary>The splitter is the only way the width changes, so it is also where it is persisted.</summary>
    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (PaneColumn.ActualWidth >= MinimumPaneWidth)
        {
            _paneWidth = PaneColumn.ActualWidth;
        }

        SaveSettings();
    }

    private void FocusSearch()
    {
        if (_list is null)
        {
            return;
        }

        // Searching a folded pane would type into nothing; unfold first.
        if (_paneCollapsed)
        {
            ApplyPaneState(false);
        }

        Pane.FocusSearch();
    }

    private void NewConnection() => OnEditRequested(null);

    // ---------------------------------------------------------------- session

    /// <summary>
    /// Opens one connection. Only one session exists at a time, so an already-connected control is
    /// closed gracefully first — <c>async void</c> is the only shape an event handler that awaits
    /// can take, hence the fully guarded body.
    /// </summary>
    private async void OnConnectRequested(Connection connection)
    {
        if (connection is null || _connecting)
        {
            return;
        }

        if (_session is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "RDP session unavailable",
                $"No usable Remote Desktop control was created at startup. See {ProbeLog.Path}.");
            return;
        }

        // Copied out of the field: the vault callback below is a lambda, and capturing the nullable
        // field would carry its nullability past the check above.
        var session = _session;
        _connecting = true;
        try
        {
            if (session.IsConnected)
            {
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, "Closing the current session…",
                    $"'{connection.Name}' replaces it once it is closed.");
                await session.CloseAsync(TimeSpan.FromSeconds(5));
            }

            Credential? credential = null;
            if (connection.CredentialId is long credentialId)
            {
                credential = _credentials?.Get(credentialId);
                if (credential is null)
                {
                    ProbeLog.Write("connections", $"'{connection.Name}' points at credential {credentialId}, which no longer exists; the control will prompt");
                }
            }

            // The connection row carries no identity of its own: the user name and domain come from
            // the credential, and with none attached both stay empty on purpose.
            var settings = new RdpConnectionSettings(
                Host: connection.Host,
                Port: connection.Port,
                UserName: credential?.UserName ?? "",
                Domain: credential?.Domain,
                UseWebAccount: connection.UseWebAccount,
                AdminSession: connection.AdminSession,
                RedirectClipboard: connection.RedirectClipboard,
                RedirectDrives: connection.RedirectDrives,
                RedirectPrinters: connection.RedirectPrinters,
                RedirectAudio: connection.RedirectAudio,
                AuthenticationLevel: connection.AuthenticationLevel,
                DisplayMode: connection.DisplayMode,
                FixedWidth: connection.FixedWidth,
                FixedHeight: connection.FixedHeight);

            var dpi = VisualTreeHelper.GetDpi(this);
            int width = Math.Max(640, (int)(RdpHost.ActualWidth * dpi.DpiScaleX));
            int height = Math.Max(480, (int)(RdpHost.ActualHeight * dpi.DpiScaleY));
            session.Configure(settings, width, height);

            if (!settings.UseWebAccount && credential is not null && _vault is not null)
            {
                // Vault path: DPAPI blob -> UTF-8 bytes -> native BSTR lent to the control -> zeroed.
                // No managed string; the vault owns the lifetime of both buffers.
                _vault.UseSecret(credential, bstr => session.PutPassword(bstr));
                ProbeLog.Write("vault", $"Password supplied from credential '{credential.Label}'");
            }
            else
            {
                // No secret is put at all. EnableCredSspSupport stays true, so the control raises
                // its own credential prompt — RemoteDeck no longer has any manual entry of its own.
                ProbeLog.Write("session", $"'{connection.Name}' has no usable credential; letting the control prompt");
            }

            _current = connection;
            UpdateSessionBar();
            session.Connect();
            _connections?.TouchLastConnected(connection.Id);
            _settings.LastConnectionId = connection.Id;
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, $"Connecting to {connection.Name}",
                $"{connection.Host}:{connection.Port}");
        }
        catch (Exception ex)
        {
            _current = null;
            UpdateSessionBar();
            ProbeLog.Write("session", $"Connect failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "Connect failed",
                $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}");
        }
        finally
        {
            _connecting = false;
        }
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e) => _session?.Disconnect();

    private void UpdateSessionBar()
    {
        SessionLabel.Text = _current is null ? "No session" : $"{_current.Name} — {_current.Host}:{_current.Port}";
        DisconnectButton.IsEnabled = _current is not null;
    }

    // ---------------------------------------------------------------- editor and delete

    /// <summary><c>null</c> means "new connection"; both cases go through the same modal editor.</summary>
    private void OnEditRequested(Connection? existing)
    {
        if (_connections is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Database unavailable",
                "Connections cannot be edited until the database opens.");
            return;
        }

        var editor = new ConnectionEditorWindow(existing) { Owner = this };
        editor.ShowDialog();
        if (editor.Saved)
        {
            _list?.Reload();
        }
    }

    /// <summary>
    /// Two-step delete, in the shell's own InfoBar and never a MessageBox: the first Delete arms
    /// the row, a second one within <see cref="DeleteConfirmationWindow"/> removes it. Arming a
    /// different row replaces the pending one rather than deleting anything.
    /// </summary>
    private void OnDeleteRequested(Connection connection)
    {
        if (connection is null)
        {
            return;
        }

        if (_connections is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Database unavailable",
                "Connections cannot be deleted until the database opens.");
            return;
        }

        if (_pendingDelete is { } armed && armed.Id == connection.Id)
        {
            DisarmDelete();
            try
            {
                _connections.Delete(connection.Id);
                ProbeLog.Write("connections", $"'{connection.Name}' deleted");
                if (_current?.Id == connection.Id)
                {
                    // The row is gone; the session it opened is not, but the bar must stop naming it.
                    _current = null;
                    UpdateSessionBar();
                }

                _list?.Reload();
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success, "Connection deleted", $"'{connection.Name}' is gone.");
            }
            catch (Exception ex)
            {
                ProbeLog.Write("connections", $"Delete failed: {ex.GetType().Name}: {ex.Message}");
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, "Delete failed", ex.Message);
            }

            return;
        }

        _pendingDelete = connection;
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, $"Delete '{connection.Name}'?",
            "Press Delete again to confirm.");
        _deleteDisarm.Stop();
        _deleteDisarm.Start();
    }

    private void OnDeleteDisarmTick(object? sender, EventArgs e)
    {
        DisarmDelete();
        StatusBar.Hide();
    }

    private void DisarmDelete()
    {
        _deleteDisarm.Stop();
        _pendingDelete = null;
    }

    private void OnManageCredentials(object sender, RoutedEventArgs e)
    {
        if (_credentials is null)
        {
            // CredentialsWindow resolves the repository with GetRequiredService: opening it without
            // a database would throw instead of showing anything.
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, "Database unavailable", "Credentials cannot be managed until the database opens.");
            return;
        }

        new CredentialsWindow { Owner = this }.ShowDialog();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>
    /// Re-applies the saved geometry, but only when the whole rectangle still fits on the current
    /// desktop: a position saved on a monitor that has since been unplugged would open the window
    /// where nobody can reach it. Anything rejected falls back to the XAML's centred default.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        if (_settings.WindowLeft is double left && _settings.WindowTop is double top
            && _settings.WindowWidth is double width && _settings.WindowHeight is double height
            && width >= MinWidth && height >= MinHeight)
        {
            // The virtual screen, not the primary one: a saved position on a second monitor is legitimate.
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (desktop.Contains(new Rect(left, top, width, height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }
            else
            {
                ProbeLog.Write("settings", $"Saved window bounds {left:F0},{top:F0} {width:F0}x{height:F0} fall outside the current desktop; centring instead");
            }
        }

        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Writes the layout to <c>%APPDATA%\RemoteDeck\settings.json</c>. Losing it only costs
    /// geometry, so a failure is logged and swallowed — never surfaced on the way out of the app.</summary>
    private void SaveSettings()
    {
        // RestoreBounds, not Left/Top/Width/Height: those describe the maximized frame, and
        // restoring them would make an un-maximized window fill the screen with no way back.
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds is { Width: > 0, Height: > 0 })
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.PaneCollapsed = _paneCollapsed;
        _settings.PaneWidth = !_paneCollapsed && PaneColumn.ActualWidth >= MinimumPaneWidth
            ? PaneColumn.ActualWidth
            : _paneWidth;
        _settings.LastConnectionId = _list?.SelectedConnection?.Id ?? _current?.Id ?? _settings.LastConnectionId;

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            ProbeLog.Write("settings", $"Save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- shutdown

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

        // Once, on whichever pass gets here first, and while the geometry still describes a window
        // that is on screen. Both routes out (graceful and direct) go through this line.
        if (!_settingsSaved)
        {
            _settingsSaved = true;
            SaveSettings();
        }

        if (_closeConfirmed || _session is null)
        {
            try
            {
                // Second pass, or nothing was ever created: let the OCX go with the window.
                // The graceful path reaches this branch on its second pass, so unhooking here
                // covers both routes out of the window.
                _deleteDisarm.Stop();
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
        DisconnectButton.IsEnabled = false;
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, "Closing session…", "");

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
