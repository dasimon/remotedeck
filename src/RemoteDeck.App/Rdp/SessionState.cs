namespace RemoteDeck.App.Rdp;

/// <summary>
/// Lifecycle of one <see cref="RdpSession"/>, i.e. of one tab. Every value is reachable from the
/// UI thread only, and every change raises <see cref="RdpSession.Changed"/>.
/// </summary>
/// <remarks>
/// The two "not connected, not failing" states are deliberately distinct:
/// <see cref="Idle"/> is where a session lands after a disconnect codes 0–3 (spec §6.4 — the
/// session ended on purpose, the tab stays open with a <em>Reconnect</em> button), whereas
/// <see cref="Failed"/> means something actually went wrong or the retry budget is exhausted.
/// </remarks>
public enum SessionState
{
    /// <summary>Never started, or disconnected normally (codes 0–3). Not an error.</summary>
    Idle,

    /// <summary>First connection attempt in flight.</summary>
    Connecting,

    /// <summary>The control reported <c>OnConnected</c>.</summary>
    Connected,

    /// <summary>Dropped on a retryable code; a reconnection is scheduled and counting down.</summary>
    Interrupted,

    /// <summary>A retry is in flight (the countdown reached zero, or the user asked for one).</summary>
    Reconnecting,

    /// <summary>Not retryable, retry budget exhausted, or the user cancelled the countdown.</summary>
    Failed,

    /// <summary>Close protocol running (spec §6.5): <c>RequestClose</c> issued, waiting.</summary>
    Closing,

    /// <summary>Closed and disposed. Terminal.</summary>
    Closed,
}
