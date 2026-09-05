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
    public void A_web_account_candidate_without_a_name_takes_the_hint_mstsc_remembers_for_its_host()
    {
        // The .rdp mstsc exports carries no username line; the UPN lives in the registry hint of the
        // server. Matching is by host, without regard to case, and only fills what is empty.
        ImportCandidate[] candidates =
        [
            new() { Name = "win02", Host = "fdc-win02", UseWebAccount = true, Source = "win02.rdp" },
            new() { Name = "win03", Host = "fdc-win03", UseWebAccount = true, UserName = "already@contoso.com", Source = "win03.rdp" },
            new() { Name = "sql", Host = "fdcsql00001", UseWebAccount = false, Source = "sql.rdp" },
        ];
        (string Host, string? UserName)[] hints = [("FDC-WIN02", "user@contoso.com"), ("fdc-win03", "other@contoso.com"), ("fdcsql00001", "admin")];

        var filled = MstscRegistryImporter.WithUserNameHints(candidates, hints);

        Assert.Equal("user@contoso.com", filled[0].UserName);
        Assert.Equal("user@contoso.com", filled[0].WebAccountUpn);
        Assert.Equal("already@contoso.com", filled[1].UserName);
        // No web account, so the hint would be a credential's name: not carried.
        Assert.Null(filled[2].UserName);
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
