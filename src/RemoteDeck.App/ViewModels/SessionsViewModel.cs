using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteDeck.App.Rdp;
using RemoteDeck.App.Services;
using RemoteDeck.App.Views;
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.App.ViewModels;

/// <summary>
/// The open tabs: which sessions exist, which one is on screen, and the close protocol that takes
/// one — or all of them — down.
///
/// It is the only place that knows both halves of a tab: the <see cref="RdpSession"/> and the
/// <c>WindowsFormsHost</c> it has to be shown through. Adding and removing that host from the
/// shell's container is delegated to the two callbacks handed to the constructor, so the
/// view-model never names a XAML element.
///
/// It is also the only thing allowed to move a host between containers — that is what
/// <see cref="Detach"/> and <see cref="Reattach"/> are — because a host that is in two trees, or in
/// none, is a live session nobody can see.
/// </summary>
/// <remarks>
/// Everything below runs on the UI thread — the sessions raise their events there and the
/// visibility switching is a WPF property assignment.
/// <para>
/// Activation hides the other sessions with <see cref="Visibility.Hidden"/>, never
/// <see cref="Visibility.Collapsed"/>: a collapsed <c>WindowsFormsHost</c> measures to zero, which
/// would make every background session negotiate a 0×0 desktop the moment it reconnects — and,
/// in dynamic display mode, on the very next debounced resize.
/// </para>
/// </remarks>
internal sealed partial class SessionsViewModel : ObservableObject
{
    /// <summary>Per-tab budget for the §6.5 close protocol when the user closes one tab.</summary>
    internal static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(5);

    private readonly Action<RdpSession> _attach;
    private readonly Action<RdpSession> _detach;

    /// <summary>
    /// The close in flight for each tab. A second Ctrl+W — or a click on the cross while the
    /// protocol waits — joins that task instead of starting a second <c>RequestClose</c>, and so
    /// does <see cref="CloseAllAsync"/>: a window closing over a tab the user has just closed must
    /// wait for the §6.5 protocol to finish, never dispose the control out from under it.
    /// </summary>
    private readonly Dictionary<SessionTabViewModel, Task> _closing = [];

    /// <summary>
    /// The window showing each detached tab. The tab itself never leaves <see cref="Tabs"/>: the
    /// strip hides it, everything that counts or closes sessions still sees it, and this map is the
    /// only record of where its host currently lives.
    /// </summary>
    private readonly Dictionary<SessionTabViewModel, SessionWindow> _detached = [];

    /// <param name="attach">Puts a session's host in the shell's container. Called by
    /// <see cref="Open"/>, before the shell starts the session.</param>
    /// <param name="detach">Removes it again. Called once the session is closed and disposed —
    /// <see cref="RdpSession.Dispose"/> deliberately leaves the host in the tree.</param>
    public SessionsViewModel(Action<RdpSession> attach, Action<RdpSession> detach)
    {
        ArgumentNullException.ThrowIfNull(attach);
        ArgumentNullException.ThrowIfNull(detach);

        _attach = attach;
        _detach = detach;
    }

    /// <summary>The open tabs, left to right. Reordered in place by <see cref="Move"/>.</summary>
    public ObservableCollection<SessionTabViewModel> Tabs { get; } = [];

    /// <summary>The tab on screen, or <c>null</c> when none is open.</summary>
    [ObservableProperty] private SessionTabViewModel? _active;

    /// <summary>Raised after <see cref="Active"/> changed, after a tab was opened and after one was
    /// closed — i.e. whenever the shell's session bar and empty-state may be stale.</summary>
    public event Action? ActiveChanged;

    /// <summary>Raised when any tab's session changed. The shell only acts on the active one.</summary>
    public event Action<SessionTabViewModel>? TabChanged;

    /// <summary>
    /// False while the window is shutting down: the cross and the middle-click are then refused,
    /// the way the shell already refuses Ctrl+W. A close started at that point would race the
    /// close-all pass over the same tab.
    /// </summary>
    [ObservableProperty] private bool _canCloseTabs = true;

    /// <summary>
    /// The tab serving <paramref name="connectionId"/>, or <c>null</c>. A connection has at most
    /// one tab: connecting to it again activates the existing one. A tab whose close is already
    /// running does not count — it is on its way out, and reusing it would hand the user a session
    /// about to disappear; the caller opens a fresh one instead.
    /// </summary>
    public SessionTabViewModel? Find(long connectionId) =>
        Tabs.FirstOrDefault(t => t.Session.Connection.Id == connectionId && !_closing.ContainsKey(t));

    /// <summary>
    /// Adds <paramref name="session"/> as a new, activated tab and puts its host in the container.
    /// The caller starts the session afterwards — <see cref="RdpSession.StartAsync"/> requires the
    /// host to already be in the visual tree.
    /// </summary>
    public SessionTabViewModel Open(RdpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var tab = new SessionTabViewModel(session);
        tab.CloseRequested += OnTabCloseRequested;
        tab.Changed += OnTabChanged;

        // Hidden until activation decides otherwise, so a host that is added while another tab is
        // active never flashes over it for one frame.
        session.Host.Visibility = Visibility.Hidden;
        _attach(session);
        Tabs.Add(tab);
        ProbeLog.Write("tabs", $"'{tab.Title}' opened ({Tabs.Count} tab(s))");

        Activate(tab);
        return tab;
    }

    /// <summary>
    /// Brings <paramref name="tab"/> to the front. Unknown and null tabs are ignored, and so are
    /// detached ones: <see cref="Active"/> names the session the shell's container shows, and a
    /// detached session is not in that container — activating it would blank the docked area while
    /// changing nothing on screen. The caller brings its window forward instead.
    /// </summary>
    public void Activate(SessionTabViewModel? tab)
    {
        if (tab is not null && (!Tabs.Contains(tab) || tab.IsDetached))
        {
            return;
        }

        Active = tab;
    }

    /// <summary>Next tab, wrapping around. No-op with fewer than two tabs.</summary>
    public void Next() => Step(1);

    /// <summary>Previous tab, wrapping around. No-op with fewer than two tabs.</summary>
    public void Previous() => Step(-1);

    private void Step(int delta)
    {
        if (Tabs.Count < 2 || Active is null)
        {
            return;
        }

        int index = Tabs.IndexOf(Active);
        if (index < 0)
        {
            return;
        }

        // Detached tabs are stepped over: they are not in the strip the user is cycling through.
        for (int step = 1; step < Tabs.Count; step++)
        {
            // Modulo of a negative is negative in C#; the extra Count keeps -1 at the far right.
            var candidate = Tabs[((index + delta * step) % Tabs.Count + Tabs.Count) % Tabs.Count];
            if (!candidate.IsDetached)
            {
                Active = candidate;
                return;
            }
        }
    }

    /// <summary>
    /// What the shell's container should show once the tab at <paramref name="index"/> has left it —
    /// closed or detached: the neighbour on its right, wrapping around, skipping detached tabs since
    /// those are on screen already in windows of their own. <c>null</c> when nothing is left to dock.
    /// </summary>
    private SessionTabViewModel? DockedNear(int index)
    {
        if (Tabs.Count == 0)
        {
            return null;
        }

        int start = Math.Clamp(index, 0, Tabs.Count - 1);
        for (int step = 0; step < Tabs.Count; step++)
        {
            var candidate = Tabs[(start + step) % Tabs.Count];
            if (!candidate.IsDetached)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Reorders the strip. Out-of-range or identical indices are ignored, so the drag
    /// handler can call it on every mouse move without checking first.</summary>
    public void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Tabs.Count || to >= Tabs.Count)
        {
            return;
        }

        Tabs.Move(from, to);
    }

    // ---------------------------------------------------------------- detach / reattach

    /// <summary>The window showing <paramref name="tab"/>, or <c>null</c> when it is docked.</summary>
    public SessionWindow? DetachedWindowOf(SessionTabViewModel? tab) =>
        tab is null ? null : _detached.GetValueOrDefault(tab);

    /// <summary>
    /// Moves a session's host out of the shell's container and into <paramref name="window"/>, which
    /// the caller has already created and shown. The tab stays in <see cref="Tabs"/>, marked
    /// <see cref="SessionTabViewModel.IsDetached"/>, and the docked area falls back to a neighbour.
    ///
    /// Moving the host — rather than re-creating the control in the new window — is what keeps the
    /// HWND, its Win32 parent and therefore the remote session alive (design §2).
    /// </summary>
    /// <returns>False when the tab is unknown, already detached, or when the move failed; the
    /// session then stays docked, exactly where the caller found it.</returns>
    public bool Detach(SessionTabViewModel tab, SessionWindow window)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(window);

        if (!Tabs.Contains(tab) || tab.IsDetached || _closing.ContainsKey(tab))
        {
            return false;
        }

        var session = tab.Session;
        try
        {
            // Out of the docked container first: a host that still has a parent cannot be given
            // another one, and WPF answers that with an InvalidOperationException.
            _detach(session);
            window.HostArea.Child = session.Host;

            // The window owns the session's size and DPI from now on.
            session.AttachedTo(window.HostArea);
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"'{tab.Title}': detach failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            Redock(session, window);
            return false;
        }

        // A detached host is always visible: it is the whole content of its window, and the
        // activation pass below must not hide it on its way to the neighbour.
        session.Host.Visibility = Visibility.Visible;
        _detached[tab] = window;
        tab.IsDetached = true;
        ProbeLog.Write("session", $"'{tab.Title}' detached ({_detached.Count} window(s))");

        if (ReferenceEquals(Active, tab))
        {
            Active = DockedNear(Tabs.IndexOf(tab));
        }
        else
        {
            // Active did not move, but the strip and the empty-state did.
            ActiveChanged?.Invoke();
        }

        return true;
    }

    /// <summary>
    /// The reverse: the host goes back into the shell's container, the tab becomes an ordinary tab
    /// again and is activated, and the now-empty window is closed.
    /// </summary>
    /// <returns>False when the tab is not detached, or when the move failed — and a failed move
    /// leaves the session in its window, still visible and still usable, rather than alive with
    /// nothing to show it.</returns>
    public bool Reattach(SessionTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_detached.TryGetValue(tab, out var window))
        {
            return false;
        }

        var session = tab.Session;
        try
        {
            window.HostArea.Child = null;
            _attach(session);
            session.AttachedTo(ParentOf(session));
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"'{tab.Title}': reattach failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            Undock(session, window);
            return false;
        }

        _detached.Remove(tab);
        tab.IsDetached = false;
        ProbeLog.Write("session", $"'{tab.Title}' reattached ({_detached.Count} window(s) left)");

        // Before the window goes: it now holds nothing, and the tab is the one the user just
        // dragged back, so it takes the docked area.
        Close(window, tab);
        Activate(tab);
        if (!ReferenceEquals(Active, tab))
        {
            // Activation refused — the strip changed anyway.
            ActiveChanged?.Invoke();
        }

        return true;
    }

    /// <summary>Puts a host back in the shell's container after a failed <see cref="Detach"/>.
    /// Never throws: the caller is already handling one failure.</summary>
    private void Redock(RdpSession session, SessionWindow window)
    {
        try
        {
            if (ReferenceEquals(window.HostArea.Child, session.Host))
            {
                window.HostArea.Child = null;
            }

            if (session.Host.Parent is null)
            {
                _attach(session);
                session.AttachedTo(ParentOf(session));
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"'{session.Connection.Name}': could not be docked again: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    /// <summary>Puts a host back in its window after a failed <see cref="Reattach"/>. Never
    /// throws.</summary>
    private void Undock(RdpSession session, SessionWindow window)
    {
        try
        {
            if (session.Host.Parent is not null)
            {
                _detach(session);
            }

            window.HostArea.Child = session.Host;
            session.Host.Visibility = Visibility.Visible;
            session.AttachedTo(window.HostArea);
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"'{session.Connection.Name}': could not be put back in its window: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    /// <summary>
    /// The element a host now sits in, read back from the host itself: the shell hands this
    /// view-model two callbacks, never the container they write to, and
    /// <see cref="RdpSession.AttachedTo"/> only needs the new parent to re-arm its size
    /// subscription. The host is its own fallback — measuring it is what the session does anyway.
    /// </summary>
    private static FrameworkElement ParentOf(RdpSession session) =>
        session.Host.Parent as FrameworkElement ?? session.Host;

    /// <summary>
    /// Closes a detached window whose session is gone or has moved back to the shell. The window
    /// refuses every close until <see cref="SessionWindow.AllowClose"/> has been called — that is
    /// how it makes sure the §6.5 protocol runs — so both happen here, and neither may throw: this
    /// runs on shutdown paths that have to finish.
    /// </summary>
    private static void Close(SessionWindow window, SessionTabViewModel tab)
    {
        try
        {
            window.HostArea.Child = null;
            window.AllowClose();
            window.Close();
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"'{tab.Title}': its window would not close: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- closing

    /// <summary>Closes one tab with the default per-tab budget.</summary>
    public Task CloseAsync(SessionTabViewModel? tab) => CloseAsync(tab, DefaultCloseTimeout);

    /// <summary>
    /// Closes one tab, or hands back the close already running on it. The returned task completes
    /// once the tab is really gone, so a caller that must not proceed until the control has been
    /// released — the window's close-all pass — can await it. Never throws.
    /// </summary>
    public Task CloseAsync(SessionTabViewModel? tab, TimeSpan timeout)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return Task.CompletedTask;
        }

        if (_closing.TryGetValue(tab, out var running))
        {
            return running;
        }

        // The entry has to exist before the first await inside CloseCoreAsync, and an async method
        // cannot register its own task from within itself; the completion source bridges the two.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closing[tab] = gate.Task;
        _ = RunCloseAsync(tab, timeout, gate);
        return gate.Task;
    }

    private async Task RunCloseAsync(SessionTabViewModel tab, TimeSpan timeout, TaskCompletionSource gate)
    {
        try
        {
            await CloseCoreAsync(tab, timeout).ConfigureAwait(true);
        }
        finally
        {
            // Removed before the task completes, so a CloseAsync resuming on that completion starts
            // a fresh close instead of being handed one that is already over.
            _closing.Remove(tab);
            gate.TrySetResult();
        }
    }

    /// <summary>
    /// Runs the §6.5 close protocol on one tab, then removes it: the host leaves the container, the
    /// tab leaves the strip, and the neighbour on its right — or, for the last tab, on its left —
    /// takes over. Never throws.
    /// </summary>
    private async Task CloseCoreAsync(SessionTabViewModel tab, TimeSpan timeout)
    {
        try
        {
            await tab.Session.CloseAsync(timeout).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A control that will not close must not keep its tab — the session is disposed either
            // way, and leaving the tab behind would leave a dead host in the container.
            ProbeLog.Write("tabs", $"'{tab.Title}': close failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }

        // Read after the await, never before: the protocol takes seconds, during which the strip
        // may have been reordered or another tab closed, and a stale index would hand the user the
        // wrong neighbour.
        int index = Tabs.IndexOf(tab);
        tab.CloseRequested -= OnTabCloseRequested;
        tab.Changed -= OnTabChanged;
        Tabs.Remove(tab);

        // The host is wherever this tab last put it: the shell's container, or the window that was
        // showing it — which is now empty and goes with the session it was holding.
        if (_detached.Remove(tab, out var window))
        {
            tab.IsDetached = false;
            Close(window, tab);
        }
        else
        {
            _detach(tab.Session);
        }

        tab.Dispose();
        ProbeLog.Write("tabs", $"'{tab.Title}' closed ({Tabs.Count} tab(s) left)");

        if (ReferenceEquals(Active, tab))
        {
            Active = DockedNear(index);
        }
        else
        {
            // Active did not move, but the strip and the empty-state did.
            ActiveChanged?.Invoke();
        }
    }

    /// <summary>
    /// Closes every session, one after another (§6.5 is a per-control protocol: two controls closing
    /// at once would interleave their <c>RequestClose</c> waits) — detached ones included, each
    /// followed by the window that was showing it. The budget is <see cref="ClosePlan"/>'s: five
    /// seconds per session under a thirty-second ceiling, and once that is spent the rest is torn
    /// down without waiting, so a hung control can never trap the user in a window that refuses to
    /// close.
    /// </summary>
    public async Task CloseAllAsync()
    {
        var pending = Tabs.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        ProbeLog.Write("close", $"Closing {pending.Length} session(s), {ClosePlan.PerSessionSeconds}s each, {ClosePlan.OverallSeconds}s overall");
        var clock = Stopwatch.StartNew();

        for (int i = 0; i < pending.Length; i++)
        {
            var tab = pending[i];
            var budget = ClosePlan.For(pending.Length - i, clock.Elapsed);
            if (budget <= TimeSpan.Zero)
            {
                ProbeLog.Write("close", $"Overall budget spent; '{tab.Title}' is closed without waiting");
                await CloseAsync(tab, TimeSpan.Zero).ConfigureAwait(true);
                continue;
            }

            // A tab the user closed a moment ago — or the cross of a detached window — already has a
            // protocol in flight with a budget of its own; CloseAsync hands that task back rather
            // than starting a second RequestClose. Skipping it here is what used to let DisposeAll
            // tear the control down mid-protocol, i.e. exactly the zombie session §6.5 prevents.
            bool joined = _closing.ContainsKey(tab);
            var close = CloseAsync(tab, budget);
            if (!joined)
            {
                await close.ConfigureAwait(true);
                continue;
            }

            // Waiting on someone else's budget: bounded by what is left of ours.
            if (await Task.WhenAny(close, Task.Delay(budget)).ConfigureAwait(true) != close)
            {
                ProbeLog.Write("close", $"'{tab.Title}': the close already in flight outlived the overall budget");
            }
        }

        ProbeLog.Write("close", $"All sessions closed in {clock.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Last-resort teardown for the second closing pass: whatever survived <see cref="CloseAllAsync"/>
    /// is disposed without any protocol, and its host leaves the container.
    /// </summary>
    public void DisposeAll()
    {
        foreach (var tab in Tabs.ToArray())
        {
            try
            {
                tab.Session.Dispose();
                if (!_detached.ContainsKey(tab))
                {
                    _detach(tab.Session);
                }
            }
            catch (Exception ex)
            {
                ProbeLog.Write("close", $"'{tab.Title}': dispose failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            // A window still showing a session that has just been disposed has nothing left to show.
            if (_detached.Remove(tab, out var window))
            {
                tab.IsDetached = false;
                Close(window, tab);
            }

            tab.CloseRequested -= OnTabCloseRequested;
            tab.Changed -= OnTabChanged;
            tab.Dispose();
        }

        Tabs.Clear();
        _detached.Clear();
        _closing.Clear();
        Active = null;
    }

    /// <summary>Shows the newly active session and hides the others. Generated by
    /// <c>[ObservableProperty]</c> on <see cref="Active"/>.</summary>
    partial void OnActiveChanged(SessionTabViewModel? value)
    {
        foreach (var tab in Tabs)
        {
            bool active = ReferenceEquals(tab, value);
            tab.IsActive = active;

            // A detached session stays visible whatever the docked area is showing: it is the only
            // content of its own window, and hiding it there would blank a window the user is
            // looking at.
            tab.Session.Host.Visibility = active || tab.IsDetached ? Visibility.Visible : Visibility.Hidden;
        }

        if (value is not null)
        {
            ProbeLog.Write("tabs", $"'{value.Title}' activated");
        }

        ActiveChanged?.Invoke();
    }

    /// <summary>Fire and forget: <see cref="CloseAsync(SessionTabViewModel?)"/> never throws and a
    /// command handler cannot await. Refused while the window is shutting down.</summary>
    private void OnTabCloseRequested(SessionTabViewModel tab)
    {
        if (!CanCloseTabs)
        {
            return;
        }

        _ = CloseAsync(tab);
    }

    private void OnTabChanged(SessionTabViewModel tab) => TabChanged?.Invoke(tab);
}
