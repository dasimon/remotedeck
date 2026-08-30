using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteDeck.App.Rdp;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.ViewModels;

/// <summary>
/// The open tabs: which sessions exist, which one is on screen, and the close protocol that takes
/// one — or all of them — down.
///
/// It is the only place that knows both halves of a tab: the <see cref="RdpSession"/> and the
/// <c>WindowsFormsHost</c> it has to be shown through. Adding and removing that host from the
/// shell's container is delegated to the two callbacks handed to the constructor, so the
/// view-model never names a XAML element.
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

    /// <summary>Tabs whose close is already running. A second Ctrl+W — or a click on the cross
    /// while the protocol waits — must join nothing and start nothing.</summary>
    private readonly HashSet<SessionTabViewModel> _closing = [];

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

    /// <summary>The tab serving <paramref name="connectionId"/>, or <c>null</c>. A connection has at
    /// most one tab: connecting to it again activates the existing one.</summary>
    public SessionTabViewModel? Find(long connectionId) =>
        Tabs.FirstOrDefault(t => t.Session.Connection.Id == connectionId);

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

    /// <summary>Brings <paramref name="tab"/> to the front. Unknown or null tabs are ignored.</summary>
    public void Activate(SessionTabViewModel? tab)
    {
        if (tab is not null && !Tabs.Contains(tab))
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

        // Modulo of a negative is negative in C#; the extra Count keeps -1 at the far right.
        Active = Tabs[((index + delta) % Tabs.Count + Tabs.Count) % Tabs.Count];
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

    /// <summary>Closes one tab with the default per-tab budget.</summary>
    public Task CloseAsync(SessionTabViewModel? tab) => CloseAsync(tab, DefaultCloseTimeout);

    /// <summary>
    /// Runs the §6.5 close protocol on one tab, then removes it: the host leaves the container, the
    /// tab leaves the strip, and the neighbour on its right — or, for the last tab, on its left —
    /// takes over. Never throws.
    /// </summary>
    public async Task CloseAsync(SessionTabViewModel? tab, TimeSpan timeout)
    {
        if (tab is null || !Tabs.Contains(tab) || !_closing.Add(tab))
        {
            return;
        }

        int index = Tabs.IndexOf(tab);
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

        _closing.Remove(tab);
        tab.CloseRequested -= OnTabCloseRequested;
        tab.Changed -= OnTabChanged;
        Tabs.Remove(tab);
        _detach(tab.Session);
        tab.Dispose();
        ProbeLog.Write("tabs", $"'{tab.Title}' closed ({Tabs.Count} tab(s) left)");

        if (ReferenceEquals(Active, tab))
        {
            Active = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
        else
        {
            // Active did not move, but the strip and the empty-state did.
            ActiveChanged?.Invoke();
        }
    }

    /// <summary>
    /// Closes every tab, one after another (§6.5 is a per-control protocol: two controls closing at
    /// once would interleave their <c>RequestClose</c> waits). Each tab gets
    /// <paramref name="perTab"/>, and the whole pass is capped at <paramref name="overall"/> — once
    /// that is spent the remaining tabs are torn down without waiting, so a hung control can never
    /// trap the user in a window that refuses to close.
    /// </summary>
    public async Task CloseAllAsync(TimeSpan perTab, TimeSpan overall)
    {
        var pending = Tabs.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        ProbeLog.Write("close", $"Closing {pending.Length} session(s), {perTab.TotalSeconds:F0}s each, {overall.TotalSeconds:F0}s overall");
        var clock = Stopwatch.StartNew();

        foreach (var tab in pending)
        {
            var remaining = overall - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                ProbeLog.Write("close", $"Overall budget spent; '{tab.Title}' is closed without waiting");
                await CloseAsync(tab, TimeSpan.Zero).ConfigureAwait(true);
                continue;
            }

            await CloseAsync(tab, remaining < perTab ? remaining : perTab).ConfigureAwait(true);
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
                _detach(tab.Session);
            }
            catch (Exception ex)
            {
                ProbeLog.Write("close", $"'{tab.Title}': dispose failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }

            tab.CloseRequested -= OnTabCloseRequested;
            tab.Changed -= OnTabChanged;
            tab.Dispose();
        }

        Tabs.Clear();
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
            tab.Session.Host.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        }

        if (value is not null)
        {
            ProbeLog.Write("tabs", $"'{value.Title}' activated");
        }

        ActiveChanged?.Invoke();
    }

    // Fire and forget: CloseAsync never throws, and a command handler cannot await.
    private void OnTabCloseRequested(SessionTabViewModel tab) => _ = CloseAsync(tab);

    private void OnTabChanged(SessionTabViewModel tab) => TabChanged?.Invoke(tab);
}
