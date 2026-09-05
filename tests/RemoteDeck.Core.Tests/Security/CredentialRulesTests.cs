using RemoteDeck.Core.Security;

namespace RemoteDeck.Core.Tests.Security;

public sealed class CredentialRulesTests
{
    [Fact]
    public void Valid_input_has_no_errors()
        => Assert.Empty(CredentialRules.Validate("Admin", "admin", ["Other"]));

    [Fact]
    public void Label_and_user_are_required()
    {
        var errors = CredentialRules.Validate("  ", "", []);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Label_must_be_unique_case_insensitively()
        => Assert.Single(CredentialRules.Validate("admin", "u", ["ADMIN"]));

    [Fact]
    public void Label_is_limited_to_64_characters()
        => Assert.Single(CredentialRules.Validate(new string('a', 65), "u", []));

    [Fact]
    public void Null_other_labels_is_rejected()
        => Assert.Throws<ArgumentNullException>(() => { _ = CredentialRules.Validate("Admin", "u", null!); });

    /// <summary>The editor trims the label it saves, so a stored row that kept its surrounding
    /// whitespace must still be recognised as the same label.</summary>
    [Fact]
    public void Existing_labels_are_compared_trimmed()
        => Assert.Single(CredentialRules.Validate("admin", "u", [" Admin "]));

    // The rules return codes since 2026-09-06 -- what is wrong is Core's to say, the words are the
    // application's. The two below hold the codes themselves; the tests above hold the counts.
    [Fact]
    public void Each_missing_field_is_named_by_its_code()
        => Assert.Equal([CredentialError.LabelRequired, CredentialError.UserNameRequired], CredentialRules.Validate("  ", "", ["Other"]));

    [Fact]
    public void A_taken_label_is_reported_as_taken_not_as_missing()
        => Assert.Equal([CredentialError.LabelTaken], CredentialRules.Validate("admin", "u", [" Admin "]));
}
