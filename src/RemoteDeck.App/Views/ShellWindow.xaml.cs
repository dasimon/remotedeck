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

    /// <summary>
    /// How long one workspace action waits for its session to answer before the mount moves on to
    /// the next one. Deliberately the very budget the project already grants a single session to
    /// answer a single protocol exchange — <see cref="SessionsViewModel.DefaultCloseTimeout"/>, and
    /// <c>ClosePlan.PerSessionSeconds</c> behind it — rather than a number invented here: this is
    /// the same kind of promise, "one server gets five seconds to answer, then we carry on without
    /// it".
    /// </summary>
    private static readonly TimeSpan ConnectWaitTimeout = SessionsViewModel.DefaultCloseTimeout;

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

    /// <summary>The workspaces, or <c>null</c> in degraded mode — the same rule as
    /// <see cref="_connections"/>, whose database it shares.</summary>
    private WorkspaceRepository? _workspaces;

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

    /// <summary>True while <see cref="MountWorkspaceAsync"/> is walking a plan. Its own guard, and
    /// not <see cref="_connecting"/>: the mount really does yield between two connections now — it
    /// waits for each session to answer — and <c>_connecting</c> is false for the whole of that
    /// wait. Without this a second workspace started from the palette in that gap would interleave
    /// its opens with the first one's.</summary>
    private bool _mounting;

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
        // Ctrl+Shift+D is symmetrical: it tears the active tab off here, and takes a session back in
        // a detached window. Like Ctrl+K it needs no canExecute — no text control does anything with
        // it — and the hook takes it in both windows, so it works from inside a remote desktop too.
        InputBindings.Add(new KeyBinding(new RelayCommand(DetachActiveTab), Key.D, ModifierKeys.Control | ModifierKeys.Shift));

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
        _workspaces = App.Current.Services.GetService<WorkspaceRepository>();
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

        // Last, and deliberately so: the resume opens sessions, and OpenConnectionAsync returns on
        // its `_version is null` guard, so everything above has to have run first.
        // (discarded: the returned Task is deliberately not awaited — Loaded cannot be awaited, and
        // MountWorkspaceAsync reports its own failures)
        _ = RestoreLastSessionIfAsked();
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
        _list.FavoriteToggleRequested += OnFavoriteToggleRequested;
        _list.WorkspaceOpenRequested += OnPaneWorkspaceOpenRequested;
        _list.WorkspaceDeleteRequested += OnPaneWorkspaceDeleteRequested;
        _list.WorkspaceUpdateRequested += OnPaneWorkspaceUpdateRequested;

        // Pull, like StatusProvider: the pane owns no repository and must not start to. Set before
        // the reload below, so the first paint already has the workspaces.
        _list.WorkspacesProvider = () => _workspaces?.GetAll() ?? [];
        _list.ReloadWorkspaces();
        // The pane holds no reference to the sessions: the shell is the one place that knows both,
        // so it hands the list a way to ask rather than a way to be told.
        _list.StatusProvider = StatusOf;
        _list.RefreshStatuses();
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
    /// <remarks>
    /// <c>rememberDetached: false</c> — dragging the pane splitter is not one of the three triggers
    /// spec espaces §7 allows to write the per-connection placement memory, and a workspace that had
    /// just imposed its own rectangles would otherwise see them written over the fallback.
    /// </remarks>
    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (PaneColumn.ActualWidth >= MinimumPaneWidth)
        {
            _paneWidth = PaneColumn.ActualWidth;
        }

        SaveSettings(rememberDetached: false);
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
    /// The detached session window that currently holds the foreground, or <c>null</c> when it is
    /// the shell — or one of its dialogs, or another application — that does. The one place the
    /// keyboard routing asks "which window is this shortcut for".
    /// </summary>
    /// <remarks>
    /// Called from inside the hook callback as well as from the UI thread, so it does what the
    /// callback is allowed to do and nothing more: walk the application's window list and read
    /// <see cref="Window.IsActive"/>. No I/O, no dispatcher hop.
    /// </remarks>
    private static SessionWindow? ActiveSessionWindow()
    {
        // Fully qualified: UseWindowsForms puts System.Windows.Forms.Application in scope too.
        var windows = System.Windows.Application.Current?.Windows;
        if (windows is null)
        {
            return null;
        }

        foreach (Window window in windows)
        {
            if (window is SessionWindow { IsActive: true } session)
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a shortcut is the application's to take, or the focused window's and input's. The
    /// single definition of that rule: the low-level hook asks it before swallowing a keystroke, and
    /// the window's Ctrl+B / Ctrl+W key bindings ask it as their <c>canExecute</c> — a shortcut
    /// reaching WPF through the message pump never went past the hook, so one path alone would not
    /// do. Three rules live here:
    /// <list type="bullet">
    /// <item>F11 and Ctrl+Alt+Pause only mean something over a detached session window: the shell
    /// has no full screen of its own, and swallowing them there would cost the remote desktop a
    /// keystroke for nothing.</item>
    /// <item>Ctrl+Shift+D is always taken: it detaches over the shell and reattaches over a session
    /// window, and the whole point of the hook is that it works from inside a remote desktop, which
    /// is exactly where the user is when they want the session in a window of its own.</item>
    /// <item>Ctrl+Tab, Ctrl+Shift+Tab and Ctrl+B are the shell's alone: a detached window has
    /// neither a strip to cycle nor a pane to fold, so taking them there would cost the remote
    /// desktop a keystroke for nothing.</item>
    /// <item>Those three and Ctrl+W also mean something inside a text field — move between fields,
    /// delete the word to the left, jump back a word — and a system-wide hook that swallows them
    /// makes typing in the shell feel broken.</item>
    /// </list>
    /// Ctrl+K is never filtered: it is the only way into the command palette and has no meaning in a
    /// WPF input.
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
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
        {
            return true;
        }

        bool overSessionWindow = ActiveSessionWindow() is not null;

        if (shortcut is "F11" or "Ctrl+Alt+Pause")
        {
            return overSessionWindow;
        }

        if (shortcut is "Ctrl+Tab" or "Ctrl+Shift+Tab" or "Ctrl+B")
        {
            return !overSessionWindow && NotATextInput();
        }

        return shortcut is not "Ctrl+W" || NotATextInput();
    }

    /// <summary>Whether the keyboard focus is somewhere a caret would be. Qualified: UseWindowsForms
    /// puts its own TextBoxBase and ComboBox in scope through implicit usings. A read-only ComboBox
    /// has no caret, so Ctrl+W there is ours.</summary>
    private static bool NotATextInput() =>
        Keyboard.FocusedElement is not (System.Windows.Controls.Primitives.TextBoxBase
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.ComboBox { IsEditable: true });

    /// <summary>
    /// What an intercepted shortcut does. The hook has no idea which window is on screen, so the
    /// routing is here: a detached session window takes its own set, the shell takes the rest, and
    /// anything else that happens to be foreground — the editor, the credentials window, the palette
    /// itself — takes none. The <c>default</c> branch is the lot-0 probe message: it now only ever
    /// fires for a shortcut the interceptor learns to recognise before this switch learns to act on
    /// it.
    /// </summary>
    private void OnShortcut(string shortcut, ShortcutInterceptor.Mechanism mechanism)
    {
        if (ActiveSessionWindow() is { } window)
        {
            OnSessionWindowShortcut(window, shortcut);
            return;
        }

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
            case "Ctrl+Shift+D":
                DetachActiveTab();
                break;
            case "F11":
            case "Ctrl+Alt+Pause":
                // Full screen belongs to a detached window and the shell has none.
                // ShouldInterceptShortcut normally keeps these out of the shell altogether; this
                // covers the window going inactive between the verdict and the dispatched call.
                ProbeLog.Write("shortcuts", $"{shortcut} ignored: no detached session window is active");
                break;
            default:
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
                    Text.Of(Strings.Shell_ShortcutInterceptedTitle, shortcut),
                    Text.Of(Strings.Shell_ShortcutInterceptedMessage, mechanism));
                break;
        }
    }

    /// <summary>
    /// The same shortcuts, aimed at the detached window the user is actually looking at. Ctrl+W goes
    /// through that window's own <c>Close</c> so the §6.5 protocol runs exactly where its cross runs
    /// it, and the palette is owned by it rather than by a shell that may be behind another monitor.
    /// </summary>
    private void OnSessionWindowShortcut(SessionWindow window, string shortcut)
    {
        switch (shortcut)
        {
            case "Ctrl+W":
                if (_closeInProgress)
                {
                    break;
                }

                window.Close();
                break;

            case "F11":
            case "Ctrl+Alt+Pause":
                window.ToggleFullScreen();
                break;

            case "Ctrl+K":
                OpenCommandPalette(window);
                break;

            case "Ctrl+Shift+D":
                Reattach(window);
                break;

            default:
                // Ctrl+Tab, Ctrl+Shift+Tab and Ctrl+B: no strip to cycle, no pane to fold.
                // ShouldInterceptShortcut declines them here, so they reach the remote desktop
                // instead of being swallowed for nothing; this only covers a race with it.
                ProbeLog.Write("shortcuts", $"{shortcut} ignored: '{window.Tab.Title}' is a detached session window");
                break;
        }
    }

    // ---------------------------------------------------------------- command palette

    /// <summary>
    /// Opens the Ctrl+K palette and runs what comes back. Modal on purpose: the palette acts on the
    /// shell's own state (the tab list, the pane, the active session), and letting the shell change
    /// underneath it would make <c>tab:&lt;index&gt;</c> point at a different tab than the one shown.
    /// </summary>
    private void OpenCommandPalette() => OpenCommandPalette(null);

    /// <param name="from">The detached session window the palette was asked for, or <c>null</c> for
    /// the shell. It decides who owns the palette — a palette centred on a shell the user cannot see
    /// is a palette on the wrong monitor — and which of the two window commands is offered.</param>
    private void OpenCommandPalette(SessionWindow? from)
    {
        // _paletteOpen is not redundant with the modality: the low-level hook fires on Ctrl+K even
        // while the palette itself holds the focus. OnShortcut already refuses that case (neither
        // the shell nor a session window is IsActive), but the WPF KeyBinding on this window would
        // still stack a second palette when the first one is dismissed and the keystroke arrives
        // twice.
        if (_paletteOpen || _closeInProgress)
        {
            return;
        }

        string? chosen;
        _paletteOpen = true;
        try
        {
            var palette = new CommandPaletteWindow(BuildPaletteItems(from)) { Owner = from ?? (Window)this };
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
            RunPaletteChoice(chosen, from);
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
    /// <param name="from">The detached window the palette was opened from, or <c>null</c> for the
    /// shell.</param>
    private IReadOnlyList<PaletteItem> BuildPaletteItems(SessionWindow? from)
    {
        var items = new List<PaletteItem>();

        foreach (var connection in _connections?.GetAll() ?? [])
        {
            var group = string.IsNullOrWhiteSpace(connection.GroupName)
                ? ConnectionListViewModel.UngroupedGroup
                : connection.GroupName;
            items.Add(new PaletteItem(PaletteItemKind.Connection, $"{ConnectionIdPrefix}{connection.Id}",
                connection.Name, Text.Of(Strings.Palette_ConnectionSubtitle, group, connection.Host),
                ConnectionPriority, Group: Strings.Palette_GroupConnections));
        }

        // By index, not by connection id: an index is what Activate needs, and the strip cannot be
        // reordered while the palette is modal.
        for (int i = 0; i < _sessions.Tabs.Count; i++)
        {
            var tab = _sessions.Tabs[i];
            items.Add(new PaletteItem(PaletteItemKind.Session, $"{TabIdPrefix}{i}",
                Text.Of(Strings.Palette_SwitchToTab, tab.Title), tab.Subtitle, TabPriority,
                Group: Strings.Palette_GroupSessions));
        }

        // Every subtitle below says what the row does, and none of them restates its title: a second
        // line that only rephrases the first costs a glance and answers nothing. Keystrokes are no
        // longer written there either — they are the Shortcut, which the palette draws as a key cap.
        //
        // The chords come from the resources like every other drawn string (spec §9): the chord
        // never changes, but the names of its keys are Windows' own vocabulary and are translated
        // with it — French says Maj, not Shift, exactly as the footer already says Échap and Entrée.
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:new",
            Strings.Palette_NewConnection, Strings.Palette_NewConnectionSubtitle, CommandPriority,
            Shortcut: Strings.Palette_ShortcutNewConnection, Group: Strings.Palette_GroupCommands));
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:import",
            Strings.Palette_ImportConnections, Strings.Palette_ImportSubtitle, CommandPriority,
            Group: Strings.Palette_GroupCommands));
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:credentials",
            Strings.Palette_ManageCredentials, Strings.Palette_ManageCredentialsSubtitle, CommandPriority,
            Group: Strings.Palette_GroupCommands));
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:pane",
            Strings.Palette_TogglePane, Strings.Palette_TogglePaneSubtitle, CommandPriority,
            Shortcut: Strings.Palette_ShortcutTogglePane, Group: Strings.Palette_GroupCommands));
        // The one place the resume is switched on and off. Its subtitle carries the current state
        // rather than a rephrasing of the title: a toggle whose value cannot be read before pressing
        // Enter is a coin flip. Offered unconditionally — it is a setting, not an act on a session.
        items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:restore-toggle",
            Strings.Palette_ToggleRestore,
            Text.Of(Strings.Palette_ToggleRestoreSubtitle,
                _settings.RestoreLastSession ? Strings.Palette_On : Strings.Palette_Off),
            CommandPriority, Group: Strings.Palette_GroupCommands));
        // The session this palette is about: the one the window it was opened from is showing, or
        // the docked tab. Active is never a detached tab — Activate refuses them — so `from` is the
        // only thing that can name the session in front of the user here, and offering these two
        // rows unconditionally would aim them at whatever happens to be docked behind it.
        if ((from?.Tab ?? _sessions.Active) is not null)
        {
            // One entry, not two: RemoteDeck has no disconnect that keeps the tab behind — the
            // toolbar's own Disconnect button is CloseActiveTab as well — so a second "Disconnect"
            // row would name the same action twice. The subtitle carries the other half of the
            // vocabulary instead, and PaletteFilter searches it, so typing "disconnect" still finds
            // this row.
            items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:close",
                Strings.Palette_CloseSession, Strings.Palette_CloseSessionSubtitle, CommandPriority,
                Shortcut: Strings.Palette_ShortcutCloseSession, Group: Strings.Palette_GroupCommands));
            items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:reconnect",
                Strings.Palette_ReconnectTab, Strings.Palette_ReconnectTabSubtitle, CommandPriority,
                Group: Strings.Palette_GroupCommands));
        }

        // Exactly one of the two, and only when it would do something: from a detached window the
        // session can only go back, and from the shell only a docked active tab can leave. Offering
        // the other one would be a row that answers with nothing.
        //
        // Both subtitles name the session by its title rather than talking about "the current
        // session": these two rows move a specific window around, and the user is entitled to read
        // which one before pressing Enter.
        if (from is not null)
        {
            items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:reattach",
                Strings.Palette_ReattachSession,
                Text.Of(Strings.Palette_ReattachSessionSubtitle, from.Tab.Title), CommandPriority,
                Shortcut: Strings.Palette_ShortcutDetach, Group: Strings.Palette_GroupCommands));
        }
        else if (_sessions.Active is { IsDetached: false } active)
        {
            items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:detach",
                Strings.Palette_DetachSession,
                Text.Of(Strings.Palette_DetachSessionSubtitle, active.Title), CommandPriority,
                Shortcut: Strings.Palette_ShortcutDetach, Group: Strings.Palette_GroupCommands));
        }

        // Saving only means something with something to save. Offered from a detached window too,
        // and deliberately: the capture reads every session, so where the palette was opened from
        // changes nothing about what it records — and arranging sessions across monitors, then
        // saving that arrangement, is the whole point of the feature. Hiding it from a full-screen
        // window would mean leaving full screen to save the layout that full screen is part of.
        if (_sessions.Tabs.Count > 0)
        {
            items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:workspace-save",
                Strings.Palette_SaveLayout, Strings.Palette_SaveLayoutSubtitle, CommandPriority,
                Group: Strings.Palette_GroupWorkspaces));
        }

        foreach (var workspace in _workspaces?.GetAll() ?? [])
        {
            items.Add(new PaletteItem(PaletteItemKind.Command,
                string.Create(CultureInfo.InvariantCulture, $"ws:{workspace.Id}"),
                Text.Of(Strings.Palette_OpenWorkspace, workspace.Name),
                Text.Plural(workspace.Items.Count, Strings.Workspace_CountOne, Strings.Workspace_CountMany,
                    workspace.Items.Count), CommandPriority,
                Group: Strings.Palette_GroupWorkspaces));
            items.Add(new PaletteItem(PaletteItemKind.Command,
                string.Create(CultureInfo.InvariantCulture, $"wsdel:{workspace.Id}"),
                Text.Of(Strings.Palette_DeleteWorkspace, workspace.Name),
                Strings.Palette_DeleteWorkspaceSubtitle, CommandPriority,
                Group: Strings.Palette_GroupWorkspaces));
        }

        return items;
    }

    /// <summary>
    /// Runs one palette entry. Unknown, malformed and stale ids are ignored rather than reported:
    /// they can only come from the list this window built moments earlier, and a row whose
    /// connection was deleted meanwhile is a race, not a mistake the user made.
    /// </summary>
    /// <param name="from">The detached window the palette was opened from, or <c>null</c> for the
    /// shell — the only thing that knows which session <c>cmd:reattach</c> means.</param>
    private void RunPaletteChoice(string id, SessionWindow? from)
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

        if (id.StartsWith("ws:", StringComparison.Ordinal))
        {
            // A mount in flight owns the strip until it is done, so a second one is simply not
            // taken. MountWorkspaceAsync refuses it as well; asking here spares the repository read
            // and puts the refusal where the user's gesture landed.
            if (!_mounting
                && long.TryParse(id.AsSpan(3), CultureInfo.InvariantCulture, out long openId)
                && _workspaces?.Get(openId) is { } toOpen)
            {
                // Fire and forget: the mount is asynchronous because it connects in series, and the
                // palette has nothing to wait for. MountWorkspaceAsync reports its own failures —
                // its whole body sits inside one try — so the dropped task can carry none.
                _ = MountWorkspaceAsync(toOpen);
            }

            return;
        }

        if (id.StartsWith("wsdel:", StringComparison.Ordinal))
        {
            // Confirmed, and deliberately not the two-press arm/confirm the connection list uses:
            // the palette closes on selection and cannot hold an armed state. Open and Delete sit
            // next to each other in the same group with near-identical text, and a deletion has no
            // undo — while saving over a name, which destroys nothing, already asks.
            if (long.TryParse(id.AsSpan(6), CultureInfo.InvariantCulture, out long deleteId))
            {
                DeleteWorkspace(deleteId, from);
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

            case "cmd:restore-toggle":
                _settings.RestoreLastSession = !_settings.RestoreLastSession;
                // Written now rather than at the next clean close: this is the user's answer to a
                // question, and a crash must not take it back. rememberDetached: false — answering
                // that question is not one of the three triggers spec espaces §7 allows to write the
                // per-connection placement memory, and a Ctrl+K toggle right after a workspace was
                // mounted would otherwise stamp that workspace's imposed rectangles onto the
                // fallback the next one relies on.
                SaveSettings(rememberDetached: false);

                // Say so. The palette closes on Enter and this setting has no visible surface of its
                // own, so without this the toggle looks like it did nothing — the only way to learn
                // the new state was to reopen the palette and read the subtitle again. A setting the
                // user cannot confirm they changed is a setting they will change twice.
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    _settings.RestoreLastSession ? Strings.Shell_RestoreOnTitle : Strings.Shell_RestoreOffTitle,
                    _settings.RestoreLastSession ? Strings.Shell_RestoreOnMessage : Strings.Shell_RestoreOffMessage);
                break;

            case "cmd:workspace-save":
                SaveCurrentLayout(from);
                break;

            // BuildPaletteItems no longer produces "cmd:disconnect": RemoteDeck has no disconnect
            // that keeps the tab behind, so "Close current session" is the one entry covering both
            // words. The old id is still accepted here — it costs a line and can mean nothing else.
            case "cmd:close":
            case "cmd:disconnect":
                if (from is not null)
                {
                    // Its own close, i.e. the very path Ctrl+W and the cross take in that window:
                    // the geometry is remembered before §6.5 takes the session down.
                    if (!_closeInProgress)
                    {
                        from.Close();
                    }
                }
                else if (_sessions.Active is null)
                {
                    StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, Strings.Shell_NoSession,
                        Strings.Shell_NoTabToCloseMessage);
                }
                else
                {
                    CloseActiveTab();
                }

                break;

            case "cmd:reconnect":
                if ((from?.Tab ?? _sessions.Active) is not { } toReconnect)
                {
                    StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational, Strings.Shell_NoSession,
                        Strings.Shell_NoTabToReconnectMessage);
                    break;
                }

                ReconnectTab(toReconnect);
                break;

            case "cmd:detach":
                DetachActiveTab();
                break;

            case "cmd:reattach":
                if (from is not null)
                {
                    Reattach(from);
                }

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
    private void OnDetachRequested(SessionTabViewModel tab, System.Windows.Point screenPoint) =>
        DetachTab(tab, screenPoint);

    /// <summary>Ctrl+Shift+D and the palette's <c>cmd:detach</c>: the active tab leaves for a window
    /// of its own. No pointer behind the gesture, hence no drop point — the window opens where this
    /// connection was last seen, or centred.</summary>
    private void DetachActiveTab()
    {
        if (_sessions.Active is { IsDetached: false } tab)
        {
            // Cast: the workspace overload takes a nullable too, and a bare null no longer says
            // which. This is still the pointer-less detach it has always been.
            DetachTab(tab, (System.Windows.Point?)null);
        }
    }

    /// <summary>
    /// Tears <paramref name="tab"/> off into a window of its own. Where that window opens is decided
    /// in this order: the placement remembered for this connection in <c>settings.json</c>, fitted
    /// onto the monitors present right now; failing that, under the pointer that dragged the tab
    /// out; failing that, centred. A remembered full screen is entered once the session is really in
    /// the window — a window that never got one has nothing to show full screen.
    /// </summary>
    /// <param name="screenPoint">Where the tab was dropped, in screen device pixels, or <c>null</c>
    /// when the detach came from the keyboard or the palette.</param>
    private void DetachTab(SessionTabViewModel tab, System.Windows.Point? screenPoint)
    {
        if (_closeInProgress || tab.IsDetached)
        {
            return;
        }

        var window = new SessionWindow(tab, _sessions);
        var placement = RememberedPlacement(tab, window)
            ?? (screenPoint is { } point ? PlaceUnder(point, window) : null);
        if (placement is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = placement.Left;
            window.Top = placement.Top;
            window.Width = placement.Width;
            window.Height = placement.Height;
        }
        else
        {
            // Nothing remembered and no pointer, or a pointer belonging to no screen ScreenFit knows
            // about. Centring is the reachable answer either way.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ReattachRequested += OnSessionWindowReattachRequested;
        window.CloseRequested += OnSessionWindowCloseRequested;
        window.CaptionDragMoved += OnSessionWindowCaptionDragMoved;
        window.CaptionDragEnded += OnSessionWindowCaptionDragEnded;
        window.SessionRequested += GoToSession;
        window.Show();

        if (_sessions.Detach(tab, window))
        {
            if (placement?.FullScreen == true)
            {
                window.ToggleFullScreen();
            }

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

    // ---------------------------------------------------------------- remembered geometry

    /// <summary>How a detached window is keyed in <c>settings.json</c>: by its connection, so the
    /// same machine reopens where it was, whatever tab it happens to be. Invariant text because
    /// <c>System.Text.Json</c> only round-trips string-keyed dictionaries.</summary>
    private static string PlacementKey(SessionTabViewModel tab) =>
        tab.Session.Connection.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Where this connection's window was last seen, adjusted to the monitors present right now, or
    /// <c>null</c> when nothing was remembered or when the screen it was on is gone.
    /// <see cref="ScreenFit"/> is what turns "last seen on the monitor that has since been
    /// unplugged" into "forget it" rather than "open it where nobody can reach it".
    /// </summary>
    private DetachedWindowPlacement? RememberedPlacement(SessionTabViewModel tab, SessionWindow window) =>
        ScreenFit.Choose(_settings.DetachedWindows.GetValueOrDefault(PlacementKey(tab)),
            Screens(VisualTreeHelper.GetDpi(this)), window.MinWidth, window.MinHeight);

    /// <summary>
    /// Where a detached window is right now, or <c>null</c> when that describes nothing usable.
    /// A minimized window has no placement: it shows nothing the user could recognise, and what is
    /// already on file — where it was before it was minimized — is the better answer.
    /// <see cref="SessionWindow.CurrentPlacement"/> handles the maximized and full-screen cases
    /// itself, reporting the restore bounds rather than the screen-sized frame.
    /// </summary>
    private static DetachedWindowPlacement? PlacementOf(SessionWindow window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            return null;
        }

        var placement = window.CurrentPlacement();
        return placement is { Width: > 0, Height: > 0 } ? placement : null;
    }

    /// <summary>
    /// Records where a detached window is, for the next time this connection is torn off. Called on
    /// both paths out of a detached window — its own close, and the shell saving on the way down —
    /// and on a reattach, which is a window disappearing just the same.
    /// </summary>
    private void RememberPlacement(SessionWindow window)
    {
        if (PlacementOf(window) is { } placement)
        {
            _settings.DetachedWindows[PlacementKey(window.Tab)] = placement;
        }
    }

    /// <summary>
    /// A session was picked from a full-screen bar. The shell is the only thing that knows where each
    /// session is, so it is the only thing that can say what "go there" means: a detached session is
    /// its own window and is simply brought forward, keeping whatever full screen it was in; a docked
    /// one is a tab, so it is activated and the main window raised over it.
    ///
    /// The window the pick came from is deliberately left alone — still full screen, still showing
    /// its own session. Nothing is re-parented and nothing reconnects; this is navigation, not a
    /// move.
    /// </summary>
    private void GoToSession(SessionTabViewModel tab)
    {
        if (_sessions.DetachedWindowOf(tab) is { } window)
        {
            // A minimised window has to be restored first: Activate() on one only flashes its
            // taskbar button. WindowState is the window's own, not the full-screen bookkeeping —
            // SessionWindow restores that itself when it needs to.
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            _ = window.Activate();
            return;
        }

        _sessions.Activate(tab);

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
    }

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
        if (_closeInProgress)
        {
            // Same refusal as DetachTab: the close-all pass is walking the tabs, and a host moved
            // between two windows behind it is a control it can no longer find where it left it.
            return;
        }

        // Before the window goes: a session coming back is a window disappearing, and the next
        // detach of this connection should still find where the user had put it.
        RememberPlacement(window);
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
    /// tab's own cross run, and it is what closes that window when it is done — so the geometry is
    /// taken here, while the window is still where the user left it.</summary>
    private void OnSessionWindowCloseRequested(SessionWindow window)
    {
        RememberPlacement(window);
        _ = _sessions.CloseAsync(window.Tab);
    }

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

    // ---------------------------------------------------------------- workspaces

    /// <summary>
    /// Captures the open sessions as a named workspace. Where each detached window sits is read off
    /// the real window rather than off what was remembered: what a workspace records is what the
    /// user sees on screen at the moment they record it.
    /// </summary>
    /// <param name="from">The detached window the palette was opened from, or <c>null</c> for the
    /// shell. It owns the dialogs below, the same way it owns the palette itself: a window owned by
    /// the shell would open <em>behind</em> a full-screen session, which is topmost — an invisible
    /// modal, and an application that looks frozen.</param>
    /// <param name="existing">The workspace being updated, or <c>null</c> to capture a new one. It
    /// only pre-fills the dialog: an update is the same capture under a name already taken, which is
    /// exactly what replacing means here. There is no editor for a workspace, so re-capturing is how
    /// one changes — this parameter is what makes that reachable from the row itself instead of
    /// requiring the user to know it and retype the name.</param>
    private void SaveCurrentLayout(SessionWindow? from, Workspace? existing = null)
    {
        if (_workspaces is null || _sessions.Tabs.Count == 0)
        {
            return;
        }

        // The cast is what the palette's own owner line already does: the two window types share no
        // base but Window, so the conditional needs to be told which one it is producing.
        Window owner = from ?? (Window)this;
        var dialog = new WorkspaceNameWindow(existing?.Name, existing?.AutoConnect ?? true) { Owner = owner };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // A duplicate name is not a mistake: replacing is the only way a workspace can evolve, since
        // there is no editor for one. But it overwrites, so it is confirmed.
        if (_workspaces.FindByName(dialog.WorkspaceName) is not null)
        {
            var confirm = System.Windows.MessageBox.Show(owner,
                Text.Of(Strings.WorkspaceName_ReplaceMessage, dialog.WorkspaceName),
                Text.Of(Strings.WorkspaceName_ReplaceTitle, dialog.WorkspaceName),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }
        }

        var workspace = new Workspace { Name = dialog.WorkspaceName, AutoConnect = dialog.AutoConnect };
        for (int i = 0; i < _sessions.Tabs.Count; i++)
        {
            var tab = _sessions.Tabs[i];
            var window = _sessions.DetachedWindowOf(tab);
            workspace.Items.Add(new WorkspaceItem
            {
                ConnectionId = tab.Session.Connection.Id,
                Ordinal = i,
                Detached = window is not null,
                Placement = window is null ? null : PlacementOf(window),
            });
        }

        // Guarded like every other repository write in this file: a locked database, a full disk or
        // a read-only %APPDATA% throws here, and an unhandled exception on the UI thread takes the
        // process down with every live RDP session — without the §6.5 close protocol, which is
        // exactly the server-side zombie this project spends its shutdown avoiding.
        try
        {
            _ = _workspaces.Save(workspace);
        }
        catch (Exception ex)
        {
            ProbeLog.Write("workspaces",
                $"Saving '{workspace.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error,
                Strings.Shell_WorkspaceSaveFailedTitle, ex.Message);
            return;
        }

        // The pane shows the workspaces, so a capture changes it even though no connection moved.
        _list?.ReloadWorkspaces();

        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
            Text.Of(Strings.Shell_WorkspaceSaved, workspace.Name),
            Text.Plural(workspace.Items.Count, Strings.Shell_WorkspaceSavedOne,
                Strings.Shell_WorkspaceSavedMany, workspace.Items.Count));
    }

    /// <summary>
    /// Deletes a workspace after confirming it. One method for the two entry points — the palette's
    /// <c>wsdel:</c> row and the pane's context menu — so the confirmation cannot exist on one path
    /// and be forgotten on the other.
    /// </summary>
    /// <param name="from">The detached window the gesture came from, or <c>null</c> for the shell.
    /// It owns the message box, which would otherwise open behind a full-screen session.</param>
    /// <remarks>
    /// A single press, deliberately not the two-step arming the connection list uses: the palette
    /// closes on selection and cannot hold an armed state. Deleting has no undo, while saving over a
    /// name — which destroys nothing — already asks, so the risk ordering would otherwise be
    /// inverted.
    /// </remarks>
    private void DeleteWorkspace(long id, SessionWindow? from)
    {
        if (_workspaces?.Get(id) is not { } toDelete)
        {
            // Already gone: a race between the click and the write, not a mistake the user made.
            return;
        }

        var confirm = System.Windows.MessageBox.Show(from ?? (Window)this,
            Strings.Shell_DeleteWorkspaceMessage,
            Text.Of(Strings.Shell_DeleteWorkspaceTitle, toDelete.Name),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        // Guarded like every other repository write in this file: a locked database, a full disk or
        // a read-only %APPDATA% throws here, and an unhandled exception on the UI thread takes the
        // process down with every live RDP session — without the §6.5 close protocol, which is
        // exactly the server-side zombie this project spends its shutdown avoiding.
        try
        {
            _workspaces.Delete(id);
            _list?.ReloadWorkspaces();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("workspaces",
                $"Deleting '{toDelete.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error,
                Strings.Common_DeleteFailedTitle, ex.Message);
        }
    }

    /// <summary>
    /// <em>Update</em> from a workspace row's context menu: re-captures the sessions as they are now,
    /// under that workspace's name.
    /// </summary>
    /// <remarks>
    /// The same capture the palette runs, with the name and the auto-connect box already filled, so
    /// it lands on the replace confirmation rather than on an empty form. Re-capturing is the only
    /// way a workspace changes — there is deliberately no editor — and until this entry existed the
    /// user had to know that and retype the name exactly.
    /// </remarks>
    private void OnPaneWorkspaceUpdateRequested(long id)
    {
        if (_workspaces?.Get(id) is { } workspace)
        {
            SaveCurrentLayout(from: null, existing: workspace);
        }
    }

    /// <summary>A workspace row was clicked in the pane.</summary>
    private void OnPaneWorkspaceOpenRequested(long id)
    {
        if (_workspaces?.Get(id) is { } workspace)
        {
            _ = MountWorkspaceAsync(workspace);
        }
    }

    /// <summary><em>Delete</em> from a workspace row's context menu. Same confirmation as the palette.</summary>
    private void OnPaneWorkspaceDeleteRequested(long id) => DeleteWorkspace(id, from: null);

    /// <summary>
    /// Detaches <paramref name="tab"/> at an imposed placement. A workspace's own placement wins
    /// over the per-connection memory, which is only the fallback when the workspace has none —
    /// otherwise opening "INCIDENT" would leave "PROD" unusable.
    /// </summary>
    private void DetachTab(SessionTabViewModel tab, DetachedWindowPlacement? placement)
    {
        if (_closeInProgress || tab.IsDetached)
        {
            return;
        }

        var window = new SessionWindow(tab, _sessions);
        var chosen = placement ?? RememberedPlacement(tab, window);
        if (chosen is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = chosen.Left;
            window.Top = chosen.Top;
            window.Width = chosen.Width;
            window.Height = chosen.Height;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ReattachRequested += OnSessionWindowReattachRequested;
        window.CloseRequested += OnSessionWindowCloseRequested;
        window.CaptionDragMoved += OnSessionWindowCaptionDragMoved;
        window.CaptionDragEnded += OnSessionWindowCaptionDragEnded;
        window.SessionRequested += GoToSession;
        window.Show();

        if (_sessions.Detach(tab, window))
        {
            if (chosen?.FullScreen == true)
            {
                window.ToggleFullScreen();
            }

            return;
        }

        window.AllowClose();
        window.Close();
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DetachRefusedTitle,
            Text.Of(Strings.Shell_DetachRefusedMessage, tab.Title));
    }

    /// <summary>
    /// Mounts a workspace: each connection is brought into the state the workspace describes.
    /// Nothing is closed — a workspace adds, it does not replace — and nothing reconnects: a session
    /// that is already open is moved, which is re-parenting.
    /// </summary>
    /// <param name="announceEmpty">False for the resume at startup: an empty resume is the normal
    /// case there, not an incident worth warning about.</param>
    /// <remarks>
    /// In series and not in parallel: six simultaneous RDP negotiations over a network that has just
    /// come up are six failures, none of which is any machine's fault. In series means the next
    /// action is not started until the previous session has connected or failed — see
    /// <see cref="WaitForConnectionAsync"/>, without which the loop would only serialise the
    /// <em>issuing</em> of six connections that then negotiate together anyway.
    /// </remarks>
    private async Task MountWorkspaceAsync(Workspace workspace, bool announceEmpty = true)
    {
        // Everything is inside the try, the null check included: the only call sites drop the task
        // on the floor, and an unobserved faulted task in .NET neither crashes nor logs a line. A
        // mount that failed on anything other than a connection would otherwise be a palette that
        // closes and does nothing at all, with nothing written anywhere to say why.
        try
        {
            ArgumentNullException.ThrowIfNull(workspace);

            // _connecting too: a mount opens sessions without going through OnConnectRequested, so
            // its re-entrancy guard is not on this path. Two mounts overlapping — the second started
            // from the palette while the first is waiting on a session — would each open a tab for
            // the same connection, and the duplicate would then break every later mount. _mounting
            // is what closes that door now that the loop really does yield: _connecting is only
            // raised inside OpenConnectionAsync and is false for the whole of the wait between two
            // connections.
            if (_connections is null || _closeInProgress || _connecting || _mounting)
            {
                return;
            }

            _mounting = true;
            try
            {
                var existing = _connections.GetAll().Select(c => c.Id).ToHashSet();

                // Through Find rather than straight off Tabs: Find drops the tabs whose close is
                // already in flight, and the plan is applied through Find as well. Built on the
                // wider set, a session that is going away would be planned as Activate and then
                // silently dropped, instead of being reopened. Indexed assignment, not ToDictionary:
                // a duplicate key is a throw there, and this is the one place that must survive one.
                var open = new Dictionary<long, bool>();
                foreach (var tab in _sessions.Tabs)
                {
                    if (_sessions.Find(tab.Session.Connection.Id) is { } live)
                    {
                        open[live.Session.Connection.Id] = live.IsDetached;
                    }
                }

                var plan = WorkspacePlan.Build(workspace, existing, open, Screens(VisualTreeHelper.GetDpi(this)));

                if (plan.Count == 0)
                {
                    if (announceEmpty)
                    {
                        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                            Strings.Shell_WorkspaceEmptyTitle,
                            Text.Of(Strings.Shell_WorkspaceEmptyMessage, workspace.Name));
                    }

                    return;
                }

                foreach (var action in plan)
                {
                    // Re-tested on every turn, not only on entry: the loop really yields now, and
                    // the window can start going down between two connections. Opening a further
                    // session into a close-all pass that has already walked past it would leave the
                    // server with exactly the zombie the §6.5 protocol exists to avoid.
                    if (_closeInProgress)
                    {
                        break;
                    }

                    await ApplyWorkspaceActionAsync(action, workspace.AutoConnect);
                }
            }
            finally
            {
                // Every path out, the throw included: a flag left raised would refuse every later
                // workspace for the life of the process.
                _mounting = false;
            }
        }
        catch (Exception ex)
        {
            // The same pair OpenConnectionAsync uses on its own failure path. The workspace is not
            // named: the throw can be the null check itself, and there would then be nothing to name.
            ProbeLog.Write("session",
                $"Mounting a workspace failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Shell_ConnectFailedTitle,
                Text.Of(Strings.Shell_ConnectFailedMessage, ex.GetType().Name,
                    ex.HResult.ToString("X8", CultureInfo.InvariantCulture), ex.Message));
        }
    }

    /// <summary>One action of the plan.</summary>
    private async Task ApplyWorkspaceActionAsync(WorkspaceAction action, bool autoConnect)
    {
        var tab = _sessions.Find(action.ConnectionId);

        switch (action.Kind)
        {
            case WorkspaceActionKind.Activate when tab is not null:
                GoToSession(tab);
                break;

            case WorkspaceActionKind.MoveDetached when tab is not null:
                if (_sessions.DetachedWindowOf(tab) is { } window && action.Placement is { } target)
                {
                    // Leave full screen first: moving a full-screen window means nothing, and
                    // SetFullScreen restores its own bounds on the way out.
                    if (window.IsFullScreen && !target.FullScreen)
                    {
                        window.ToggleFullScreen();
                    }

                    // And out of maximized as well — the same normalisation SetFullScreen does on
                    // the way in. Left/Top/Width/Height on a window that is not Normal change
                    // nothing the user can see, and CurrentPlacement saved the restore rectangle of
                    // a maximized window precisely so that it could be applied here.
                    if (!window.IsFullScreen && window.WindowState != WindowState.Normal)
                    {
                        window.WindowState = WindowState.Normal;
                    }

                    if (!window.IsFullScreen)
                    {
                        window.Left = target.Left;
                        window.Top = target.Top;
                        window.Width = target.Width;
                        window.Height = target.Height;
                    }

                    if (target.FullScreen && !window.IsFullScreen)
                    {
                        window.ToggleFullScreen();
                    }
                }

                GoToSession(tab);
                break;

            case WorkspaceActionKind.Detach when tab is not null:
                DetachTab(tab, action.Placement);
                break;

            case WorkspaceActionKind.Reattach when tab is not null:
                if (_sessions.DetachedWindowOf(tab) is { } toReattach)
                {
                    Reattach(toReattach);
                }

                break;

            // Only when nothing is open for this connection. The plan was built before the first
            // await and can be stale by the time it is applied; OpenConnectionAsync has no
            // already-open guard of its own — that one lives in OnConnectRequested, which this path
            // does not go through — and SessionsViewModel.Open does not enforce one session per
            // connection either. Without this guard a stale Open would build a second tab and a
            // second RDP session for a connection that already has one.
            case WorkspaceActionKind.OpenDocked when tab is null:
            case WorkspaceActionKind.OpenDetached when tab is null:
                if (_connections?.Get(action.ConnectionId) is { } connection)
                {
                    await OpenConnectionAsync(connection, start: autoConnect);

                    // Find again: the tab did not exist before the open.
                    if (_sessions.Find(action.ConnectionId) is not { } opened)
                    {
                        break;
                    }

                    // The wait that makes "in series" mean what §4.2 says it means. StartAsync only
                    // *issues* the connection — the ActiveX negotiation is asynchronous and the
                    // session is Connecting when it returns — so without this the loop would
                    // serialise six issuings and leave six negotiations to run together, which is
                    // the very thing being avoided. Nothing to wait for when AutoConnect is off: the
                    // tab is deliberately left Idle.
                    if (autoConnect)
                    {
                        await WaitForConnectionAsync(opened);
                    }

                    // Only now, and this is the other half of the wait: SetFullScreen refuses any
                    // session that is not Connected, and until here it never was. A session that
                    // failed or never answered is still detached, in a window of its own, simply not
                    // full screen — which is what a detached window whose session is down looks like
                    // anywhere else in the product.
                    if (action.Kind == WorkspaceActionKind.OpenDetached)
                    {
                        DetachTab(opened, action.Placement);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Waits until <paramref name="tab"/>'s session has stopped negotiating — connected, dropped,
    /// failed or ended — or until <see cref="ConnectWaitTimeout"/> runs out, whichever comes first.
    /// Never throws and never waits longer than the cap, so a machine that answers nothing costs the
    /// mount five seconds and the next action still runs (spec §4.3: a failure is isolated to its
    /// own session).
    /// </summary>
    /// <remarks>
    /// Built on <see cref="SessionTabViewModel.Changed"/>, which is raised on the UI thread, and the
    /// handler is removed in a <c>finally</c> so neither the timeout nor a throw can leave this
    /// window subscribed to a session it no longer cares about. The state is re-read after
    /// subscribing: it can settle between the first test and the subscription, and the event that
    /// said so is gone by then.
    /// </remarks>
    private static async Task WaitForConnectionAsync(SessionTabViewModel tab)
    {
        if (HasSettled(tab.State))
        {
            return;
        }

        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(SessionTabViewModel changed)
        {
            if (HasSettled(changed.State))
            {
                settled.TrySetResult();
            }
        }

        tab.Changed += OnChanged;
        try
        {
            if (HasSettled(tab.State))
            {
                return;
            }

            // WhenAny, never the completion source alone: a control that raises nothing at all —
            // no OnConnected, no OnDisconnected — would otherwise hang the mount for good.
            using var cap = new CancellationTokenSource(ConnectWaitTimeout);
            await Task.WhenAny(settled.Task, Task.Delay(Timeout.Infinite, cap.Token));

            if (!HasSettled(tab.State))
            {
                ProbeLog.Write("session",
                    $"'{tab.Title}': still {tab.State} after {ConnectWaitTimeout.TotalSeconds:F0}s; the mount carries on");
            }
        }
        finally
        {
            tab.Changed -= OnChanged;
        }
    }

    /// <summary>
    /// Whether a session has stopped negotiating. <see cref="SessionState.Connecting"/> and
    /// <see cref="SessionState.Reconnecting"/> are the only two states where an answer is still on
    /// its way; every other one — connected, interrupted, failed, idle, closing, closed — is an
    /// outcome the mount can act on and move past. Waiting out a retry countdown is deliberately not
    /// part of it: <c>ReconnectPolicy</c> owns that, and it outlasts any mount.
    /// </summary>
    private static bool HasSettled(SessionState state) =>
        state is not (SessionState.Connecting or SessionState.Reconnecting);

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

        if (!VpnIsReady(connection))
        {
            return;
        }

        await OpenConnectionAsync(connection, start: true);
    }

    /// <summary>
    /// Checks the VPN profile a connection names, and offers to raise it when it is down.
    /// </summary>
    /// <returns>True when the session may go ahead: the connection needs no VPN, or the one it needs
    /// is up. False when the tunnel is down — whether or not the user chose to raise it, because
    /// dialling is asynchronous and the session has to be started again once it is really up.</returns>
    /// <remarks>
    /// <para>
    /// Only on this path — a connection the user asked for. Mounting a workspace deliberately does
    /// not check: it opens its sessions in series, and stopping that series on a question would turn
    /// one dialog into six. A workspace whose sessions are behind a tunnel fails the ordinary way,
    /// per session, which is the behaviour its own failure isolation already describes.
    /// </para>
    /// <para>
    /// A failure to enumerate is not treated as "the tunnel is down": that would offer to raise a
    /// VPN that may already be up. It is logged and the session proceeds, so a broken check can
    /// never be worse than no check at all.
    /// </para>
    /// </remarks>
    private bool VpnIsReady(Connection connection)
    {
        if (string.IsNullOrWhiteSpace(connection.VpnProfile))
        {
            return true;
        }

        VpnState state;
        try
        {
            state = VpnRequirement.Check(connection.VpnProfile, WindowsVpn.ConnectedProfiles());
        }
        catch (Exception ex)
        {
            ProbeLog.Write("vpn", $"Could not read the VPN state: {ex.GetType().Name}: {ex.Message}; connecting anyway");
            return true;
        }

        if (state != VpnState.NotConnected)
        {
            return true;
        }

        var profile = connection.VpnProfile.Trim();
        var answer = System.Windows.MessageBox.Show(this,
            Text.Of(Strings.Shell_VpnDownMessage, connection.Name, profile),
            Text.Of(Strings.Shell_VpnDownTitle, profile),
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning,
                Text.Of(Strings.Shell_VpnDownTitle, profile),
                Text.Of(Strings.Shell_VpnDownMessage, connection.Name, profile));
            return false;
        }

        // rasdial's own words when it refuses, rather than a message of ours guessing at the cause:
        // 691 is a bad credential, 789 an IPsec negotiation that failed, 809 a NAT in the way, and
        // none of that is something RemoteDeck could paraphrase usefully.
        if (WindowsVpn.Dial(profile) is { } complaint)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error,
                Text.Of(Strings.Shell_VpnDialFailedTitle, profile), complaint);
            return false;
        }

        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
            Text.Of(Strings.Shell_VpnDialingTitle, profile), Strings.Shell_VpnDialingMessage);
        return false;
    }

    /// <summary>
    /// Opens a tab for <paramref name="connection"/> and, when <paramref name="start"/>, starts the
    /// session. Awaitable, which <see cref="OnConnectRequested"/> cannot be: mounting a workspace
    /// connects in series, and a series needs somewhere to wait.
    /// </summary>
    /// <param name="start">False for a workspace whose <c>AutoConnect</c> is off: the tab exists,
    /// the session waits to be selected.</param>
    private async Task OpenConnectionAsync(Connection connection, bool start)
    {
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
            if (start)
            {
                await session.StartAsync();
            }
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

        // The user name and domain come from the credential, and with none attached both stay empty
        // on purpose. The one exception is a web-account connection: it carries its account hint
        // (the UPN mstsc keeps as UsernameHint for the server), which the control needs to find the
        // Entra account without prompting. No domain goes with it, and never a password.
        var settings = new RdpConnectionSettings(
            Host: connection.Host,
            Port: connection.Port,
            UserName: connection.UseWebAccount ? connection.WebAccountUpn ?? "" : credential?.UserName ?? "",
            Domain: connection.UseWebAccount ? null : credential?.Domain,
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

    /// <summary><em>Reconnect</em> from the toolbar, which only ever means the docked session.</summary>
    private void ReconnectActiveTab() => ReconnectTab(_sessions.Active);

    /// <summary><em>Reconnect</em> one session — the docked one from the toolbar, the window's own
    /// from a palette opened there. <c>async void</c> is the only shape an awaiting handler can
    /// take, hence the fully guarded body.</summary>
    private async void ReconnectTab(SessionTabViewModel? tab)
    {
        if (tab is null)
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

    /// <summary>What the pane's state pill must say about one saved connection: the state of the
    /// session opened from it, or nothing at all when no session was ever opened. <c>Find</c> already
    /// skips the tabs being closed, so a row goes quiet the moment its session starts leaving.</summary>
    private ConnectionStatus StatusOf(long connectionId)
        => _sessions.Find(connectionId) is { } tab ? StatusTag.For(tab.State) : ConnectionStatus.None;

    /// <summary>A tab was opened, closed or activated.</summary>
    private void OnSessionsChanged()
    {
        _list?.RefreshStatuses();
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
        // Before the early return: a background tab losing its connection still has to change the
        // pill on its row, even though it changes nothing in the session bar.
        _list?.RefreshStatuses();

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

    /// <summary>Reports the active session's state in the one place RemoteDeck reports anything.
    /// Shared with the detached windows (§6.4): a session says the same thing wherever it is
    /// shown.</summary>
    private void UpdateSessionInfoBar(SessionTabViewModel tab) =>
        SessionStatusPresenter.Report(StatusBar, tab);

    // ---------------------------------------------------------------- editor and delete

    /// <summary><c>null</c> means "new connection"; both cases go through the same modal editor.</summary>
    /// <summary>
    /// <em>Favorite</em> from the row's context menu. A one-column write, so it goes straight to the
    /// repository rather than through the editor — there is no form to reopen, and the pane re-sorts
    /// itself on the reload because favorites lead its ordering.
    /// </summary>
    /// <remarks>
    /// Guarded like every other repository write in this window: an unhandled <c>SqliteException</c>
    /// on the UI thread takes the application down, and with it every live session, without the §6.5
    /// close protocol.
    /// </remarks>
    private void OnFavoriteToggleRequested(Connection connection, bool isFavorite)
    {
        if (connection is null)
        {
            return;
        }

        if (_connections is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DatabaseUnavailableTitle,
                Strings.Shell_DatabaseNoEditMessage);
            return;
        }

        try
        {
            _connections.SetFavorite(connection.Id, isFavorite);
            _list?.Reload();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("connections", $"Favorite toggle failed: {ex.GetType().Name}: {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Shell_FavoriteFailedTitle, ex.Message);
        }
    }

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

    /// <summary>
    /// Photographs the tab strip for the next start-up. Written on every clean close and only
    /// there — deliberately not from <see cref="SaveSettings"/>, which the splitter also calls: a
    /// close by crash leaves the previous snapshot on disk, which is the useful behaviour
    /// (spec espaces §3.2).
    /// </summary>
    private void CaptureLastSession()
    {
        var entries = new List<LastSessionEntry>();
        for (int i = 0; i < _sessions.Tabs.Count; i++)
        {
            var tab = _sessions.Tabs[i];
            var window = _sessions.DetachedWindowOf(tab);
            entries.Add(new LastSessionEntry
            {
                ConnectionId = tab.Session.Connection.Id,
                Ordinal = i,
                Detached = window is not null,
                Placement = window is null ? null : PlacementOf(window),
            });
        }

        _settings.LastSession = entries;
    }

    /// <summary>
    /// Reopens the last session, if the user asked for it. Goes through the same
    /// <see cref="WorkspacePlan"/> as the named workspaces: it is the same decision on another
    /// source, and it has no business being written twice.
    /// </summary>
    private async Task RestoreLastSessionIfAsked()
    {
        if (!_settings.RestoreLastSession || _connections is null)
        {
            return;
        }

        // An empty snapshot is not an early return any more: the setting being on is worth saying
        // even when it had nothing to reopen, because that is precisely the case where a user who
        // forgot they enabled it would otherwise see no sign of it at all.
        if (_settings.LastSession.Count == 0)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                Strings.Shell_RestoreOnTitle, Strings.Shell_RestoredNothing);
            return;
        }

        // An ephemeral workspace, never written to the database: it is the adapter between the
        // windowing state held in settings.json and the mounting decision.
        var asWorkspace = new Workspace
        {
            Name = string.Empty,
            AutoConnect = true,
            Items = [.. _settings.LastSession.Select(e => new WorkspaceItem
            {
                ConnectionId = e.ConnectionId,
                Ordinal = e.Ordinal,
                Detached = e.Detached,
                Placement = e.Placement,
            })],
        };

        // OpenConnectionAsync writes LastConnectionId for every connection it opens, so the mount
        // would leave it on the last item of the plan instead of on the row the user had selected.
        // BuildPane has already re-selected that row from this very value, so it is simply put back:
        // a resume is not a selection, and the next SaveSettings falls back to it when the pane has
        // none of its own.
        long? selected = _settings.LastConnectionId;

        // Counted rather than taken from the plan: the plan drops connections deleted since, and an
        // action can still be refused, so only the strip knows how many sessions really came back.
        int before = _sessions.Tabs.Count;
        try
        {
            // announceEmpty: false — at start-up a resume that yields nothing (connections deleted
            // since) is the normal case, not an incident worth its own warning. It is reported
            // below instead, as part of saying the setting is on.
            await MountWorkspaceAsync(asWorkspace, announceEmpty: false);
        }
        finally
        {
            _settings.LastConnectionId = selected;
        }

        // Said out loud, every launch the setting is on. It is the only place the state of this
        // setting is visible at all — the palette command that toggles it closes on Enter and shows
        // nothing, and RemoteDeck has no settings window. Reporting it here also answers the
        // question at the moment it has an effect, which no permanent indicator would do better.
        int reopened = _sessions.Tabs.Count - before;
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
            Strings.Shell_RestoreOnTitle,
            reopened > 0
                ? Text.Plural(reopened, Strings.Shell_RestoredOne, Strings.Shell_RestoredMany, reopened)
                : Strings.Shell_RestoredNothing);
    }

    /// <summary>Writes the layout to <c>%APPDATA%\RemoteDeck\settings.json</c>. Losing it only costs
    /// geometry, so a failure is logged and swallowed — never surfaced on the way out of the app.</summary>
    /// <param name="rememberDetached">Whether the detached windows still open are also written into
    /// the per-connection placement memory. True only where spec espaces §7 allows it — the
    /// application closing — and false everywhere else this method is called for a reason of its own.
    /// <see cref="RememberPlacement"/> is triggered by a caption drag ending, a reattach and the
    /// close, and by nothing else: a programmatic placement is not one of them, so folding the pane
    /// or toggling the resume right after mounting a workspace must not write that workspace's
    /// imposed rectangle over the fallback another workspace relies on.</param>
    private void SaveSettings(bool rememberDetached = true)
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

        // The second path geometry is written on: the application going down with detached windows
        // still open. OnClosing runs this on its first pass, before the close-all protocol takes
        // those windows away, so each is still standing where the user left it. A window that closed
        // on its own earlier already wrote its own entry and is no longer in this list.
        if (rememberDetached)
        {
            foreach (var tab in _sessions.Tabs)
            {
                if (_sessions.DetachedWindowOf(tab) is { } detached)
                {
                    RememberPlacement(detached);
                }
            }
        }

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
            // Before SaveSettings writes the file, and before the close-all pass below takes the
            // sessions away: the strip has to still be there to be photographed. This is the only
            // call site, which is what makes the snapshot a clean-close-only affair.
            CaptureLastSession();
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
            // No budgets from here: ClosePlan owns both — five seconds per session under a
            // thirty-second ceiling — and with detached windows the number of live sessions is no
            // longer bounded by what fits in a tab strip.
            await _sessions.CloseAllAsync();
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
