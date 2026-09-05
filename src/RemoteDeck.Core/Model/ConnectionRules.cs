namespace RemoteDeck.Core.Model;

/// <summary>Validation rules for the connection editor. Pure; messages are UI-ready English.</summary>
public static class ConnectionRules
{
    public const int MaxNameLength = 80;

    /// <summary>
    /// The server-authentication level used when a connection says nothing: 2, "attempt
    /// authentication and prompt on failure", which is what <c>mstsc.exe</c> writes as
    /// <c>authentication level:i:2</c> in every <c>.rdp</c> it saves.
    /// </summary>
    /// <remarks>
    /// Chosen rather than inherited, because what is inherited was measured on 2026-09-05 and it is
    /// <strong>0</strong>: instantiated and asked before anything is set, the Remote Desktop control
    /// reports <c>AuthenticationLevel = 0</c>, "no authentication of the server". Left to that, a
    /// connection created with the editor's "Default" would accept a spoofed host in silence.
    /// Microsoft documents the three values and no default; this constant is the default.
    /// </remarks>
    public const int DefaultAuthenticationLevel = 2;

    /// <summary>
    /// The server-authentication level to hand the control: the one the connection stores, or
    /// <see cref="DefaultAuthenticationLevel"/> when it stores none. An explicit 0 stays 0 — a user
    /// who chose "no server authentication" in the editor chose it.
    /// </summary>
    public static int EffectiveAuthenticationLevel(int? stored) => stored ?? DefaultAuthenticationLevel;

    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const int MinFixedWidth = 640;
    private const int MinFixedHeight = 480;
    private const int MaxFixedSide = 8192;

    /// <param name="port">The port, or <c>null</c> when the box is empty — reported as missing, not out of range.</param>
    public static IReadOnlyList<string> Validate(string? name, string? host, int? port, DisplayMode mode, int? fixedWidth, int? fixedHeight)
    {
        var errors = new List<string>();

        var trimmedName = name?.Trim() ?? "";
        if (trimmedName.Length == 0) errors.Add("Name is required.");
        else if (trimmedName.Length > MaxNameLength) errors.Add($"Name must be at most {MaxNameLength} characters.");

        var trimmedHost = host?.Trim() ?? "";
        if (trimmedHost.Length == 0) errors.Add("Host is required.");
        else if (trimmedHost.Any(char.IsWhiteSpace)) errors.Add("Host must not contain spaces.");

        if (port is null) errors.Add("Port is required.");
        else if (port is < MinPort or > MaxPort) errors.Add($"Port must be between {MinPort} and {MaxPort}.");

        // Dynamic follows the window, so the fixed size is irrelevant — and must not block saving.
        if (mode != DisplayMode.Dynamic)
        {
            if (fixedWidth is not (>= MinFixedWidth and <= MaxFixedSide)) errors.Add($"Width must be between {MinFixedWidth} and {MaxFixedSide}.");
            if (fixedHeight is not (>= MinFixedHeight and <= MaxFixedSide)) errors.Add($"Height must be between {MinFixedHeight} and {MaxFixedSide}.");
        }

        return errors.AsReadOnly();
    }
}
