namespace RemoteDeck.Core.Model;

/// <summary>
/// The letters a connection's badge shows, taken from the end of its name: machines of one fleet
/// share their beginning ("FDC-VM-WIN01", "FDC-VM-WIN03"), so initials would draw the same badge
/// on every row. The badge is the first letter of the last word that has one, followed by the
/// number the name ends with ("W1", "W3"), or by that word's second letter when there is no number
/// ("FDCSQL-RECETTE" gives "RE"). Words are runs of letters and digits.
/// </summary>
public static class Monogram
{
    /// <summary>At most this many digits of the ending number, the last ones.</summary>
    private const int MaxDigits = 2;

    public static string Of(string? name)
    {
        var words = Words(name ?? "");
        if (words.Count == 0) return "";

        var number = EndingNumber(words[^1]);
        var lettered = words.LastOrDefault(w => w.Any(char.IsLetter));
        var badge = lettered is null ? number
            : number.Length > 0 ? $"{lettered[0]}{number}"
            : lettered[..Math.Min(2, lettered.Length)];
        return badge.ToUpperInvariant();
    }

    /// <summary>The digits <paramref name="word"/> ends with, without leading zeros ("007" is "7",
    /// "000" is "0"), keeping the last <see cref="MaxDigits"/>; empty when it ends with a letter.</summary>
    private static string EndingNumber(string word)
    {
        var start = word.Length;
        while (start > 0 && char.IsDigit(word[start - 1])) start--;
        if (start == word.Length) return "";

        var digits = word[start..].TrimStart('0');
        if (digits.Length == 0) digits = "0";
        return digits[Math.Max(0, digits.Length - MaxDigits)..];
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var separator = i == text.Length || !char.IsLetterOrDigit(text[i]);
            if (!separator && start < 0) start = i;
            if (separator && start >= 0)
            {
                words.Add(text[start..i]);
                start = -1;
            }
        }

        return words;
    }
}
