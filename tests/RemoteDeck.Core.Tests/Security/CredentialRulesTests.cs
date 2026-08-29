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
}
