namespace RemoteDeck.Core.Security;

/// <summary>Validation rules for the credential editor. Pure; messages are UI-ready English.</summary>
/// <summary>What is wrong with a credential, as a code. The words are the application's.</summary>
public enum CredentialError
{
    LabelRequired,
    LabelTooLong,
    LabelTaken,
    UserNameRequired,
}

public static class CredentialRules
{
    public const int MaxLabelLength = 64;

    public static IReadOnlyList<CredentialError> Validate(string? label, string? userName, IEnumerable<string> otherLabels)
    {
        ArgumentNullException.ThrowIfNull(otherLabels);

        var errors = new List<CredentialError>();
        var trimmed = label?.Trim() ?? "";
        if (trimmed.Length == 0) errors.Add(CredentialError.LabelRequired);
        else if (trimmed.Length > MaxLabelLength) errors.Add(CredentialError.LabelTooLong);
        // Both sides are trimmed: the editor stores a trimmed label, but a row written before that
        // rule — or by hand — can still carry surrounding whitespace and must not sneak past.
        else if (otherLabels.Any(o => string.Equals(o.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))) errors.Add(CredentialError.LabelTaken);
        if (string.IsNullOrWhiteSpace(userName)) errors.Add(CredentialError.UserNameRequired);
        return errors.AsReadOnly();
    }
}
