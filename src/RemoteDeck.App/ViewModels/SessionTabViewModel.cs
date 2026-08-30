using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteDeck.App.Rdp;
using RemoteDeck.Core.Diagnostics;

namespace RemoteDeck.App.ViewModels;

/// <summary>
/// One tab: everything the strip and the session bar need to draw a single
/// <see cref="RdpSession"/>, and nothing else.
///
/// The view-model owns no UI and performs no action of its own: it observes
/// <see cref="RdpSession.Changed"/> and republishes it as bindable properties, and
/// <see cref="CloseCommand"/> merely raises <see cref="CloseRequested"/> so that
/// <see cref="SessionsViewModel"/> — the only thing that knows about the visual tree — runs the
/// close protocol.
/// </summary>
/// <remarks>
/// Internal because it exposes <see cref="Session"/>, and <see cref="RdpSession"/> is internal
/// (it owns a <c>WindowsFormsHost</c> the shell has to place). WPF binds to the public properties
/// of an internal type without complaint — verified on .NET 10 before this class was written — so
/// the accessibility costs the XAML nothing.
/// </remarks>
internal sealed partial class SessionTabViewModel : ObservableObject, IDisposable
{
    /// <summary>Status-dot resource keys. Declared here rather than in the XAML so that the
    /// mapping state → colour lives in one place; <c>SessionTabStrip</c> turns the key into a
    /// <c>DynamicResource</c> through a <c>DataTrigger</c>, which keeps the dot theme-aware.</summary>
    public const string ConnectedBrushKey = "SystemFillColorSuccessBrush";
    public const string RetryingBrushKey = "SystemFillColorCautionBrush";
    public const string FailedBrushKey = "SystemFillColorCriticalBrush";
    public const string NeutralBrushKey = "TextFillColorTertiaryBrush";

    private readonly RdpSession _session;
    private bool _disposed;

    public SessionTabViewModel(RdpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        Title = session.Connection.Name;
        Subtitle = $"{session.Connection.Host}:{session.Connection.Port}";
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this));

        _session.Changed += OnSessionChanged;
        Refresh();
    }

    /// <summary>The session behind the tab. Internal on purpose: the shell and
    /// <see cref="SessionsViewModel"/> drive it, the XAML never sees it.</summary>
    internal RdpSession Session => _session;

    /// <summary>Connection name — the tab caption.</summary>
    public string Title { get; }

    /// <summary>Host and port, shown in the session bar and as the tab's tooltip.</summary>
    public string Subtitle { get; }

    /// <summary>Mirror of <see cref="RdpSession.State"/>.</summary>
    [ObservableProperty] private SessionState _state;

    /// <summary>Which theme brush the 8 px status dot must use; one of the four keys above.</summary>
    [ObservableProperty] private string _statusBrushKey = NeutralBrushKey;

    /// <summary>« retry in 7 s » while a reconnection is counting down, empty otherwise.</summary>
    [ObservableProperty] private string _countdownText = "";

    /// <summary>One short line describing the state, for the session bar.</summary>
    [ObservableProperty] private string _stateText = "";

    /// <summary>True for the tab whose session is visible. Drives the strip's active styling.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Closes the tab. Raises <see cref="CloseRequested"/>; the actual protocol is
    /// <see cref="SessionsViewModel.CloseAsync"/>'s.</summary>
    public IRelayCommand CloseCommand { get; }

    /// <summary>Raised by <see cref="CloseCommand"/> and by the strip's middle-click.</summary>
    public event Action<SessionTabViewModel>? CloseRequested;

    /// <summary>Raised whenever the underlying session changed anything — state, attempt count or
    /// countdown. <see cref="SessionsViewModel"/> forwards it so the shell can refresh the bar.</summary>
    public event Action<SessionTabViewModel>? Changed;

    private void OnSessionChanged()
    {
        Refresh();
        Changed?.Invoke(this);
    }

    /// <summary>Re-reads everything from the session. Cheap and idempotent: the properties are
    /// <c>ObservableProperty</c>-generated, so assigning an unchanged value raises nothing.</summary>
    private void Refresh()
    {
        State = _session.State;
        StatusBrushKey = BrushKeyFor(_session.State);
        CountdownText = _session.NextRetryIn is { } remaining
            ? $"retry in {remaining.TotalSeconds:F0} s"
            : "";
        StateText = DescribeState();
    }

    private static string BrushKeyFor(SessionState state) => state switch
    {
        SessionState.Connected => ConnectedBrushKey,
        SessionState.Interrupted or SessionState.Reconnecting => RetryingBrushKey,
        SessionState.Failed => FailedBrushKey,
        _ => NeutralBrushKey,
    };

    private string DescribeState() => _session.State switch
    {
        SessionState.Idle => _session.LastDisconnect is { IsError: false } ended
            ? $"Disconnected — {ended.Title}"
            : "Not connected",
        SessionState.Connecting => "Connecting…",
        SessionState.Connected => "Connected",
        SessionState.Interrupted => CountdownText.Length == 0
            ? $"Interrupted (attempt {_session.Attempt})"
            : $"Interrupted — {CountdownText} (attempt {_session.Attempt})",
        SessionState.Reconnecting => $"Reconnecting (attempt {_session.Attempt})",
        SessionState.Failed => _session.LastDisconnect is { } failure ? $"Failed — {failure.Title}" : "Failed",
        SessionState.Closing => "Closing…",
        SessionState.Closed => "Closed",
        _ => _session.State.ToString(),
    };

    /// <summary>The disconnect family of the last drop, or <c>null</c> if the session never dropped.
    /// The shell picks the InfoBar severity from it.</summary>
    internal DisconnectCategory? LastCategory => _session.LastDisconnect?.Category;

    /// <summary>Stops observing the session. The session itself is disposed by its own
    /// <see cref="RdpSession.CloseAsync"/>, not here.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Changed -= OnSessionChanged;
    }
}
