using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Import;

/// <summary>
/// A connection an importer proposes, before the user reviews it. Mirrors the columns of
/// <see cref="Connection"/> that an external source can legitimately fill: no identifier, no group, no
/// notes, and above all no credential — an imported password belongs to another security context and is
/// never read, stored or reported.
/// </summary>
public sealed record ImportCandidate
{
    /// <summary>Proposed connection name: the file name without its extension, or the host.</summary>
    public required string Name { get; init; }

    /// <summary>Host name or address, without the port.</summary>
    public required string Host { get; init; }

    /// <summary>TCP port; 3389 unless the source carried a usable one.</summary>
    public int Port { get; init; } = 3389;

    /// <summary>User name found in the source, or null.</summary>
    public string? UserName { get; init; }

    /// <summary>Domain found in the source, or null.</summary>
    public string? Domain { get; init; }

    /// <summary>How the remote resolution should follow the window.</summary>
    public DisplayMode DisplayMode { get; init; } = DisplayMode.Dynamic;

    /// <summary>Remote width when <see cref="DisplayMode"/> is not <see cref="DisplayMode.Dynamic"/>.</summary>
    public int? FixedWidth { get; init; }

    /// <summary>Remote height when <see cref="DisplayMode"/> is not <see cref="DisplayMode.Dynamic"/>.</summary>
    public int? FixedHeight { get; init; }

    /// <summary>Clipboard redirection; on by default, like a new connection.</summary>
    public bool RedirectClipboard { get; init; } = true;

    /// <summary>Drive redirection.</summary>
    public bool RedirectDrives { get; init; }

    /// <summary>Printer redirection.</summary>
    public bool RedirectPrinters { get; init; }

    /// <summary>Audio played on this machine.</summary>
    public bool RedirectAudio { get; init; }

    /// <summary>Entra ID (web account) authentication.</summary>
    public bool UseWebAccount { get; init; }

    /// <summary>
    /// The account hint a web-account connection is imported with: the source's user name, but only
    /// under <see cref="UseWebAccount"/>. Any other user name belongs to a credential, which import
    /// never creates, so it stays out of the connection.
    /// </summary>
    public string? WebAccountUpn => UseWebAccount ? UserName : null;

    /// <summary>Server authentication level 0, 1 or 2; null when the source left it unspecified.</summary>
    public int? AuthenticationLevel { get; init; }

    /// <summary>Where the candidate comes from: a file path, or <c>mstsc registry</c>.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// What the importer could not use, in English, for the preview window. Never contains a secret:
    /// no value of a credential-bearing entry is ever copied here.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
