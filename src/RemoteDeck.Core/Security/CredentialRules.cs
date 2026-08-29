namespace RemoteDeck.Core.Security;

/// <summary>Validation rules for the credential editor. Pure; messages are UI-ready English.</summary>
public static class CredentialRules
{
    public const int MaxLabelLength = 64;

    public static IReadOnlyList<string> Validate(string? label, string? userName, IEnumerable<string> otherLabels)
    {
        var errors = new List<string>();
        var trimmed = label?.Trim() ?? "";
        if (trimmed.Length == 0) errors.Add("Label is required.");
        else if (trimmed.Length > MaxLabelLength) errors.Add($"Label must be at most {MaxLabelLength} characters.");
        else if (otherLabels.Any(o => string.Equals(o, trimmed, StringComparison.OrdinalIgnoreCase))) errors.Add("A credential with this label already exists.");
        if (string.IsNullOrWhiteSpace(userName)) errors.Add("User name is required.");
        return errors;
    }
}
