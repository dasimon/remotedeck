using System.Globalization;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Security;

namespace RemoteDeck.App.Resources;

/// <summary>
/// The sentence for each validation code. <c>Core</c> says <em>what</em> is wrong, as a code; the
/// words are the application's, in the user's language.
/// </summary>
/// <remarks>
/// Until 2026-09-06 the rules returned English sentences and the editors showed them as they were:
/// <c>Vérifiez le formulaire — Name is required.</c> in a French interface. The limits quoted in a
/// message come from the same constants the rule checks, so the number on screen cannot drift from
/// the number enforced.
/// </remarks>
internal static class ValidationMessages
{
    public static string Of(ConnectionError error) => error switch
    {
        ConnectionError.NameRequired => Strings.Editor_ErrNameRequired,
        ConnectionError.NameTooLong => Format(Strings.Editor_ErrNameTooLong, ConnectionRules.MaxNameLength),
        ConnectionError.HostRequired => Strings.Editor_ErrHostRequired,
        ConnectionError.HostHasWhitespace => Strings.Editor_ErrHostWhitespace,
        ConnectionError.PortRequired => Strings.Editor_ErrPortRequired,
        ConnectionError.PortOutOfRange => Format(Strings.Editor_ErrPortRange, ConnectionRules.MinPort, ConnectionRules.MaxPort),
        ConnectionError.FixedWidthOutOfRange => Format(Strings.Editor_ErrWidthRange, ConnectionRules.MinFixedWidth, ConnectionRules.MaxFixedSide),
        ConnectionError.FixedHeightOutOfRange => Format(Strings.Editor_ErrHeightRange, ConnectionRules.MinFixedHeight, ConnectionRules.MaxFixedSide),
        _ => error.ToString(),
    };

    public static string Of(CredentialError error) => error switch
    {
        CredentialError.LabelRequired => Strings.CredEditor_ErrLabelRequired,
        CredentialError.LabelTooLong => Format(Strings.CredEditor_ErrLabelTooLong, CredentialRules.MaxLabelLength),
        CredentialError.LabelTaken => Strings.CredEditor_ErrLabelTaken,
        CredentialError.UserNameRequired => Strings.CredEditor_ErrUserRequired,
        _ => error.ToString(),
    };

    private static string Format(string pattern, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, pattern, args);
}
