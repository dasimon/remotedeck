namespace RemoteDeck.Core.Sessions;

/// <summary>
/// Decides whether a dropped session may be reconnected automatically, and how long to wait
/// before each attempt.
/// </summary>
/// <remarks>
/// Reconnection is deliberately narrow: only the <em>transient</em> network codes of
/// <c>IMsTscAxEvents::OnDisconnected</c> qualify, i.e. a socket or timer that failed while the
/// host itself is still expected to answer. Everything else is left to the user:
/// <list type="bullet">
/// <item><description>0–3 — the disconnection was intentional, reconnecting would fight the user or the server.</description></item>
/// <item><description>Name and address failures (260, 520, 776, 1288, 1540, 2052) — a wrong or unresolvable host will not become resolvable within 60 seconds.</description></item>
/// <item><description><c>SSL_ERR_*</c> — retrying rejected credentials risks locking the account out.</description></item>
/// <item><description>Security and licensing — a retry loop cannot repair a certificate or a licence.</description></item>
/// </list>
/// Codes are documented at
/// https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-ondisconnected
/// One retried code, 267, is <em>not</em> on that page — see the set below.
/// </remarks>
public static class ReconnectPolicy
{
    /// <summary>Wait before attempt 1, 2, 3, 4 and 5 respectively.</summary>
    public static IReadOnlyList<TimeSpan> Delays { get; } =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    /// <summary>Number of automatic attempts before the session is declared failed.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Disconnect codes worth retrying: lost socket or timeout, host presumed still there.</summary>
    private static readonly HashSet<int> TransientNetworkCodes =
    [
        264,  // disconnectReasonConnectionTimedOut
        516,  // disconnectReasonSocketConnectFailed
        772,  // disconnectReasonWinsockSendFailed
        1028, // disconnectReasonSocketRecvFailed
        1796, // disconnectReasonTimeoutOccurred
        2308, // disconnectReasonAtClientWinsockFDCLOSE

        // Observed on 2026-08-30 with control version 12 and EnableAutoReconnect=false; not listed
        // on the Microsoft page above; Windows describes it as a lost connection due to network
        // problems. A real network cut reports 267, so leaving it out would send the session
        // straight to Failed without a single retry.
        267,
    ];

    /// <summary>Returns true when <paramref name="reason"/> is a transient network failure.</summary>
    public static bool ShouldReconnect(int reason) => TransientNetworkCodes.Contains(reason);

    /// <summary>
    /// Returns the wait preceding the given 1-based attempt, or <c>null</c> once
    /// <see cref="MaxAttempts"/> has been exhausted (or for a non-positive attempt).
    /// </summary>
    public static TimeSpan? DelayFor(int attempt) =>
        attempt >= 1 && attempt <= Delays.Count ? Delays[attempt - 1] : null;
}
