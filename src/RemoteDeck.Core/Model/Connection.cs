namespace RemoteDeck.Core.Model;

/// <summary>One saved RDP target. Mirrors the Connection table (spec §4) one-to-one.</summary>
public sealed class Connection
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 3389;
    public string GroupName { get; set; } = "";
    public long? CredentialId { get; set; }
    public bool IsFavorite { get; set; }
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Dynamic;
    public int? FixedWidth { get; set; }
    public int? FixedHeight { get; set; }
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool RedirectPrinters { get; set; }
    public bool RedirectAudio { get; set; }
    public bool AdminSession { get; set; }
    public bool UseWebAccount { get; set; }
    /// <summary>
    /// The Entra account (UPN) a <see cref="UseWebAccount"/> connection signs in with, or <c>null</c>
    /// to let the control ask. An account hint, not a credential: it is what mstsc keeps as
    /// <c>UsernameHint</c> for the server, and what lets the broker find the account without a prompt.
    /// </summary>
    public string? WebAccountUpn { get; set; }
    public int? AuthenticationLevel { get; set; }
    public string? AcceptedCertThumbprint { get; set; }
    /// <summary>
    /// The Windows VPN profile this connection needs, or <c>null</c> when it needs none. Matched
    /// loosely against the profiles that are up — see <c>VpnRequirement</c>. RemoteDeck stores the
    /// name only: the credentials stay in the Windows profile, where the user put them.
    /// </summary>
    public string? VpnProfile { get; set; }

    public string Notes { get; set; } = "";
    public DateTime? LastConnectedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}
