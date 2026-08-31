using RemoteDeck.Core.Import;

namespace RemoteDeck.Core.Tests.Import;

public sealed class MstscRegistryImporterTests
{
    [Fact]
    public void Hosts_are_deduplicated_without_regard_to_case()
    {
        (string Host, string? UserName)[] entries = [("SRV01", "jdoe"), ("srv02", null), ("srv01", "other")];

        var candidates = MstscRegistryImporter.FromServers(entries);

        Assert.Equal(new[] { "SRV01", "srv02" }, candidates.Select(c => c.Host).ToArray());
        Assert.Equal("SRV01", candidates[0].Name);
        Assert.Equal("jdoe", candidates[0].UserName);
        Assert.Null(candidates[1].UserName);
        Assert.Equal("mstsc registry", candidates[0].Source);
        Assert.Equal(3389, candidates[0].Port);
    }

    [Fact]
    public void Blank_hosts_are_ignored()
    {
        (string Host, string? UserName)[] entries = [("", "jdoe"), ("   ", null), (" srv01 ", "  ")];

        var candidates = MstscRegistryImporter.FromServers(entries);

        Assert.Equal("srv01", Assert.Single(candidates).Host);
        Assert.Null(candidates[0].UserName);
    }
}
