using RemoteDeck.Core.Model;
using RemoteDeck.Core.Search;

namespace RemoteDeck.Core.Tests.Search;

public sealed class ConnectionFilterTests
{
    private static Connection C(string name, string host = "h", string group = "", bool fav = false)
        => new() { Name = name, Host = host, GroupName = group, IsFavorite = fav };

    [Fact]
    public void Fold_removes_case_and_accents_and_keeps_length()
    {
        Assert.Equal("elan", TextNormalizer.Fold("Élan"));
        Assert.Equal(4, TextNormalizer.Fold("Élan").Length);
        Assert.Equal("ss", TextNormalizer.Fold("SS"));
    }

    [Fact]
    public void Empty_query_returns_all_favorites_first_then_group_then_name()
    {
        var all = new[] { C("zeta", group: "Dev"), C("alpha", group: "Prod"), C("Beta", group: "Dev"), C("omega", group: "Prod", fav: true) };

        var names = ConnectionFilter.Apply(all, "  ").Select(m => m.Connection.Name).ToArray();

        Assert.Equal(new[] { "omega", "Beta", "zeta", "alpha" }, names);
    }

    [Fact]
    public void Query_is_accent_and_case_insensitive()
    {
        var all = new[] { C("Élan Prod"), C("Other") };

        var r = ConnectionFilter.Apply(all, "ELAN");

        Assert.Single(r);
        Assert.Equal("Élan Prod", r[0].Connection.Name);
        Assert.Equal(new MatchRange(0, 4), r[0].NameRanges[0]);
    }

    [Fact]
    public void Prefix_on_name_outranks_substring_on_host()
    {
        var all = new[] { C("Web01", host: "sql-prod"), C("SQL Prod", host: "web01") };

        var r = ConnectionFilter.Apply(all, "sql");

        Assert.Equal("SQL Prod", r[0].Connection.Name);
        Assert.Equal("Web01", r[1].Connection.Name);
        Assert.Equal(new MatchRange(0, 3), r[1].HostRanges[0]);
    }

    [Fact]
    public void Fuzzy_subsequence_matches_and_reports_each_character()
    {
        var all = new[] { C("Hyper-V Host 3") };

        var r = ConnectionFilter.Apply(all, "hvh");

        Assert.Single(r);
        Assert.Equal(3, r[0].NameRanges.Count);
        Assert.Equal(new MatchRange(0, 1), r[0].NameRanges[0]);
    }

    [Fact]
    public void Every_word_must_match_somewhere()
    {
        var all = new[] { C("DC01", host: "dc01.corp", group: "Prod"), C("DC02", host: "dc02.corp", group: "Dev") };

        var r = ConnectionFilter.Apply(all, "dc prod");

        Assert.Single(r);
        Assert.Equal("DC01", r[0].Connection.Name);
    }

    [Fact]
    public void Favorites_rank_first_even_with_lower_text_score()
    {
        var all = new[] { C("sql prefix"), C("x sql", fav: true) };

        var r = ConnectionFilter.Apply(all, "sql");

        Assert.Equal("x sql", r[0].Connection.Name);
    }

    [Fact]
    public void No_match_returns_empty()
        => Assert.Empty(ConnectionFilter.Apply([C("A")], "zzz"));
}
