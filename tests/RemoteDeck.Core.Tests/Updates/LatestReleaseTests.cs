using RemoteDeck.Core.Updates;

namespace RemoteDeck.Core.Tests.Updates;

/// <summary>Reading the answer of the GitHub "latest release" call, which must never throw.</summary>
public sealed class LatestReleaseTests
{
    private const string ExeHash = "669f7ab5dbbae1efbac700f88cdb166bd7395a806d7d6f498073a119f1e6e330";
    private const string SumsHash = "bee805430933bdf830ed5f168e2484309c285eb9ef941f8b9d780af4164c6b07";

    private const string ExeUrl = "https://github.com/dasimon/remotedeck/releases/download/v0.8.0/RemoteDeck.exe";
    private const string SumsUrl = "https://github.com/dasimon/remotedeck/releases/download/v0.8.0/SHA256SUMS";
    private const string PageUrl = "https://github.com/dasimon/remotedeck/releases/tag/v0.8.0";

    private static string ExeAsset(string size = "167000293", string url = ExeUrl, string digestMember = ",\"digest\":\"sha256:" + ExeHash + "\"")
        => "{\"name\":\"RemoteDeck.exe\",\"size\":" + size + ",\"browser_download_url\":\"" + url + "\"" + digestMember + "}";

    private static string SumsAsset(string url = SumsUrl)
        => "{\"name\":\"SHA256SUMS\",\"size\":80,\"browser_download_url\":\"" + url + "\",\"digest\":\"sha256:" + SumsHash + "\"}";

    private static string Json(
        string tag = "\"v0.8.0\"",
        string prerelease = "false",
        string draft = "false",
        string page = "\"" + PageUrl + "\"",
        string? exe = null,
        string? sums = null)
    {
        var assets = string.Join(",", new[] { exe ?? ExeAsset(), sums ?? SumsAsset() }.Where(a => a.Length > 0));
        return $"{{\"tag_name\":{tag},\"prerelease\":{prerelease},\"draft\":{draft},\"html_url\":{page},\"assets\":[{assets}]}}";
    }

    [Fact]
    public void The_real_shape_of_the_answer_is_read()
    {
        var release = LatestRelease.Parse(Json());

        Assert.NotNull(release);
        Assert.Equal(new ReleaseVersion(0, 8, 0), release.Version);
        Assert.Equal(new Uri(PageUrl), release.PageUrl);
        Assert.Equal(new Uri(ExeUrl), release.ExeUrl);
        Assert.Equal(167000293L, release.ExeSize);
        Assert.Equal(ExeHash, release.ExeDigest);
        Assert.Equal(new Uri(SumsUrl), release.SumsUrl);
    }

    [Fact]
    public void An_upper_case_digest_is_returned_in_lower_case()
    {
        var exe = ExeAsset(digestMember: ",\"digest\":\"sha256:" + ExeHash.ToUpperInvariant() + "\"");

        Assert.Equal(ExeHash, LatestRelease.Parse(Json(exe: exe))!.ExeDigest);
    }

    [Theory]
    // Older releases carry no digest at all.
    [InlineData("")]
    [InlineData(",\"digest\":null")]
    // Another algorithm, or a hash that is not 64 hexadecimal characters: not trusted, not used.
    [InlineData(",\"digest\":\"sha512:669f\"")]
    [InlineData(",\"digest\":\"sha256:669f\"")]
    [InlineData(",\"digest\":\"669f7ab5dbbae1efbac700f88cdb166bd7395a806d7d6f498073a119f1e6e330\"")]
    [InlineData(",\"digest\":12")]
    public void A_missing_or_unreadable_digest_gives_none(string digestMember)
    {
        var release = LatestRelease.Parse(Json(exe: ExeAsset(digestMember: digestMember)));

        Assert.NotNull(release);
        Assert.Null(release.ExeDigest);
    }

    [Fact]
    public void The_assets_are_found_whatever_their_order_and_the_other_assets_are_ignored()
    {
        var json = "{\"tag_name\":\"v1.0.0\",\"prerelease\":false,\"draft\":false,\"html_url\":\"" + PageUrl + "\",\"assets\":["
            + SumsAsset() + ","
            + "{\"name\":\"RemoteDeck.pdb\",\"size\":5,\"browser_download_url\":\"https://github.com/x/RemoteDeck.pdb\"},"
            + ExeAsset() + "]}";

        var release = LatestRelease.Parse(json);

        Assert.NotNull(release);
        Assert.Equal(new Uri(ExeUrl), release.ExeUrl);
        Assert.Equal(new Uri(SumsUrl), release.SumsUrl);
        Assert.Equal(new ReleaseVersion(1, 0, 0), release.Version);
    }

    [Theory]
    // A captive portal or a proxy answering 200 with a web page.
    [InlineData("<html><body>Please log in to the guest network</body></html>")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"v0.8.0\"")]
    [InlineData("{\"tag_name\":")]
    [InlineData("{\"message\":\"Not Found\",\"status\":\"404\"}")]
    public void Anything_that_is_not_a_release_gives_nothing(string json)
    {
        Assert.Null(LatestRelease.Parse(json));
    }

    [Fact]
    public void A_pre_release_is_ignored()
    {
        Assert.Null(LatestRelease.Parse(Json(prerelease: "true")));
    }

    [Fact]
    public void A_draft_is_ignored()
    {
        Assert.Null(LatestRelease.Parse(Json(draft: "true")));
    }

    [Theory]
    [InlineData("\"0.8.0\"")]
    [InlineData("\"v0.8.0-beta\"")]
    [InlineData("\"v0.8\"")]
    [InlineData("\"nightly\"")]
    [InlineData("null")]
    [InlineData("8")]
    public void A_refused_tag_gives_nothing(string tag)
    {
        Assert.Null(LatestRelease.Parse(Json(tag: tag)));
    }

    [Fact]
    public void A_missing_flag_is_not_assumed_to_be_false()
    {
        var json = "{\"tag_name\":\"v0.8.0\",\"html_url\":\"" + PageUrl + "\",\"assets\":[" + ExeAsset() + "," + SumsAsset() + "]}";

        Assert.Null(LatestRelease.Parse(json));
    }

    [Fact]
    public void Without_the_executable_there_is_nothing_to_offer()
    {
        Assert.Null(LatestRelease.Parse(Json(exe: "")));
    }

    [Fact]
    public void Without_the_checksums_there_is_nothing_to_offer()
    {
        Assert.Null(LatestRelease.Parse(Json(sums: "")));
    }

    [Fact]
    public void An_asset_that_only_looks_like_the_executable_does_not_count()
    {
        var other = ExeAsset().Replace("RemoteDeck.exe\"", "remotedeck.exe\"", StringComparison.Ordinal);

        Assert.Null(LatestRelease.Parse(Json(exe: other)));
    }

    [Theory]
    [InlineData("http://github.com/dasimon/remotedeck/releases/download/v0.8.0/RemoteDeck.exe")]
    [InlineData("/dasimon/remotedeck/RemoteDeck.exe")]
    [InlineData("ftp://github.com/RemoteDeck.exe")]
    [InlineData("not a url")]
    public void A_download_address_that_is_not_https_gives_nothing(string url)
    {
        Assert.Null(LatestRelease.Parse(Json(exe: ExeAsset(url: url))));
        Assert.Null(LatestRelease.Parse(Json(sums: SumsAsset(url: url))));
    }

    [Fact]
    public void A_page_address_that_is_not_https_gives_nothing()
    {
        Assert.Null(LatestRelease.Parse(Json(page: "\"http://github.com/dasimon/remotedeck/releases/tag/v0.8.0\"")));
        Assert.Null(LatestRelease.Parse(Json(page: "null")));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("\"167000293\"")]
    [InlineData("null")]
    // Does not fit in a long.
    [InlineData("99999999999999999999")]
    [InlineData("1.5")]
    public void An_executable_without_a_positive_size_gives_nothing(string size)
    {
        Assert.Null(LatestRelease.Parse(Json(exe: ExeAsset(size: size))));
    }

    [Fact]
    public void Assets_of_an_unexpected_shape_give_nothing_and_never_throw()
    {
        var head = "{\"tag_name\":\"v0.8.0\",\"prerelease\":false,\"draft\":false,\"html_url\":\"" + PageUrl + "\",\"assets\":";

        Assert.Null(LatestRelease.Parse(head + "[1,null,\"RemoteDeck.exe\",{\"name\":5},{\"name\":\"RemoteDeck.exe\"}]}"));
        Assert.Null(LatestRelease.Parse(head + "{}}"));
    }
}
