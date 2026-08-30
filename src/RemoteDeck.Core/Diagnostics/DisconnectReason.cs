// Disconnect codes and their meanings are taken verbatim from the documented parameter list of
// IMsTscAxEvents::OnDisconnected (47 codes, verified 2026-08-30):
// https://learn.microsoft.com/windows/win32/termserv/imstscaxevents-ondisconnected
// No other code is invented here: anything absent from that page is reported as Unknown.

namespace RemoteDeck.Core.Diagnostics;

/// <summary>Coarse family of a disconnect code, used to pick the tone of the message shown.</summary>
public enum DisconnectCategory
{
    /// <summary>Codes 0–3: the session ended on purpose, nothing failed.</summary>
    NotAnError,

    /// <summary>Name resolution, socket or timeout failure.</summary>
    Network,

    /// <summary>Credential rejected (<c>SSL_ERR_*</c>): never retried automatically.</summary>
    Authentication,

    /// <summary>Encryption, certificate or security-data failure.</summary>
    Security,

    /// <summary>Licence negotiation failure or licensing time-out.</summary>
    Licensing,

    /// <summary>The client ran out of memory.</summary>
    Resources,

    /// <summary>Internal client error or internal timer error.</summary>
    Internal,

    /// <summary>Code absent from the documented table.</summary>
    Unknown,
}

/// <summary>What a disconnect code means, ready to be shown to the user.</summary>
/// <param name="Reason">The raw code received from <c>OnDisconnected</c>.</param>
/// <param name="Category">Family the code belongs to.</param>
/// <param name="Title">Short English wording of the cause.</param>
/// <param name="IsError">False for codes 0–3, which must never be presented as a failure.</param>
public sealed record DisconnectDescription(int Reason, DisconnectCategory Category, string Title, bool IsError);

/// <summary>Translates an <c>OnDisconnected</c> code into a category and a short English title.</summary>
public static class DisconnectReason
{
    private static readonly Dictionary<int, DisconnectDescription> Table = Build();

    /// <summary>
    /// Describes <paramref name="reason"/>. An undocumented code yields
    /// <see cref="DisconnectCategory.Unknown"/>, a title carrying the raw number, and
    /// <c>IsError = true</c>.
    /// </summary>
    public static DisconnectDescription Describe(int reason) =>
        Table.TryGetValue(reason, out var known)
            ? known
            : new DisconnectDescription(reason, DisconnectCategory.Unknown, $"Disconnected (code {reason})", true);

    private static Dictionary<int, DisconnectDescription> Build()
    {
        var table = new Dictionary<int, DisconnectDescription>();

        // Not errors — the session ended on purpose (§6.4: never shown as a failure).
        Add(DisconnectCategory.NotAnError, 0, "No information available");        // disconnectReasonNoInfo
        Add(DisconnectCategory.NotAnError, 1, "Disconnected locally");            // disconnectReasonLocalNotError
        Add(DisconnectCategory.NotAnError, 2, "Disconnected by the remote user"); // disconnectReasonRemoteByUser
        Add(DisconnectCategory.NotAnError, 3, "Disconnected by the server");      // disconnectReasonByServer

        // Network.
        Add(DisconnectCategory.Network, 260, "DNS name lookup failed");         // disconnectReasonDNSLookupFailed
        Add(DisconnectCategory.Network, 264, "Connection timed out");           // disconnectReasonConnectionTimedOut
        Add(DisconnectCategory.Network, 516, "Socket connect failed");          // disconnectReasonSocketConnectFailed
        Add(DisconnectCategory.Network, 520, "Host not found");                 // disconnectReasonHostNotFound
        Add(DisconnectCategory.Network, 772, "Socket send failed");             // disconnectReasonWinsockSendFailed
        Add(DisconnectCategory.Network, 776, "The IP address is not valid");    // disconnectReasonInvalidIPAddr
        Add(DisconnectCategory.Network, 1028, "Socket receive failed");         // disconnectReasonSocketRecvFailed
        Add(DisconnectCategory.Network, 1288, "DNS lookup failed");             // disconnectReasonDNSLookupFailed2
        Add(DisconnectCategory.Network, 1540, "Host name resolution failed");   // disconnectReasonGetHostByNameFailed
        Add(DisconnectCategory.Network, 1796, "Time-out occurred");             // disconnectReasonTimeoutOccurred
        Add(DisconnectCategory.Network, 2052, "Bad IP address specified");      // disconnectReasonInvalidIP
        Add(DisconnectCategory.Network, 2308, "Socket closed");                 // disconnectReasonAtClientWinsockFDCLOSE

        // Resources.
        Add(DisconnectCategory.Resources, 262, "Out of memory"); // disconnectReasonOutOfMemory
        Add(DisconnectCategory.Resources, 518, "Out of memory"); // disconnectReasonOutOfMemory2
        Add(DisconnectCategory.Resources, 774, "Out of memory"); // disconnectReasonOutOfMemory3

        // Internal.
        Add(DisconnectCategory.Internal, 1032, "Internal error");       // disconnectReasonInternalError
        Add(DisconnectCategory.Internal, 1544, "Internal timer error"); // disconnectReasonTimerError

        // Security.
        Add(DisconnectCategory.Security, 1030, "Security data is not valid");        // disconnectReasonInvalidSecurityData
        Add(DisconnectCategory.Security, 1286, "Encryption method is not valid");    // disconnectReasonInvalidEncryption
        Add(DisconnectCategory.Security, 1542, "Server security data is not valid"); // disconnectReasonInvalidServerSecurityInfo
        Add(DisconnectCategory.Security, 1798, "Server certificate is unreadable");  // disconnectReasonServerCertificateUnpackErr
        Add(DisconnectCategory.Security, 2310, "Internal security error");           // disconnectReasonInternalSecurityError
        Add(DisconnectCategory.Security, 2566, "Internal security error");           // disconnectReasonInternalSecurityError2
        Add(DisconnectCategory.Security, 2822, "Encryption error");                  // disconnectReasonEncryptionError
        Add(DisconnectCategory.Security, 3078, "Decryption error");                  // disconnectReasonDecryptionError
        Add(DisconnectCategory.Security, 3080, "Decompression error");               // disconnectReasonClientDecompressionError

        // Licensing.
        Add(DisconnectCategory.Licensing, 2056, "License negotiation failed"); // disconnectReasonLicensingFailed
        Add(DisconnectCategory.Licensing, 2312, "Licensing timed out");        // disconnectReasonLicensingTimeout

        // Authentication (SSL_ERR_*) — never retried automatically.
        Add(DisconnectCategory.Authentication, 2055, "Logon failed");                                  // SSL_ERR_LOGON_FAILURE
        Add(DisconnectCategory.Authentication, 2567, "No such user account");                          // SSL_ERR_NO_SUCH_USER
        Add(DisconnectCategory.Authentication, 2823, "The account is disabled");                       // SSL_ERR_ACCOUNT_DISABLED
        Add(DisconnectCategory.Authentication, 3079, "The account is restricted");                     // SSL_ERR_ACCOUNT_RESTRICTION
        Add(DisconnectCategory.Authentication, 3335, "The account is locked out");                     // SSL_ERR_ACCOUNT_LOCKED_OUT
        Add(DisconnectCategory.Authentication, 3591, "The account has expired");                       // SSL_ERR_ACCOUNT_EXPIRED
        Add(DisconnectCategory.Authentication, 3847, "The password has expired");                      // SSL_ERR_PASSWORD_EXPIRED
        Add(DisconnectCategory.Authentication, 4615, "The password must be changed");                  // SSL_ERR_PASSWORD_MUST_CHANGE
        Add(DisconnectCategory.Authentication, 5639, "Credential delegation refused by policy");       // SSL_ERR_DELEGATION_POLICY
        Add(DisconnectCategory.Authentication, 5895, "Credential delegation needs mutual authentication"); // SSL_ERR_POLICY_NTLM_ONLY
        Add(DisconnectCategory.Authentication, 6151, "No authenticating authority could be reached");  // SSL_ERR_NO_AUTHENTICATING_AUTHORITY
        Add(DisconnectCategory.Authentication, 6919, "The server certificate has expired");            // SSL_ERR_CERT_EXPIRED
        Add(DisconnectCategory.Authentication, 7175, "Incorrect smart card PIN");                      // SSL_ERR_SMARTCARD_WRONG_PIN
        Add(DisconnectCategory.Authentication, 8455, "Saved credentials refused, sign in again");      // SSL_ERR_FRESH_CRED_REQUIRED_BY_SERVER
        Add(DisconnectCategory.Authentication, 8711, "The smart card is blocked");                     // SSL_ERR_SMARTCARD_CARD_BLOCKED

        return table;

        void Add(DisconnectCategory category, int reason, string title) =>
            table.Add(reason, new DisconnectDescription(
                reason, category, title, category != DisconnectCategory.NotAnError));
    }
}
