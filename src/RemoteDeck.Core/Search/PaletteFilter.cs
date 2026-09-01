namespace RemoteDeck.Core.Search;

/// <summary>What a palette entry stands for: a saved connection, a session already open, or an
/// action the shell can run. The palette picks the row's icon from it.</summary>
public enum PaletteItemKind
{
    /// <summary>A saved connection; <see cref="PaletteItem.Id"/> is <c>conn:&lt;id&gt;</c>.</summary>
    Connection = 0,
    /// <summary>An application command; <see cref="PaletteItem.Id"/> is <c>cmd:&lt;name&gt;</c>.</summary>
    Command = 1,
    /// <summary>A session already open, offered so it can be brought forward;
    /// <see cref="PaletteItem.Id"/> is <c>tab:&lt;index&gt;</c>.</summary>
    Session = 2,
}

/// <summary>
/// One entry offered by the command palette. <paramref name="Id"/> is what the caller acts on once the
/// entry is chosen; <paramref name="Priority"/> is a sort bonus, added to the score of a matched entry
/// and used as the primary key of an unfiltered list (frequent commands rank above plain connections).
/// </summary>
/// <param name="Shortcut">The keystroke that runs the entry without the palette, rendered as a key
/// cap on the right of the row, or empty when the entry has none. Display only: unlike
/// <paramref name="Title"/> and <paramref name="Subtitle"/> it is <em>not</em> searched, so that
/// typing "n" cannot pull "Ctrl+N" ahead of a connection actually named after it.</param>
/// <param name="Group">The localized heading the row is filed under. The caller composes it, the
/// same way it composes the two texts; the palette only groups equal values together.</param>
public sealed record PaletteItem(
    PaletteItemKind Kind,
    string Id,
    string Title,
    string Subtitle,
    int Priority,
    string Shortcut = "",
    string Group = "");

/// <summary>A palette entry kept by the filter, with its rank and the spans to highlight.</summary>
public sealed record PaletteMatch(
    PaletteItem Item,
    int Score,
    IReadOnlyList<MatchRange> TitleRanges,
    IReadOnlyList<MatchRange> SubtitleRanges);

/// <summary>
/// Fuzzy, accent- and case-insensitive filtering of heterogeneous palette entries. Pure: no I/O, no UI,
/// no state. Same tiers and range conventions as <see cref="ConnectionFilter"/>, over two fields instead
/// of three.
/// </summary>
public static class PaletteFilter
{
    private const int TitlePrefixScore = 100;
    private const int TitleSubstringScore = 60;
    private const int SubtitleSubstringScore = 40;
    private const int SubsequenceScore = 10;

    private enum MatchKind { Subsequence, Substring, Prefix }

    private sealed record FieldMatch(MatchKind Kind, IReadOnlyList<MatchRange> Ranges);

    /// <summary>
    /// Returns the palette entries matching <paramref name="query"/>, best first, capped at
    /// <paramref name="limit"/> entries. An empty or blank query keeps every entry with
    /// <c>Score = 0</c>, ordered by descending <see cref="PaletteItem.Priority"/> then by folded title.
    /// Otherwise the query is folded and split on whitespace, and an entry is kept only when
    /// <em>every</em> word is at least a subsequence of its folded title or subtitle; its
    /// <see cref="PaletteItem.Priority"/> is added to the text score, and ties are broken by folded
    /// title. The cap is applied after sorting, so it keeps the best entries, not the first seen.
    /// </summary>
    public static IReadOnlyList<PaletteMatch> Apply(IEnumerable<PaletteItem> items, string? query, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);

        if (string.IsNullOrWhiteSpace(query)) return Unfiltered(items, limit);

        var words = TextNormalizer.Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<PaletteMatch>();

        foreach (var item in items)
        {
            var match = Score(item, words);
            if (match is not null) matches.Add(match);
        }

        return matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => TextNormalizer.Fold(m.Item.Title), StringComparer.Ordinal)
            .Take(limit)
            .ToList()
            .AsReadOnly();
    }

    private static IReadOnlyList<PaletteMatch> Unfiltered(IEnumerable<PaletteItem> items, int limit)
        => items
            .OrderByDescending(i => i.Priority)
            .ThenBy(i => TextNormalizer.Fold(i.Title), StringComparer.Ordinal)
            .Take(limit)
            .Select(i => new PaletteMatch(i, 0, [], []))
            .ToList()
            .AsReadOnly();

    /// <summary>Scores one entry, or returns null when a word matches neither of its two fields.</summary>
    private static PaletteMatch? Score(PaletteItem item, string[] words)
    {
        var title = TextNormalizer.Fold(item.Title);
        var subtitle = TextNormalizer.Fold(item.Subtitle);

        var titleRanges = new List<MatchRange>();
        var subtitleRanges = new List<MatchRange>();
        var score = 0;

        foreach (var word in words)
        {
            var inTitle = Find(title, word);
            var inSubtitle = Find(subtitle, word);
            if (inTitle is null && inSubtitle is null) return null;

            score += WordScore(inTitle, inSubtitle);
            // Highlight every field the word was actually found in, even the one that lost the scoring.
            if (inTitle is not null) titleRanges.AddRange(inTitle.Ranges);
            if (inSubtitle is not null) subtitleRanges.AddRange(inSubtitle.Ranges);
        }

        return new PaletteMatch(item, score + item.Priority, titleRanges.AsReadOnly(), subtitleRanges.AsReadOnly());
    }

    /// <summary>Best tier wins; a word scores once, not once per field.</summary>
    private static int WordScore(FieldMatch? inTitle, FieldMatch? inSubtitle)
    {
        if (inTitle?.Kind == MatchKind.Prefix) return TitlePrefixScore;
        if (inTitle?.Kind == MatchKind.Substring) return TitleSubstringScore;
        if (inSubtitle is not null && inSubtitle.Kind != MatchKind.Subsequence) return SubtitleSubstringScore;
        return SubsequenceScore;
    }

    /// <summary>Contiguous match first (prefix, then substring), falling back to a fuzzy subsequence.</summary>
    private static FieldMatch? Find(string text, string word)
    {
        var at = text.IndexOf(word, StringComparison.Ordinal);
        if (at >= 0)
            return new FieldMatch(at == 0 ? MatchKind.Prefix : MatchKind.Substring, [new MatchRange(at, word.Length)]);

        var ranges = FindSubsequence(text, word);
        return ranges is null ? null : new FieldMatch(MatchKind.Subsequence, ranges);
    }

    /// <summary>Leftmost-greedy subsequence: one single-character range per matched character.</summary>
    private static IReadOnlyList<MatchRange>? FindSubsequence(string text, string word)
    {
        var ranges = new List<MatchRange>(word.Length);
        var from = 0;
        foreach (var c in word)
        {
            var at = text.IndexOf(c, from);
            if (at < 0) return null;
            ranges.Add(new MatchRange(at, 1));
            from = at + 1;
        }
        return ranges.AsReadOnly();
    }
}
