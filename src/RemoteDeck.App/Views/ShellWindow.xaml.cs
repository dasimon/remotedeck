using System.Globalization;
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
using RemoteDeck.App.Resources;
using RemoteDeck.App.Services;
using RemoteDeck.App.ViewModels;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Diagnostics;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Rdp;
using RemoteDeck.Core.Search;
using RemoteDeck.Core.Security;
using RemoteDeck.Core.Sessions;
using RemoteDeck.Core.Settings;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// The application shell: a connection pane on the left, the session tabs on the right, and a
/// splitter between them whose position — like the window's own geometry — survives a restart.
///
/// The window owns the plumbing the pane and the tabs deliberately do not: the repositories, the
/// vault, the RDP control version, the editors, the two-step delete and the settings file.
/// <see cref="ConnectionListViewModel"/> only raises intents and
/// <see cref="SessionsViewModel"/> only orchestrates sessions; everything they cost happens here.
/// </summary>
/// <remarks>
/// One session per tab (lot 4). The shell no longer owns a control: each
/// <see cref="RdpSession"/> creates its own, and the only thing this window keeps from startup is
/// the catalog's verdict on which version to use. What the shell still owns is the visual tree —
/// <see cref="RdpSession.Dispose"/> explicitly does not remove its host from
/// <c>SessionsArea</c>, so <see cref="DetachSession"/> is what closes that loop.
/// </remarks>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in
// scope through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button,
// TextBox and friends ambiguous here.
public partial class ShellWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>How long an armed delete stays armed before it disarms itself.</summary>
    private static readonly TimeSpan DeleteConfirmationWindow = TimeSpan.FromSeconds(5);

    /// <summary>§6.5 budget for one tab while the window is closing.</summary>
    private static readonly TimeSpan PerTabCloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling on the whole close-all pass, whatever the tabs do.</summary>
    private static readonly TimeSpan OverallCloseTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Narrowest usable pane; mirrors <c>PaneColumn.MinWidth</c> in the XAML and is what
    /// unfolding restores when the stored width is unusable.</summary>
    private const double MinimumPaneWidth = 220;

    /// <summary>Smallest remote desktop a session is ever asked for; mirrors
    /// <c>RdpSession</c>'s own floor, so a pane dragged almost shut cannot produce a 120×40 desktop.</summary>
    private const int MinimumRemoteWidth = 640;
    private const int MinimumRemoteHeight = 480;

    /// <summary>Slack around the tab strip inside which the corner of a dragged detached window
    /// counts as being over it. The strip is 34 px tall, so 24 px on each side makes a band the user
    /// can aim at without having to hit the tabs themselves.</summary>
    private const double ReattachMargin = 24;

    /// <summary>Shortest drop zone the strip is ever given, whatever it currently measures: with
    /// every session detached the strip has no visible tab left and would otherwise shrink to a
    /// line nobody could aim at. Mirrors the 34 px tab height.</summary>
    private const double MinimumDropZoneHeight = 34;

    /// <summary>How far below the pointer a torn-off window's top edge starts, so the caption strip
    /// the user was dragging ends up under the cursor rather than above it. Half the window's 32 px
    /// caption.</summary>
    private const double CaptionGrabOffset = 16;

    /// <summary>Palette id prefixes. The palette carries strings, not objects, so the shell can act
    /// on a choice after the window that produced it is gone.</summary>
    private const string ConnectionIdPrefix = "conn:";
    private const string TabIdPrefix = "tab:";

    /// <summary>Palette sort bonuses: commands first, then the open tabs, then every connection.
    /// They only decide an unfiltered list and break ties in a filtered one.</summary>
    private const int ConnectionPriority = 0;
    private const int TabPriority = 5;
    private const int CommandPriority = 10;

    private readonly SettingsStore _settingsStore = new(SettingsStore.DefaultPath());
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _deleteDisarm;
    private readonly SessionsViewModel _sessions;

    private ConnectionRepository? _connections;
    private CredentialRepository? _credentials;
    private ICredentialVault? _vault;
    private ConnectionListViewModel? _list;
    private ShortcutInterceptor? _shortcuts;

    /// <summary>Control version chosen from the catalog at startup, or <c>null</c> when none is
    /// usable. Every session is created against it.</summary>
    private RdpControlVersion? _version;

    /// <summary>Armed by a first Delete; a second one on the same row within
    /// <see cref="DeleteConfirmationWindow"/> performs the deletion.</summary>
    private Connection? _pendingDelete;

    /// <summary>The detached window whose caption drag is currently over the tab strip, i.e. the one
    /// that would be taken back if the user let go now. Null the rest of the time.</summary>
    private SessionWindow? _reattachCandidate;

    private bool _paletteOpen;
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

        // The tabs are usable before OnLoaded runs: they hold no repository and no control, only
        // the two callbacks that put a session's host in — and take it out of — SessionsArea.
        _sessions = new SessionsViewModel(AttachSession, DetachSession);
        _sessions.ActiveChanged += OnSessionsChanged;
        _sessions.TabChanged += OnTabChanged;
        TabStrip.ViewModel = _sessions;
        TabStrip.DetachRequested += OnDetachRequested;

        // Window-level shortcuts. They fire whenever the WPF side owns the keyboard; while the RDP
        // control has focus nothing reaches WPF at all, which is what ShortcutInterceptor is for —
        // and why Ctrl+B and Ctrl+W are wired in both places. The two never double-fire: the
        // low-level hook swallows the keystroke it handles, so WPF never sees it.
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusSearch), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(NewConnection), Key.N, ModifierKeys.Control));
        // Ctrl+B and Ctrl+W carry a canExecute, Ctrl+F, Ctrl+N and Ctrl+K do not: the first two are
        // the ones a text field needs back (Ctrl+W deletes a word, Ctrl+B moves by one), and the hook
        // already declines them there. Without the same guard on this path the pane would still fold
        // and the tab would still close, so both paths ask the one helper. CommandManager re-asks
        // CanExecute on every matching key press (TranslateInput calls command.CanExecute inline;
        // there is no cached verdict), so nothing needs invalidating when the focus moves.
        InputBindings.Add(new KeyBinding(new RelayCommand(TogglePane, () => ShouldInterceptShortcut("Ctrl+B")), Key.B, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(CloseActiveTab, () => ShouldInterceptShortcut("Ctrl+W")), Key.W, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenCommandPalette), Key.K, ModifierKeys.Control));

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Resolved first, and deliberately before the RDP control version is picked: the early
        // return below leaves a window that can open no session, and the pane must still be usable
        // there. GetService, not GetRequiredService — the repositories are absent when the database
        // failed to open (spec §6.6), which is a degraded mode, not a crash.
        _connections = App.Current.Services.GetService<ConnectionRepository>();
        _credentials = App.Current.Services.GetService<CredentialRepository>();
        _vault = App.Current.Services.GetService<ICredentialVault>();
        BuildPane();
        UpdateSessionsArea();

        // All that remains of the old single-control startup: which version the sessions will use.
        // Creating a control is now each RdpSession's own business, so a class factory that refuses
        // one costs that tab and nothing else.
        _version = RdpControlCatalog.Select(ClsidRegistry.IsUsable);
        if (_version is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Shell_NoControlTitle,
                Strings.Shell_NoControlMessage);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        ProbeLog.Write("R4", $"FluentWindow ready; control version {_version.Label} ({_version.Clsid:D})");
        ProbeLog.Write("R3", $"Window DPI scale X={dpi.DpiScaleX:F2} Y={dpi.DpiScaleY:F2}");

        // Arming the interceptor calls SetWindowsHookEx, which EDR or a GPO is allowed to deny
        // (spec §7.3 names that an expected outcome). The failure is reported in the InfoBar and
        // leaves a usable — if reduced — window behind.
        //
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
            // Asked inside the hook callback, before the keystroke is swallowed: it is the only
            // point where the key can still be handed back to whatever has the focus.
            _shortcuts.ShouldIntercept = ShouldInterceptShortcut;
            // BeginInvoke, not Invoke: the notification comes off the message pump itself, and a
            // synchronous hop back would block the thread that raised it.
            _shortcuts.Triggered += shortcut => Dispatcher.BeginInvoke(() => OnShortcut(shortcut, mechanism));
        }
        catch (Exception ex)
        {
            // A locked-down machine can refuse the hook. Documented outcome (spec §7.3): carry on
            // without application shortcuts; Ctrl+Alt+Left / Ctrl+Alt+Right remain the way out.
            _shortcuts = null;
            ProbeLog.Write("startup", $"ShortcutInterceptor({mechanism}) failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Shell_ShortcutsUnavailableTitle,
                Text.Of(Strings.Shell_ShortcutsUnavailableMessage, ex.Message, ProbeLog.Path));
        }

        // R5 probe: one reflection pass over the interop assembly, once per launch, to record
        // whether any member at all could hand us the server certificate.
        RdpSessionHost.LogCertificateSurface();

        if (_shortcuts is not null && _list is not null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                Text.Of(Strings.Shell_ReadyTitle, _version.Label), Strings.Shell_ReadyMessage);
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
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DatabaseUnavailableTitle,
                Text.Of(Strings.Shell_DatabaseUnreadableMessage, ProbeLog.Path));
            return;
        }

        _list = new ConnectionListViewModel(_connections);
        _list.ConnectRequested += OnConnectRequested;
        _list.EditRequested += OnEditRequested;
        _list.DeleteRequested += OnDeleteRequested;
        _list.ImportRequested += ImportConnections;
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

    // ---------------------------------------------------------------- shortcuts

    /// <summary>
    /// Whether a shortcut is the application's to take, or the focused input's. The single
    /// definition of that rule: the low-level hook asks it before swallowing a keystroke, and the
    /// window's Ctrl+B / Ctrl+W key bindings ask it as their <c>canExecute</c> — a shortcut reaching
    /// WPF through the message pump never went past the hook, so one path alone would not do.
    /// Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+W and Ctrl+B all mean something inside a text field — move
    /// between fields, delete the word to the left, jump back a word — and a system-wide hook that
    /// swallows them makes typing in the shell feel broken. Ctrl+K is never filtered: it is the
    /// only way into the command palette and has no meaning in a WPF input.
    /// </summary>
    /// <remarks>
    /// Runs inside the hook callback (see <see cref="ShortcutInterceptor.ShouldIntercept"/>), so it
    /// reads WPF state and nothing else — no I/O, no synchronous dispatcher hop. Off the UI thread
    /// there is no safe way to read the focus at all, so the shortcut is taken, as before.
    /// <para>
    /// On the key-binding path a <c>false</c> verdict stops the command but still marks the key
    /// handled (<c>CommandManager.TranslateInput</c> sets <c>Handled</c> whenever a binding matched,
    /// executed or not). That costs nothing here: no WPF text control does anything with Ctrl+W or
    /// Ctrl+B, so the keystroke had nowhere else to go.
    /// </para>
    /// </remarks>
    private static bool ShouldInterceptShortcut(string shortcut)
    {
        if (shortcut is not ("Ctrl+Tab" or "Ctrl+Shift+Tab" or "Ctrl+W" or "Ctrl+B"))
        {
            return true;
        }

        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
        {
            return true;
        }

        // Qualified: UseWindowsForms puts its own Application, TextBoxBase and ComboBox in scope
        // through implicit usings. A read-only ComboBox has no caret, so Ctrl+W there is ours.
        return Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.ComboBox { IsEditable: true });
    }

    /// <summary>
    /// What an intercepted shortcut does. The <c>default</c> branch is the lot-0 probe message: it
    /// now only ever fires for a shortcut the interceptor learns to recognise before this switch
    /// learns to act on it.
    /// </summary>
    private void OnShortcut(string shortcut, ShortcutInterceptor.Mechanism mechanism)
    {
        // The hook is system-wide and fires whenever *any* window of this process is foreground —
        // the connection editor and the credentials window included. Acting there would toggle the
        // pane behind a dialog and, worse, let Ctrl+W close a session the user cannot even see.
        // Window.IsActive is false for the shell exactly while one of its owned windows is up.
        if (!IsActive)
        {
            ProbeLog.Write("shortcuts", $"{shortcut} ignored: the shell window is not active");
            return;
        }

        switch (shortcut)
        {
            case "Ctrl+B":
                TogglePane();
                break;
            case "Ctrl+Tab":
                _sessions.Next();
                break;
            case "Ctrl+Shift+Tab":
                _sessions.Previous();
                break;
            case "Ctrl+W":
                CloseActiveTab();
                break;
            case "Ctrl+K":
                OpenCommandPalette();
                break;
            default:
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
                    Text.Of(Strings.Shell_ShortcutInterceptedTitle, shortcut),
                    Text.Of(Strings.Shell_ShortcutInterceptedMessage, mechanism));
                break;
        }
    }

    // ---------------------------------------------------------------- command palette

    /// <summary>
    /// Opens the Ctrl+K palette and runs what comes back. Modal on purpose: the palette acts on the
    /// shell's own state (the tab list, the pane, the active session), and letting the shell change
    /// underneath it would make <c>tab:&lt;index&gt;</c> point at a different tab than the one shown.
    /// </summary>
    private void OpenCommandPalette()
    {
        // _paletteOpen is not redundant with the modality: the low-level hook fires on Ctrl+K even
        // while the palette itself holds the focus. OnShortcut already refuses that case (the shell
        // is not IsActive), but the WPF KeyBinding on this window would still stack a second palette
        // when the first one is dismissed and the keystroke arrives twice.
        if (_paletteOpen || _closeInProgress)
        {
            return;
        }

        string? chosen;
        _paletteOpen = true;
        try
        {
            var palette = new CommandPaletteWindow(BuildPaletteItems()) { Owner = this };
            palette.ShowDialog();
            chosen = palette.ChosenId;
        }
        finally
        {
            _paletteOpen = false;
        }

        // Outside the try: the chosen action often opens another window or writes to the InfoBar,
        // and both belong to the shell once the palette is gone.
        if (chosen is not null)
        {
            RunPaletteChoice(chosen);
        }
    }

    /// <summary>
    /// Everything the palette offers, in one flat list: every saved connection, every open tab, and
    /// the shell's commands. Ordering is <see cref="PaletteFilter"/>'s business — the priorities
    /// below are the only ranking expressed here.
    /// </summary>
    /// <remarks>
    /// Read straight from the repository rather than from the pane's rows: those are filtered by
    /// whatever is typed in the search box, and the palette must see every connection. Without a
    /// database there simply are none, and the commands stand alone.
    /// </remarks>
    private IReadOnlyList<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>();

        foreach (var connection in _connections?.GetAll() ?? [])
        {
            var group = string.IsNullOrWhiteSpace(connection.GroupName)
                ? ConnectionListViewModel.UngroupedGroup
                : connection.GroupName;
            items.Add(new PaletteItem(PaletteItemKind.Connection, $"{ConnectionIdPrefix}{connection.Id}",
                connection.Name, Text.Of(Strings.Palette_ConnectionSubtitle, group, connection.Host),
                ConnectionPriority));
        }

        // By index, not by connection id: an index is what Activate needs, and the strip cannot be
        // reordered while the palette is modal.
        for (int i = 0; i < _sessions.Tabs.Count; i++)
        {
            var tab = _sessions.Tabs[i];
            items.Add(new PaletteItem(PaletteItemKind.Command, $"{TabIdPrefix}{i}",
                Text.Of(Strings.Palette_SwitchToTab, tab.Title), tab.Subtitle, TabPriority));
        }

        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:new",
            Strings.Palette_NewConnection, "Ctrl+N", CommandPriority));
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:import",
            Strings.Palette_ImportConnections, Strings.Palette_ImportSubtitle, CommandPriority));
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:credentials",
            Strings.Palette_ManageCredentials, Strings.Palette_ManageCredentialsSubtitle, CommandPriority));
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:pane",
            Strings.Palette_TogglePane, "Ctrl+B", CommandPriority));
        // One entry, not two: RemoteDeck has no disconnect that keeps the tab behind — the toolbar's
        // own Disconnect button is CloseActiveTab as well — so a second "Disconnect" row would name
        // the same action twice. The subtitle carries the other half of the vocabulary instead, and
        // PaletteFilter searches it, so typing "disconnect" still finds this row.
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:close",
            Strings.Palette_CloseSession, Strings.Palette_CloseSessionSubtitle, CommandPriority));
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:reconnect",
            Strings.Palette_ReconnectTab, Strings.Palette_ReconnectTabSubtitle, CommandPriority));

        return items;
    }

    /// <summary>
    /// Runs one palette entry. Unknown, malformed and stale ids are ignored rather than reported:
    /// they can only come from the list this window built moments earlier, and a row whose
    /// connection was deleted meanwhile is a race, not a mistake the user made.
    /// </summary>
    private void RunPaletteChoice(string id)
    {
        if (id.StartsWith(ConnectionIdPrefix, StringComparison.Ordinal))
        {
            if (long.TryParse(id.AsSpan(ConnectionIdPrefix.Length), CultureInfo.InvariantCulture, out long connectionId)
                && _connections?.Get(connectionId) is { } connection)
            {
                // The same path Enter takes in the pane: one tab per connection, existing tab reused.
                OnConnectRequested(connection);
            }

            return;
        }

        if (id.StartsWith(TabIdPrefix, StringComparison.Ordinal))
        {
            if (int.TryParse(id.AsSpan(TabIdPrefix.Length), CultureInfo.InvariantCulture, out int index)
                && index >= 0 && index < _sessions.Tabs.Count)
            {
                _sessions.Activate(_sessions.Tabs[index]);
            }

            return;
        }

        switch (id)
        {
            case "cmd:new":
                NewConnection();
                break;

            case "cmd:import":
                ImportConnections();
                break;

            case "cmd:credentials":
                ManageCredentials();
                break;

            case "cmd:pane":
                TogglePane();
                break;

            // BuildPaletteItems no longer produces "cmd:disconnect": RemoteDeck has no disconnect
            // that keeps the tab behind, so "Close current session" is the one entry covering both
            // words. The old id is still accepted here — it costs a line and can mean nothing else.
            case "cmd:close":
            case "cmd:disconnect":
                if (_sessions.Active is null)
                {
                    StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, Strings.Shell_NoSession,
                        Strings.Shell_NoTabToCloseMessage);
                    break;
                }

                CloseActiveTab();
                break;

            case "cmd:reconnect":
                if (_sessions.Active is null)
                {
                    StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, Strings.Shell_NoSession,
                        Strings.Shell_NoTabToReconnectMessage);
                    break;
                }

                ReconnectActiveTab();
                break;
        }
    }

    // ---------------------------------------------------------------- sessions

    /// <summary>Puts a session's host in the sessions area. Called by
    /// <see cref="SessionsViewModel.Open"/>, before the session is started.</summary>
    private void AttachSession(RdpSession session) => SessionsArea.Children.Add(session.Host);

    /// <summary>Takes it out again. <see cref="RdpSession.Dispose"/> deliberately leaves the host
    /// in the tree, so this is the only thing that removes it.</summary>
    private void DetachSession(RdpSession session) => SessionsArea.Children.Remove(session.Host);

    // ---------------------------------------------------------------- detach / reattach

    /// <summary>
    /// A tab was dragged out of the strip. The shell builds the window, places it under the pointer
    /// and shows it, and only then asks <see cref="SessionsViewModel.Detach"/> to move the session's
    /// host into it — a <c>WindowsFormsHost</c> can only be given to a window that already has a
    /// handle of its own.
    /// </summary>
    /// <remarks>
    /// No <c>Owner</c>: an owned window is always painted above its owner and minimises with it,
    /// which is the opposite of what a session torn onto a second monitor is for.
    /// </remarks>
    private void OnDetachRequested(SessionTabViewModel tab, System.Windows.Point screenPoint)
    {
        if (_closeInProgress || tab.IsDetached)
        {
            return;
        }

        var window = new SessionWindow(tab);
        if (PlaceUnder(screenPoint, window) is { } placement)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = placement.Left;
            window.Top = placement.Top;
            window.Width = placement.Width;
            window.Height = placement.Height;
        }
        else
        {
            // The pointer belongs to no screen ScreenFit knows about — it should not happen, since
            // the user is pointing at one. Centring is the reachable answer either way.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ReattachRequested += OnSessionWindowReattachRequested;
        window.CloseRequested += OnSessionWindowCloseRequested;
        window.CaptionDragMoved += OnSessionWindowCaptionDragMoved;
        window.CaptionDragEnded += OnSessionWindowCaptionDragEnded;
        window.Show();

        if (_sessions.Detach(tab, window))
        {
            return;
        }

        // Refused: the session never left the docked container, so this window has nothing to show.
        // AllowClose first — the window cancels every close until the shell has run the protocol,
        // and there is no protocol to run over a session that never moved.
        window.AllowClose();
        window.Close();
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DetachRefusedTitle,
            Text.Of(Strings.Shell_DetachRefusedMessage, tab.Title));
    }

    /// <summary>
    /// Where a window torn off at <paramref name="screenPoint"/> opens: the size of the docked
    /// session area, centred horizontally under the pointer with its caption strip under it, then
    /// clamped by <see cref="ScreenFit"/> onto a monitor that is really there. Null when the point
    /// belongs to no screen at all.
    /// </summary>
    /// <remarks>
    /// Nothing here reads or writes <c>settings.json</c>: remembering where a session was last
    /// detached is a separate concern, and this method is the fallback it will fall back to.
    /// </remarks>
    private DetachedWindowPlacement? PlaceUnder(System.Windows.Point screenPoint, SessionWindow window)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double width = Math.Max(window.MinWidth, SessionsArea.ActualWidth);
        double height = Math.Max(window.MinHeight, SessionsArea.ActualHeight);
        var pointer = ToDeviceIndependent(screenPoint, dpi);

        var proposed = new DetachedWindowPlacement(
            pointer.X - width / 2, pointer.Y - CaptionGrabOffset, width, height, FullScreen: false);
        return ScreenFit.Choose(proposed, Screens(dpi), window.MinWidth, window.MinHeight);
    }

    /// <summary>
    /// The working areas of the monitors present right now, in the device-independent units
    /// <see cref="Window.Left"/> and <see cref="Window.Top"/> are expressed in.
    /// </summary>
    /// <remarks>
    /// <c>Screen.AllScreens</c> reports physical pixels, hence the division by the shell's own DPI
    /// scale: exact on a desktop where every monitor runs at the same scale, and approximate on a
    /// mixed one — where Windows re-scales the window onto whichever monitor it lands on anyway.
    /// The whole point of going through <see cref="ScreenFit"/> is that the result is a rectangle
    /// the user can reach, not one that is right to the pixel.
    /// </remarks>
    private static IReadOnlyList<ScreenBounds> Screens(DpiScale dpi)
    {
        var screens = new List<ScreenBounds>();
        // Qualified: UseWindowsForms puts System.Windows.Forms.Screen in scope through the implicit
        // usings, and RemoteDeck never imports that namespace into a WPF file.
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            screens.Add(new ScreenBounds(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY,
                area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY));
        }

        return screens;
    }

    /// <summary>A point in screen device pixels, in device-independent units.</summary>
    private static System.Windows.Point ToDeviceIndependent(System.Windows.Point screenPoint, DpiScale dpi) =>
        new(screenPoint.X / dpi.DpiScaleX, screenPoint.Y / dpi.DpiScaleY);

    /// <summary>The <em>Reattach</em> button of a detached window.</summary>
    private void OnSessionWindowReattachRequested(SessionWindow window) => Reattach(window);

    /// <summary>
    /// Takes a session back into the docked area, from the button or from a drag onto the strip.
    /// <see cref="SessionsViewModel.Reattach"/> closes the emptied window itself, so nothing here
    /// touches it afterwards; a refusal leaves the session where it is, still visible and still
    /// usable, and only costs a line in the probe log.
    /// </summary>
    private void Reattach(SessionWindow window)
    {
        ClearReattachHint();
        if (!_sessions.Reattach(window.Tab))
        {
            ProbeLog.Write("session", $"'{window.Tab.Title}': reattach refused; the session stays in its window");
            return;
        }

        // The window that had the focus has just gone; without this the shell would sit behind
        // whatever was under it, showing the session the user just dropped on it.
        Activate();
    }

    /// <summary>The cross of a detached window. The §6.5 protocol is the same one Ctrl+W and the
    /// tab's own cross run, and it is what closes that window when it is done.</summary>
    private void OnSessionWindowCloseRequested(SessionWindow window) =>
        _ = _sessions.CloseAsync(window.Tab);

    /// <summary>A detached window moved under the user's hand: light the strip's drop band exactly
    /// while letting go would take the session back.</summary>
    private void OnSessionWindowCaptionDragMoved(SessionWindow window)
    {
        var candidate = IsOverTabStrip(window) ? window : null;
        if (ReferenceEquals(candidate, _reattachCandidate))
        {
            return;
        }

        _reattachCandidate = candidate;
        TabStrip.ShowDropHint(candidate is not null);
    }

    /// <summary>The drag ended. Reattaching is deferred to the next dispatcher pass: the drag ended
    /// inside the window's own mouse handler, and reattaching moves the session's host out of that
    /// window and closes it.</summary>
    private void OnSessionWindowCaptionDragEnded(SessionWindow window)
    {
        bool dropped = ReferenceEquals(_reattachCandidate, window);
        ClearReattachHint();
        if (dropped)
        {
            // (discarded: the returned DispatcherOperation is deliberately not awaited)
            _ = Dispatcher.BeginInvoke(() => Reattach(window));
        }
    }

    /// <summary>
    /// Whether the top-left corner of a dragged detached window has reached the tab strip. The drop
    /// zone is the strip's own rectangle grown by <see cref="ReattachMargin"/> and never shorter
    /// than one tab, so a strip whose every session is detached — and which therefore measures to
    /// nothing — is still something the user can aim at. A shell that is not on screen is no target
    /// at all.
    /// </summary>
    private bool IsOverTabStrip(SessionWindow window)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !TabStrip.IsVisible)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = ToDeviceIndependent(TabStrip.PointToScreen(new System.Windows.Point(0, 0)), dpi);
        var zone = new Rect(origin.X, origin.Y,
            TabStrip.ActualWidth, Math.Max(TabStrip.ActualHeight, MinimumDropZoneHeight));
        zone.Inflate(ReattachMargin, ReattachMargin);

        return zone.Contains(new System.Windows.Point(window.Left, window.Top));
    }

    private void ClearReattachHint()
    {
        _reattachCandidate = null;
        TabStrip.ShowDropHint(false);
    }

    /// <summary>
    /// Opens one connection, or brings its tab forward when it already has one — a connection has
    /// at most one session. <c>async void</c> is the only shape an event handler that awaits can
    /// take, hence the fully guarded body.
    /// </summary>
    private async void OnConnectRequested(Connection connection)
    {
        if (connection is null || _connecting || _closeInProgress)
        {
            return;
        }

        if (_sessions.Find(connection.Id) is { } existing)
        {
            _sessions.Activate(existing);
            return;
        }

        if (_version is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Shell_SessionUnavailableTitle,
                Text.Of(Strings.Shell_SessionUnavailableMessage, ProbeLog.Path));
            return;
        }

        _connecting = true;
        try
        {
            var session = new RdpSession(connection, _version, host => SupplyAndConnectAsync(connection, host));
            _sessions.Open(session);
            UpdateSessionsArea();

            // StartAsync creates the OCX, and an AxHost only produces its COM object once it owns a
            // window handle — which WindowsFormsHost gives it during a layout pass in a *visible*
            // container. Forcing that pass here is what makes the very first tab connectable.
            SessionsArea.UpdateLayout();

            _settings.LastConnectionId = connection.Id;
            await session.StartAsync();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"Opening '{connection.Name}' failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Shell_ConnectFailedTitle,
                Text.Of(Strings.Shell_ConnectFailedMessage, ex.GetType().Name,
                    ex.HResult.ToString("X8", CultureInfo.InvariantCulture), ex.Message));
        }
        finally
        {
            _connecting = false;
        }
    }

    /// <summary>
    /// Configures one control, lends it the secret and connects. Handed to every
    /// <see cref="RdpSession"/> as its <c>supplyAndConnect</c> delegate, so it runs again — from
    /// scratch — for each automatic retry and each <em>Reconnect</em>. Nothing derived from the
    /// credential is cached between calls: the vault re-lends the secret every time.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>async</c>: every step is synchronous. It returns a <see cref="Task"/>
    /// only because that is the shape <see cref="RdpSession"/> asks for, and anything thrown here
    /// is caught there and turned into a failed attempt.
    /// </remarks>
    private Task SupplyAndConnectAsync(Connection connection, RdpSessionHost host)
    {
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

        // Every tab shares one container, so the area's size is every session's size.
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(MinimumRemoteWidth, (int)(SessionsArea.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(MinimumRemoteHeight, (int)(SessionsArea.ActualHeight * dpi.DpiScaleY));
        host.Configure(settings, width, height);

        if (!settings.UseWebAccount && credential is not null && _vault is not null)
        {
            // Vault path: DPAPI blob -> UTF-8 bytes -> native BSTR lent to the control -> zeroed.
            // No managed string; the vault owns the lifetime of both buffers.
            _vault.UseSecret(credential, bstr => host.PutPassword(bstr));
            ProbeLog.Write("vault", $"Password supplied from credential '{credential.Label}'");
        }
        else
        {
            // No secret is put at all. EnableCredSspSupport stays true, so the control raises
            // its own credential prompt — RemoteDeck no longer has any manual entry of its own.
            ProbeLog.Write("session", $"'{connection.Name}' has no usable credential; letting the control prompt");
        }

        host.Connect();
        _connections?.TouchLastConnected(connection.Id);
        return Task.CompletedTask;
    }

    /// <summary>Closes the active tab (Ctrl+W, the cross, <em>Disconnect</em>). Fire and forget:
    /// <see cref="SessionsViewModel.CloseAsync(SessionTabViewModel?)"/> never throws.</summary>
    private void CloseActiveTab()
    {
        if (_closeInProgress)
        {
            return;
        }

        _ = _sessions.CloseAsync(_sessions.Active);
    }

    /// <summary><em>Disconnect</em> is the graceful end of a session, which is exactly the §6.5
    /// close protocol — the same thing the tab's cross does.</summary>
    private void OnDisconnectClick(object sender, RoutedEventArgs e) => CloseActiveTab();

    private void OnReconnectClick(object sender, RoutedEventArgs e) => ReconnectActiveTab();

    /// <summary><em>Reconnect</em>, from the button or from the palette. <c>async void</c> is the
    /// only shape an awaiting handler can take, hence the fully guarded body.</summary>
    private async void ReconnectActiveTab()
    {
        if (_sessions.Active is not { } tab)
        {
            return;
        }

        try
        {
            await tab.Session.ReconnectNowAsync();
        }
        catch (Exception ex)
        {
            // RdpSession swallows attempt failures itself; this only covers the unexpected.
            ProbeLog.Write("session", $"Reconnect failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    private void OnCancelRetryClick(object sender, RoutedEventArgs e) => _sessions.Active?.Session.CancelReconnect();

    /// <summary>Copies the active session's diagnostics. The clipboard is owned by whatever
    /// currently holds it, so <c>SetText</c> is allowed to fail — and must not cost the window.</summary>
    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (_sessions.Active is not { } tab)
        {
            return;
        }

        try
        {
            // Qualified: UseWindowsForms puts System.Windows.Forms.Clipboard in scope too.
            System.Windows.Clipboard.SetText(tab.Session.BuildDiagnostics());
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success, Strings.Shell_DiagnosticsCopiedTitle,
                Text.Of(Strings.Shell_DiagnosticsCopiedMessage, tab.Title));
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"Clipboard.SetText failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DiagnosticsNotCopiedTitle,
                Text.Of(Strings.Shell_DiagnosticsNotCopiedMessage, ex.GetType().Name));
        }
    }

    /// <summary>A tab was opened, closed or activated.</summary>
    private void OnSessionsChanged()
    {
        UpdateSessionsArea();
        UpdateSessionBar();
        if (_sessions.Active is { } tab)
        {
            UpdateSessionInfoBar(tab);
            return;
        }

        // No tab left: the bar would otherwise keep saying "Connected to X" over the empty-area
        // message. Hide() keeps the text in place, so nothing flashes on the next open.
        StatusBar.Hide();
    }

    /// <summary>A session changed state or ticked its countdown. Only the visible one is reported;
    /// a background tab says everything it has to say through its status dot.</summary>
    private void OnTabChanged(SessionTabViewModel tab)
    {
        if (!ReferenceEquals(tab, _sessions.Active))
        {
            return;
        }

        UpdateSessionBar();
        UpdateSessionInfoBar(tab);
    }

    /// <summary>Shows the black session backdrop only while there is something to put in it, so the
    /// empty-state message is readable in both themes. Detached tabs do not count: their session is
    /// on screen in a window of its own, and the docked area behind them is empty.</summary>
    private void UpdateSessionsArea()
    {
        bool any = _sessions.Tabs.Any(t => !t.IsDetached);
        SessionsBorder.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptySessions.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The session bar always describes the active tab. <em>Reconnect</em> is offered exactly where
    /// a new attempt is legal (Failed, or Idle after a normal disconnect) and <em>Cancel</em>
    /// exactly where a retry is pending, so the two are never both on screen.
    /// </summary>
    private void UpdateSessionBar()
    {
        var tab = _sessions.Active;
        SessionLabel.Text = tab is null
            ? Strings.Shell_NoSession
            : Text.Of(Strings.Shell_SessionLabel, tab.Title, tab.Subtitle, tab.StateText);

        bool live = tab is not null && !_closeInProgress;
        ReconnectButton.Visibility = live && tab!.State is SessionState.Failed or SessionState.Idle
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelRetryButton.Visibility = live && tab!.State is SessionState.Interrupted or SessionState.Reconnecting
            ? Visibility.Visible
            : Visibility.Collapsed;
        DiagnosticsButton.IsEnabled = live;
        DisconnectButton.IsEnabled = live;
    }

    /// <summary>
    /// Reports the active session's state in the one place RemoteDeck reports anything. Severity
    /// follows the disconnect family (§6.4): codes 0–3 are informational, a network drop is a
    /// warning — it is being retried — and everything else is an error, with Windows' own wording
    /// attached because that is the only text that names the actual cause.
    /// </summary>
    private void UpdateSessionInfoBar(SessionTabViewModel tab)
    {
        var session = tab.Session;
        var disconnect = session.LastDisconnect;

        switch (tab.State)
        {
            case SessionState.Idle when disconnect is null:
                // Freshly opened, nothing has happened yet: leave whatever is on screen alone.
                break;

            case SessionState.Idle:
                // disconnect.Title comes from RemoteDeck.Core and stays English in v1 (spec §9):
                // only the wording around it is localised.
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    Text.Of(Strings.Session_DisconnectedTitle, tab.Title), disconnect!.Title);
                break;

            case SessionState.Connecting:
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    Text.Of(Strings.Session_ConnectingTitle, tab.Title), tab.Subtitle);
                break;

            case SessionState.Connected:
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
                    Text.Of(Strings.Session_ConnectedTitle, tab.Title), tab.Subtitle);
                break;

            case SessionState.Interrupted:
                // The countdown is empty for the tick between the drop and the first timer tick;
                // the attempt then stands on its own rather than behind a leading space.
                string progress = Text.Of(Strings.Session_AttemptProgress, session.Attempt, ReconnectPolicy.MaxAttempts);
                StatusBar.Show(SeverityFor(disconnect),
                    Text.Of(Strings.Session_InterruptedTitle, tab.Title,
                        disconnect?.Title ?? Strings.Session_ConnectionLost),
                    Join(tab.CountdownText.Length == 0
                            ? progress
                            : Text.Of(Strings.Session_CountdownWithProgress, tab.CountdownText, progress),
                        WindowsWording(session)));
                break;

            case SessionState.Reconnecting:
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning,
                    Text.Of(Strings.Session_ReconnectingTitle, tab.Title),
                    Text.Of(Strings.Session_ReconnectingMessage, session.Attempt, ReconnectPolicy.MaxAttempts));
                break;

            case SessionState.Failed:
                StatusBar.Show(SeverityFor(disconnect),
                    Text.Of(Strings.Session_FailedTitle, tab.Title,
                        disconnect?.Title ?? Strings.Session_CouldNotConnect),
                    WindowsWording(session));
                break;

            default:
                // Closing and Closed: the tab is on its way out, the bar has nothing useful to add.
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

    // ---------------------------------------------------------------- editor and delete

    /// <summary><c>null</c> means "new connection"; both cases go through the same modal editor.</summary>
    private void OnEditRequested(Connection? existing)
    {
        if (_connections is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DatabaseUnavailableTitle,
                Strings.Shell_DatabaseNoEditMessage);
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
    /// <remarks>
    /// A connection with an open tab has its session closed first, through the same §6.5 protocol
    /// as any other close: deleting the row out from under a live session would leave a tab whose
    /// title names something that no longer exists. <c>async void</c> for that await, fully guarded.
    /// </remarks>
    private async void OnDeleteRequested(Connection connection)
    {
        if (connection is null)
        {
            return;
        }

        if (_connections is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DatabaseUnavailableTitle,
                Strings.Shell_DatabaseNoDeleteMessage);
            return;
        }

        if (_pendingDelete is { } armed && armed.Id == connection.Id)
        {
            DisarmDelete();
            try
            {
                if (_sessions.Find(connection.Id) is { } tab)
                {
                    StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                        Text.Of(Strings.Shell_ClosingConnectionTitle, connection.Name),
                        Strings.Shell_ClosingConnectionMessage);
                    await _sessions.CloseAsync(tab);
                }

                _connections.Delete(connection.Id);
                ProbeLog.Write("connections", $"'{connection.Name}' deleted");
                _list?.Reload();
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success, Strings.Shell_ConnectionDeletedTitle,
                    Text.Of(Strings.Shell_ConnectionDeletedMessage, connection.Name));
            }
            catch (Exception ex)
            {
                ProbeLog.Write("connections", $"Delete failed: {ex.GetType().Name}: {ex.Message}");
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Common_DeleteFailedTitle, ex.Message);
            }

            return;
        }

        _pendingDelete = connection;
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning,
            Text.Of(Strings.Shell_DeleteConfirmTitle, connection.Name), Strings.Shell_DeleteConfirmMessage);
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

    /// <summary>
    /// Opens the import preview, from the palette or from the pane's Import button. The window writes
    /// nothing until the user presses Import, and reports its own outcome; the shell only has to pick
    /// up what landed in the table.
    /// </summary>
    private void ImportConnections()
    {
        if (_connections is null)
        {
            // ImportWindow resolves the repository with GetRequiredService: opening it without a
            // database would throw instead of showing anything.
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DatabaseUnavailableTitle,
                Strings.Shell_DatabaseNoImportMessage);
            return;
        }

        var import = new ImportWindow { Owner = this };
        import.ShowDialog();
        if (import.ImportedCount > 0)
        {
            _list?.Reload();
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
                Text.Plural(import.ImportedCount, Strings.Import_ImportedOne, Strings.Import_ImportedMany,
                    import.ImportedCount),
                Strings.Import_ImportedMessage);
        }
    }

    private void OnManageCredentials(object sender, RoutedEventArgs e) => ManageCredentials();

    /// <summary>Opens the credential manager, from the toolbar button or from the palette.</summary>
    private void ManageCredentials()
    {
        if (_credentials is null)
        {
            // CredentialsWindow resolves the repository with GetRequiredService: opening it without
            // a database would throw instead of showing anything.
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DatabaseUnavailableTitle,
                Strings.Shell_DatabaseNoCredentialsMessage);
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
        _settings.LastConnectionId = _list?.SelectedConnection?.Id
            ?? _sessions.Active?.Session.Connection.Id
            ?? _settings.LastConnectionId;

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
    /// runs the graceful <c>RequestClose</c> protocol on every open tab, one at a time, so each
    /// server is told to end its session instead of being left with a zombie one; the second pass
    /// releases the COM objects. <c>async void</c> is the only shape available to an event handler
    /// that must await, so the body is fully guarded — whatever happens, the window still closes.
    /// </summary>
    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // The window stays interactive for up to the close-all budget, so the close box can be
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

        if (_closeConfirmed || _sessions.Tabs.Count == 0)
        {
            try
            {
                // Second pass, or nothing was ever opened: let whatever is left go with the window.
                // The graceful path reaches this branch on its second pass, so unhooking here
                // covers both routes out of the window.
                _deleteDisarm.Stop();
                _shortcuts?.Dispose();
                _sessions.DisposeAll();
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
        // The cross, the middle-click and Ctrl+W all stop here: a close started now would race the
        // close-all pass below over the same tab.
        _sessions.CanCloseTabs = false;
        int count = _sessions.Tabs.Count;
        UpdateSessionBar();
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
            Text.Plural(count, Strings.Shell_ClosingSessionsOne, Strings.Shell_ClosingSessionsMany, count), "");

        try
        {
            await _sessions.CloseAllAsync(PerTabCloseTimeout, OverallCloseTimeout);
        }
        catch (Exception ex)
        {
            // A failed close must never trap the user in the window.
            ProbeLog.Write("close", $"CloseAllAsync failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }

        _closeConfirmed = true;
        // BeginInvoke, not a direct Close(): CloseAllAsync can complete synchronously (nothing
        // connected, or RequestClose answering controlCloseCanProceed), and closing from inside
        // the Closing handler that just set e.Cancel would re-enter it. Let this pass unwind.
        // (discarded: the returned DispatcherOperation is deliberately not awaited)
        _ = Dispatcher.BeginInvoke(() => Close());
    }
}
