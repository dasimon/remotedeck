using RemoteDeck.Core.Rdp;

namespace RemoteDeck.Core.Tests.Rdp;

public sealed class RdpControlCatalogTests
{
    [Fact]
    public void Candidates_are_ordered_newest_first()
    {
        var labels = RdpControlCatalog.Candidates.Select(c => c.Label).ToArray();

        Assert.Equal(new[] { "13", "12", "11", "10" }, labels);
    }

    [Fact]
    public void Candidates_have_distinct_clsids()
    {
        var clsids = RdpControlCatalog.Candidates.Select(c => c.Clsid).ToArray();

        Assert.Equal(clsids.Length, clsids.Distinct().Count());
    }

    [Fact]
    public void Select_returns_newest_registered_candidate()
    {
        var v12 = RdpControlCatalog.Candidates[1].Clsid;
        var v10 = RdpControlCatalog.Candidates[3].Clsid;

        var chosen = RdpControlCatalog.Select(g => g == v12 || g == v10);

        Assert.NotNull(chosen);
        Assert.Equal("12", chosen.Label);
    }

    [Fact]
    public void Select_returns_null_when_nothing_is_registered()
    {
        Assert.Null(RdpControlCatalog.Select(_ => false));
    }

    [Fact]
    public void Select_stops_probing_after_first_match()
    {
        var probed = new List<Guid>();

        RdpControlCatalog.Select(g => { probed.Add(g); return true; });

        Assert.Single(probed);
    }
}
