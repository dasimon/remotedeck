using RemoteDeck.Core.Model;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Transfer;

/// <summary>
/// What a configuration file holds: connections, the credentials they use, and workspaces. Never a
/// secret — a credential travels as its label and user name, and the password stays in the DPAPI
/// vault of the machine and account that sealed it, which is the only place it can be read anyway.
/// </summary>
/// <remarks>
/// Window geometry, the last session and the other entries of <c>settings.json</c> are left out:
/// they describe one machine's screens, not the configuration a user carries to another one.
/// </remarks>
public sealed record ConfigurationDocument
{
    /// <summary>The value <see cref="Format"/> must carry; anything else is not ours.</summary>
    public const string FormatName = "remotedeck-configuration";

    /// <summary>The version this build writes and the highest it reads.</summary>
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;

    public int Version { get; init; } = CurrentVersion;

    public DateTime ExportedUtc { get; init; }

    public IReadOnlyList<ExportedCredential> Credentials { get; init; } = [];

    public IReadOnlyList<ExportedConnection> Connections { get; init; } = [];

    public IReadOnlyList<ExportedWorkspace> Workspaces { get; init; } = [];
}

/// <summary>A credential without its secret: what an import matches a local credential on.</summary>
public sealed record ExportedCredential(string Label, string UserName, string? Domain);

/// <summary>
/// A connection as it leaves one machine. <see cref="Key"/> only ties workspaces to connections
/// inside the file; it is not an identifier anywhere else. <see cref="Credential"/> is a label.
/// </summary>
public sealed record ExportedConnection
{
    public long Key { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 3389;
    public string GroupName { get; init; } = "";
    public string? Credential { get; init; }
    public bool IsFavorite { get; init; }
    public DisplayMode DisplayMode { get; init; } = DisplayMode.Dynamic;
    public int? FixedWidth { get; init; }
    public int? FixedHeight { get; init; }
    public bool RedirectClipboard { get; init; } = true;
    public bool RedirectDrives { get; init; }
    public bool RedirectPrinters { get; init; }
    public bool RedirectAudio { get; init; }
    public bool AdminSession { get; init; }
    public bool UseWebAccount { get; init; }
    public string? WebAccountUpn { get; init; }
    public int? AuthenticationLevel { get; init; }
    public string? VpnProfile { get; init; }
    public bool AutoRaiseVpn { get; init; }
    public string Notes { get; init; } = "";
}

public sealed record ExportedWorkspace(string Name, bool AutoConnect, IReadOnlyList<ExportedWorkspaceItem> Items);

/// <param name="Connection">The <see cref="ExportedConnection.Key"/> of the connection it opens.</param>
public sealed record ExportedWorkspaceItem(long Connection, int Ordinal, bool Detached, DetachedWindowPlacement? Placement);
