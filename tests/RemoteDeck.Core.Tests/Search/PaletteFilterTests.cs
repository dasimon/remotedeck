using RemoteDeck.Core.Search;

namespace RemoteDeck.Core.Tests.Search;

public sealed class PaletteFilterTests
{
    private static PaletteItem Item(string title, string subtitle = "", int priority = 0, PaletteItemKind kind = PaletteItemKind.Connection)
        => new(kind, $"conn:{title}", title, subtitle, priority);

    [Fact]
    public void Empty_query_returns_every_item_by_priority_then_title()
    {
        var all = new[] { Item("zeta"), Item("alpha"), Item("Beta", priority: 10), Item("omega", priority: 10) };

        var titles = PaletteFilter.Apply(all, "  ").Select(m => m.Item.Title).ToArray();

        Assert.Equal(new[] { "Beta", "omega", "alpha", "zeta" }, titles);
    }

    [Fact]
    public void Limit_truncates_after_sorting()
    {
        var all = new[] { Item("srv-a"), Item("srv-b"), Item("srv-c"), Item("srv-d"), Item("srv-e") };

        Assert.Equal(2, PaletteFilter.Apply(all, null, limit: 2).Count);
        Assert.Equal(new[] { "srv-a", "srv-b" }, PaletteFilter.Apply(all, "srv", limit: 2).Select(m => m.Item.Title).ToArray());
    }

    [Fact]
    public void Title_prefix_outranks_subtitle_substring()
    {
        var all = new[] { Item("Web01", "prod . sql-01"), Item("SQL Prod", "dev . web-01") };

        var r = PaletteFilter.Apply(all, "sql");

        Assert.Equal("SQL Prod", r[0].Item.Title);
        Assert.Equal("Web01", r[1].Item.Title);
        Assert.True(r[0].Score > r[1].Score);
    }

    [Fact]
    public void Query_is_accent_and_case_insensitive()
    {
        var all = new[] { Item("Élan Prod", "café"), Item("Other") };

        var r = PaletteFilter.Apply(all, "ELAN");

        Assert.Single(r);
        Assert.Equal("Élan Prod", r[0].Item.Title);
        Assert.Equal(new MatchRange(0, 4), r[0].TitleRanges[0]);
    }

    [Fact]
    public void Every_word_must_match_the_title_or_the_subtitle()
    {
        var all = new[] { Item("SQL Prod", "datacenter one"), Item("SQL Dev", "lab") };

        var r = PaletteFilter.Apply(all, "sql datacenter");

        Assert.Single(r);
        Assert.Equal("SQL Prod", r[0].Item.Title);
    }

    [Fact]
    public void Title_ranges_point_at_the_matched_substring()
    {
        var all = new[] { Item("web-sql-01") };

        var r = PaletteFilter.Apply(all, "sql");

        Assert.Equal(new MatchRange(4, 3), Assert.Single(r[0].TitleRanges));
        Assert.Empty(r[0].SubtitleRanges);
    }

    [Fact]
    public void Subsequence_match_yields_one_range_per_character()
    {
        var all = new[] { Item("Alpha Beta") };

        var r = PaletteFilter.Apply(all, "ab");

        Assert.Equal(new[] { new MatchRange(0, 1), new MatchRange(6, 1) }, r[0].TitleRanges);
        Assert.Equal(10, r[0].Score);
    }

    [Fact]
    public void Priority_breaks_a_tie_between_equal_text_scores()
    {
        var all = new[] { Item("alpha srv"), Item("beta srv", priority: 5, kind: PaletteItemKind.Command) };

        var r = PaletteFilter.Apply(all, "srv");

        Assert.Equal("beta srv", r[0].Item.Title);
        Assert.Equal(65, r[0].Score);
        Assert.Equal(60, r[1].Score);
    }

    [Fact]
    public void No_match_returns_an_empty_list()
    {
        var all = new[] { Item("SQL Prod", "datacenter") };

        Assert.Empty(PaletteFilter.Apply(all, "zzz"));
    }
}
