using System.Net;
using System.Security.Cryptography;
using System.Text;
using RemoteDeck.Core.Updates;

namespace RemoteDeck.Core.Tests.Updates;

/// <summary>What the update client accepts from the network, with a fake handler standing in for
/// GitHub: the host list on every hop, the redirection limit, the size caps and the hash checks, in
/// the order that keeps a wrong file off the disk.</summary>
public sealed class ReleaseClientTests : IDisposable
{
    private const string Repository = "dasimon/remotedeck";
    private const string ApiUrl = "https://api.github.com/repos/dasimon/remotedeck/releases/latest";
    private const string ExeUrl = "https://github.com/dasimon/remotedeck/releases/download/v0.8.0/RemoteDeck.exe";
    private const string SumsUrl = "https://github.com/dasimon/remotedeck/releases/download/v0.8.0/SHA256SUMS";
    private const string AssetUrl = "https://release-assets.githubusercontent.com/asset/RemoteDeck.exe?sig=secret";

    private static readonly byte[] Exe = Encoding.ASCII.GetBytes("MZ pretend executable, long enough to be split in a few reads");
    private static readonly string ExeHash = Convert.ToHexStringLower(SHA256.HashData(Exe));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RemoteDeckRelease-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();

    public ReleaseClientTests()
    {
        _github.Bytes(SumsUrl, Encoding.ASCII.GetBytes($"{ExeHash} *RemoteDeck.exe\n"));
        // As on GitHub: the asset link redirects to the file host.
        _github.Redirect(ExeUrl, AssetUrl);
        _github.Bytes(AssetUrl, Exe);
    }

    public void Dispose()
    {
        _github.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static LatestRelease Release(string? digest = null, long? size = null, string sums = SumsUrl, string exe = ExeUrl) =>
        new(new ReleaseVersion(0, 8, 0), new Uri("https://github.com/dasimon/remotedeck/releases/tag/v0.8.0"),
            new Uri(exe), size ?? Exe.Length, digest, new Uri(sums));

    private static string ReleaseJson(long size = 1000, string exeUrl = ExeUrl) =>
        "{\"tag_name\":\"v0.8.0\",\"prerelease\":false,\"draft\":false," +
        "\"html_url\":\"https://github.com/dasimon/remotedeck/releases/tag/v0.8.0\",\"assets\":[" +
        $"{{\"name\":\"RemoteDeck.exe\",\"size\":{size},\"browser_download_url\":\"{exeUrl}\"}}," +
        $"{{\"name\":\"SHA256SUMS\",\"size\":80,\"browser_download_url\":\"{SumsUrl}\"}}]}}";

    private async Task<LatestRelease> GetLatestAsync()
    {
        using var client = new ReleaseClient(_github, "0.0.1");
        return await client.GetLatestAsync(Repository, CancellationToken.None);
    }

    private async Task<VerifiedDownload> DownloadAsync(LatestRelease release)
    {
        using var client = new ReleaseClient(_github, "0.0.1");
        return await client.DownloadAsync(release, _directory, new Progress<double>(), CancellationToken.None);
    }

    private string Target => Path.Combine(_directory, "RemoteDeck.exe");

    private void AssertNothingOnDisk()
    {
        Assert.False(File.Exists(Target));
        Assert.False(File.Exists(Target + ".partial"));
    }

    // --- Check ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_latest_release_is_read_with_the_headers_GitHub_requires()
    {
        _github.Text(ApiUrl, ReleaseJson());

        var release = await GetLatestAsync();

        Assert.Equal(new ReleaseVersion(0, 8, 0), release.Version);
        var request = Assert.Single(_github.Requests);
        Assert.Equal("RemoteDeck/0.0.1", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
    }

    [Fact]
    public async Task A_web_page_instead_of_the_API_answer_is_an_update_failure()
    {
        _github.Text(ApiUrl, "<html><body>Please sign in to the Wi-Fi</body></html>");

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    [Fact]
    public async Task A_release_larger_than_the_cap_is_not_offered()
    {
        _github.Text(ApiUrl, ReleaseJson(size: ReleaseClient.MaxExeBytes + 1));

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    [Fact]
    public async Task A_release_whose_files_are_elsewhere_is_not_offered()
    {
        _github.Text(ApiUrl, ReleaseJson(exeUrl: "https://example.com/RemoteDeck.exe"));

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    [Fact]
    public async Task An_API_answer_one_byte_over_the_cap_is_refused()
    {
        var json = ReleaseJson();
        _github.Text(ApiUrl, json + new string(' ', (int)ReleaseClient.MaxApiBytes - json.Length + 1));

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    [Fact]
    public async Task An_HTTP_error_is_an_update_failure()
    {
        // Nothing registered for the API address: the fake answers 404.
        var failure = await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);

        Assert.Contains("404", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_failure_reports_its_innermost_cause()
    {
        _github.Fail(ApiUrl, new HttpRequestException("outer", new IOException("certificate rejected")));

        var failure = await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);

        Assert.Contains("certificate rejected", failure.Message, StringComparison.Ordinal);
    }

    // --- Redirections ----------------------------------------------------------------------------

    [Fact]
    public async Task A_relative_redirection_is_resolved_against_the_address_that_sent_it()
    {
        _github.Redirect(ApiUrl, "/repos/dasimon/remotedeck/releases/123");
        _github.Text("https://api.github.com/repos/dasimon/remotedeck/releases/123", ReleaseJson());

        await GetLatestAsync();

        Assert.Equal(
            [new Uri(ApiUrl), new Uri("https://api.github.com/repos/dasimon/remotedeck/releases/123")],
            _github.Requests.Select(r => r.Uri));
    }

    [Theory]
    [InlineData("http://api.github.com/repos/dasimon/remotedeck/releases/123")]
    [InlineData("https://example.com/releases/123")]
    [InlineData("https://github.com.evil.example/releases/123")]
    [InlineData("https://github.com:8443/releases/123")]
    [InlineData("//example.com/releases/123")]
    public async Task A_redirection_off_the_allowed_hosts_is_refused_without_being_followed(string location)
    {
        _github.Redirect(ApiUrl, location);

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);

        Assert.Equal([new Uri(ApiUrl)], _github.Requests.Select(r => r.Uri));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task At_most_five_redirections_are_followed(int redirections, bool followed)
    {
        var from = ApiUrl;
        for (var i = 1; i <= redirections; i++)
        {
            var to = $"{ApiUrl}/{i}";
            _github.Redirect(from, to);
            from = to;
        }
        _github.Text(from, ReleaseJson());

        if (followed)
            await GetLatestAsync();
        else
            await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);

        Assert.Equal(Math.Min(redirections, ReleaseClient.MaxRedirects) + 1, _github.Requests.Count);
    }

    [Fact]
    public async Task A_redirection_without_a_target_is_refused()
    {
        _github.Respond(ApiUrl, () => new HttpResponseMessage(HttpStatusCode.Found));

        await Assert.ThrowsAsync<UpdateException>(GetLatestAsync);
    }

    // --- Download ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("sums")]
    public async Task The_verified_executable_gets_its_final_name(string? digest)
    {
        var download = await DownloadAsync(Release(digest: digest is null ? null : ExeHash));

        Assert.Equal(Target, download.Path);
        Assert.Equal(ExeHash, download.Sha256);
        Assert.Equal(Exe, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".partial"));
        // SHA256SUMS first, then the executable through its redirection.
        Assert.Equal([new Uri(SumsUrl), new Uri(ExeUrl), new Uri(AssetUrl)], _github.Requests.Select(r => r.Uri));
    }

    [Fact]
    public async Task Progress_ends_at_one()
    {
        var reports = new List<double>();
        using var client = new ReleaseClient(_github, "0.0.1");

        await client.DownloadAsync(Release(), _directory, new SynchronousProgress(reports.Add), CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1]);
    }

    [Theory]
    [InlineData("https://example.com/SHA256SUMS", "sums")]
    [InlineData("http://github.com/dasimon/remotedeck/releases/download/v0.8.0/RemoteDeck.exe", "exe")]
    public async Task A_first_address_off_the_allowed_hosts_is_never_requested(string url, string which)
    {
        var release = which == "sums" ? Release(sums: url) : Release(exe: url);

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(release));

        Assert.DoesNotContain(_github.Requests, r => r.Uri == new Uri(url));
        if (which == "sums") Assert.Empty(_github.Requests);
        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_redirection_of_the_executable_off_the_allowed_hosts_is_refused()
    {
        _github.Redirect(ExeUrl, "https://codeload.github.com/RemoteDeck.exe");

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release()));

        AssertNothingOnDisk();
    }

    [Fact]
    public async Task SHA256SUMS_without_a_line_for_the_executable_is_refused_before_the_download()
    {
        _github.Bytes(SumsUrl, Encoding.ASCII.GetBytes($"{ExeHash} *RemoteDeck.exe.bak\n"));

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release()));

        Assert.Equal([new Uri(SumsUrl)], _github.Requests.Select(r => r.Uri));
        AssertNothingOnDisk();
    }

    [Fact]
    public async Task SHA256SUMS_one_byte_over_the_cap_is_refused()
    {
        var line = $"{ExeHash} *RemoteDeck.exe\n";
        _github.Bytes(SumsUrl, Encoding.ASCII.GetBytes(line + new string('\n', (int)ReleaseClient.MaxSumsBytes - line.Length + 1)));

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release()));

        Assert.Equal([new Uri(SumsUrl)], _github.Requests.Select(r => r.Uri));
    }

    [Fact]
    public async Task An_API_digest_that_differs_from_SHA256SUMS_is_refused_before_the_download()
    {
        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release(digest: new string('0', 64))));

        Assert.Equal([new Uri(SumsUrl)], _github.Requests.Select(r => r.Uri));
        AssertNothingOnDisk();
    }

    [Fact]
    public async Task An_executable_one_byte_over_its_announced_size_is_refused_and_deleted()
    {
        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release(size: Exe.Length - 1)));

        AssertNothingOnDisk();
    }

    [Fact]
    public async Task An_executable_shorter_than_its_announced_size_is_refused_and_deleted()
    {
        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release(size: Exe.Length + 1)));

        AssertNothingOnDisk();
    }

    [Fact]
    public async Task An_executable_whose_bytes_do_not_match_is_refused_and_deleted()
    {
        var tampered = (byte[])Exe.Clone();
        tampered[^1] ^= 1;
        _github.Bytes(AssetUrl, tampered);

        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release()));

        AssertNothingOnDisk();
    }

    [Fact]
    public async Task A_release_larger_than_the_cap_is_not_downloaded()
    {
        await Assert.ThrowsAsync<UpdateException>(() => DownloadAsync(Release(size: ReleaseClient.MaxExeBytes + 1)));

        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task A_cancelled_download_stays_a_cancellation_and_leaves_nothing()
    {
        using var cts = new CancellationTokenSource();
        using var client = new ReleaseClient(_github, "0.0.1");
        var progress = new SynchronousProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DownloadAsync(Release(), _directory, progress, cts.Token));

        AssertNothingOnDisk();
    }

    /// <summary>Reports on the calling thread, so that a test sees every report before the call returns.</summary>
    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Answers by exact address; anything not registered is a 404. Records what was asked.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Func<HttpResponseMessage>> _answers = [];

        public List<(Uri Uri, string UserAgent, string Accept)> Requests { get; } = [];

        public void Respond(string url, Func<HttpResponseMessage> answer) => _answers[new Uri(url)] = answer;

        public void Bytes(string url, byte[] body) =>
            Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

        public void Text(string url, string body) => Bytes(url, Encoding.UTF8.GetBytes(body));

        public void Redirect(string url, string location) => Respond(url, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        });

        public void Fail(string url, Exception failure) => Respond(url, () => throw failure);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add((uri, request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString()));
            return Task.FromResult(_answers.TryGetValue(uri, out var answer)
                ? answer()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
