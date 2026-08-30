using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Search;

/// <summary>A highlight span, expressed in indexes of the <em>original</em> (unfolded) string.</summary>
public readonly record struct MatchRange(int Start, int Length);

/// <summary>A connection kept by the filter, with its rank and the spans to highlight.</summary>
public sealed record ConnectionMatch(
    Connection Connection,
    int Score,
    IReadOnlyList<MatchRange> NameRanges,
    IReadOnlyList<MatchRange> HostRanges);

/// <summary>
/// Fuzzy, accent- and case-insensitive filtering of connections. Pure: no I/O, no UI, no state.
/// </summary>
public static class ConnectionFilter
{
    private const int FavoriteBonus = 1000;
    private const int NamePrefixScore = 100;
    private const int NameSubstringScore = 60;
    private const int HostSubstringScore = 40;
    private const int GroupSubstringScore = 20;
    private const int SubsequenceScore = 10;

    private enum MatchKind { Subsequence, Substring, Prefix }

    private sealed record FieldMatch(MatchKind Kind, IReadOnlyList<MatchRange> Ranges);

    /// <summary>
    /// Returns the connections matching <paramref name="query"/>, best first. An empty or blank query
    /// keeps every connection with <c>Score = 0</c>, ordered like <c>ConnectionRepository.GetAll()</c>
    /// (favorites first, then group, then name). Otherwise the query is folded and split on whitespace,
    /// and a connection is kept only when <em>every</em> word is a subsequence of its folded name, host
    /// or group.
    /// </summary>
    public static IReadOnlyList<ConnectionMatch> Apply(IEnumerable<Connection> connections, string? query)
    {
        ArgumentNullException.ThrowIfNull(connections);

        if (string.IsNullOrWhiteSpace(query)) return Unfiltered(connections);

        var words = TextNormalizer.Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<ConnectionMatch>();

        foreach (var connection in connections)
        {
            var match = Score(connection, words);
            if (match is not null) matches.Add(match);
        }

        return matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => TextNormalizer.Fold(m.Connection.Name), StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    private static IReadOnlyList<ConnectionMatch> Unfiltered(IEnumerable<Connection> connections)
        => connections
            .OrderByDescending(c => c.IsFavorite)
            .ThenBy(c => TextNormalizer.Fold(c.GroupName), StringComparer.Ordinal)
            .ThenBy(c => TextNormalizer.Fold(c.Name), StringComparer.Ordinal)
            .Select(c => new ConnectionMatch(c, 0, [], []))
            .ToList()
            .AsReadOnly();

    /// <summary>Scores one connection, or returns null when a word matches none of its fields.</summary>
    private static ConnectionMatch? Score(Connection connection, string[] words)
    {
        var name = TextNormalizer.Fold(connection.Name);
        var host = TextNormalizer.Fold(connection.Host);
        var group = TextNormalizer.Fold(connection.GroupName);

        var nameRanges = new List<MatchRange>();
        var hostRanges = new List<MatchRange>();
        var score = 0;

        foreach (var word in words)
        {
            var inName = Find(name, word);
            var inHost = Find(host, word);
            var inGroup = Find(group, word);
            if (inName is null && inHost is null && inGroup is null) return null;

            score += WordScore(inName, inHost, inGroup);
            // Highlight every field the word was actually found in, even the ones that lost the scoring.
            if (inName is not null) nameRanges.AddRange(inName.Ranges);
            if (inHost is not null) hostRanges.AddRange(inHost.Ranges);
        }

        if (connection.IsFavorite) score += FavoriteBonus;
        return new ConnectionMatch(connection, score, nameRanges.AsReadOnly(), hostRanges.AsReadOnly());
    }

    /// <summary>Best tier wins; a word scores once, not once per field.</summary>
    private static int WordScore(FieldMatch? inName, FieldMatch? inHost, FieldMatch? inGroup)
    {
        if (inName?.Kind == MatchKind.Prefix) return NamePrefixScore;
        if (inName?.Kind == MatchKind.Substring) return NameSubstringScore;
        if (inHost is not null && inHost.Kind != MatchKind.Subsequence) return HostSubstringScore;
        if (inGroup is not null && inGroup.Kind != MatchKind.Subsequence) return GroupSubstringScore;
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
