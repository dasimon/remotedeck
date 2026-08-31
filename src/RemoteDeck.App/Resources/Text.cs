using System.Globalization;

namespace RemoteDeck.App.Resources;

/// <summary>
/// Composes a localised sentence from a <see cref="Strings"/> template and its arguments.
///
/// Every composed message in the app goes through here rather than through string concatenation:
/// a translation is free to move the placeholders around, and only a positional format can follow
/// it. <see cref="CultureInfo.CurrentCulture"/> is passed explicitly so numbers inside a message
/// are written the way the displayed language writes them.
/// </summary>
internal static class Text
{
    /// <summary>Formats <paramref name="template"/> — always a <see cref="Strings"/> member — with
    /// <paramref name="args"/>.</summary>
    public static string Of(string template, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, template, args);

    /// <summary>Picks the singular or the plural template for <paramref name="count"/> and formats it
    /// with <paramref name="args"/>. English pluralises everything but 1; a translation that groups
    /// zero with the singular simply writes the same wording in both templates.</summary>
    public static string Plural(int count, string one, string many, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, count == 1 ? one : many, args);
}
