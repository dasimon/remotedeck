using System.Globalization;
using System.Text;

namespace RemoteDeck.Core.Search;

/// <summary>Case- and accent-insensitive text folding, for comparison only — never for display or storage.</summary>
public static class TextNormalizer
{
    /// <summary>
    /// Folds <paramref name="s"/> to lower-case, accent-free text, emitting exactly one character per
    /// input character: the first non-combining character of that character's FormD decomposition.
    /// Length is preserved char by char, so an index into the folded text is also an index into the
    /// original — highlight ranges computed on the fold stay valid on the string the UI shows.
    /// </summary>
    public static string Fold(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Length == 0) return "";

        var folded = new StringBuilder(s.Length);
        foreach (var c in s) folded.Append(FoldChar(c));
        return folded.ToString();
    }

    private static char FoldChar(char c)
    {
        // ASCII has nothing to decompose; a lone surrogate cannot be normalized (it would throw).
        if (char.IsAscii(c) || char.IsSurrogate(c)) return char.ToLowerInvariant(c);

        foreach (var d in c.ToString().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark)
                return char.ToLowerInvariant(d);

        // Nothing but combining marks (or no decomposition at all): keep the character itself.
        return char.ToLowerInvariant(c);
    }
}
