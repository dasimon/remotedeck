namespace RemoteDeck.Core.Model;

/// <summary>Validation rules for the connection editor. Pure; messages are UI-ready English.</summary>
public static class ConnectionRules
{
    public const int MaxNameLength = 80;

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
