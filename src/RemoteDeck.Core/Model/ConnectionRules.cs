namespace RemoteDeck.Core.Model;

/// <summary>
/// What is wrong with a connection, as a code. The words are the application's: until 2026-09-06
/// these rules returned English sentences, and a French interface showed them as they were.
/// </summary>
public enum ConnectionError
{
    NameRequired,
    NameTooLong,
    HostRequired,
    HostHasWhitespace,
    PortRequired,
    PortOutOfRange,
    FixedWidthOutOfRange,
    FixedHeightOutOfRange,
}

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

    // Public: the application quotes them in its messages, so the number on screen is the number
    // enforced, from the same constant.
    public const int MinPort = 1;
    public const int MaxPort = 65535;
    public const int MinFixedWidth = 640;
    public const int MinFixedHeight = 480;
    public const int MaxFixedSide = 8192;

    /// <param name="port">The port, or <c>null</c> when the box is empty — reported as missing, not out of range.</param>
    public static IReadOnlyList<ConnectionError> Validate(string? name, string? host, int? port, DisplayMode mode, int? fixedWidth, int? fixedHeight)
    {
        var errors = new List<ConnectionError>();

        var trimmedName = name?.Trim() ?? "";
        if (trimmedName.Length == 0) errors.Add(ConnectionError.NameRequired);
        else if (trimmedName.Length > MaxNameLength) errors.Add(ConnectionError.NameTooLong);

        var trimmedHost = host?.Trim() ?? "";
        if (trimmedHost.Length == 0) errors.Add(ConnectionError.HostRequired);
        else if (trimmedHost.Any(char.IsWhiteSpace)) errors.Add(ConnectionError.HostHasWhitespace);

        if (port is null) errors.Add(ConnectionError.PortRequired);
        else if (port is < MinPort or > MaxPort) errors.Add(ConnectionError.PortOutOfRange);

        // Dynamic follows the window, so the fixed size is irrelevant — and must not block saving.
        if (mode != DisplayMode.Dynamic)
        {
            if (fixedWidth is not (>= MinFixedWidth and <= MaxFixedSide)) errors.Add(ConnectionError.FixedWidthOutOfRange);
            if (fixedHeight is not (>= MinFixedHeight and <= MaxFixedSide)) errors.Add(ConnectionError.FixedHeightOutOfRange);
        }

        return errors.AsReadOnly();
    }
}
