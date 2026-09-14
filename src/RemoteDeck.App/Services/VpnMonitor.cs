using System.Net.NetworkInformation;
using System.Windows.Threading;
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.App.Services;

/// <summary>
/// Which VPN profiles are up, kept current for the connection pane without polling.
/// </summary>
/// <remarks>
/// <para>
/// Driven by <see cref="NetworkChange.NetworkAddressChanged"/>. Measured on the reference client on
/// 2026-09-14 with a read-only probe: cutting the tunnel and raising it again each raised the event
/// within the second, the new state was already readable when it arrived, and no change of state
/// went by without one.
/// </para>
/// <para>
/// The event arrives on a thread-pool thread, several times for one change and for plenty that is
/// not a tunnel, so it only restarts a short debounce on the UI thread. The reading happens once the
/// burst is over, and again a few seconds later: one measurement is not every VPN client, and an
/// interface that reports Up a moment after its address changed must not leave the pane stale.
/// Nothing is raised, and nothing is logged, unless the set of profiles actually changed.
/// </para>
/// </remarks>
internal sealed class VpnMonitor : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FollowUp = TimeSpan.FromSeconds(3);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _followUp;
    private bool _started;
    private bool _disposed;

    public VpnMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _followUp = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = FollowUp };
        _followUp.Tick += (_, _) =>
        {
            _followUp.Stop();
            Read();
        };
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Debounce };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Read();
            _followUp.Stop();
            _followUp.Start();
        };
    }

    /// <summary>
    /// The profiles up at the last reading, or <c>null</c> when that reading failed — which the pane
    /// shows as nothing, since a state that cannot be read is not a tunnel that is down.
    /// </summary>
    public IReadOnlySet<string>? Current { get; private set; }

    /// <summary>The set changed. Raised on the UI thread.</summary>
    public event Action<IReadOnlySet<string>?>? Changed;

    /// <summary>Takes a first reading and starts listening. Call once, from the UI thread.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Current = ReadProfiles();
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
    }

    private void OnAddressChanged(object? sender, EventArgs e)
    {
        // Thread pool. BeginInvoke, never Invoke: a blocked UI thread must not stall the network
        // stack's callback.
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _debounce.Stop();
            _debounce.Start();
        });
    }

    private void Read()
    {
        if (_disposed)
        {
            return;
        }

        var next = ReadProfiles();
        if (VpnRequirement.SameProfiles(Current, next))
        {
            return;
        }

        Current = next;
        ProbeLog.Write("vpn", next is null
            ? "VPN state could not be read; the pane shows none"
            : next.Count == 0
                ? "VPN state changed: no VPN interface is up"
                : $"VPN state changed: up: {string.Join(", ", next.Order(StringComparer.OrdinalIgnoreCase))}");
        Changed?.Invoke(next);
    }

    private static IReadOnlySet<string>? ReadProfiles()
    {
        try
        {
            return WindowsVpn.ConnectedProfiles(log: false);
        }
        catch (Exception ex)
        {
            ProbeLog.Write("vpn", $"Could not read the VPN state for the pane: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _debounce.Stop();
        _followUp.Stop();
    }
}
