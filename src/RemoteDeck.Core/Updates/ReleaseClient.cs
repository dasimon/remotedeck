using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace RemoteDeck.Core.Updates;

/// <summary>An executable that was downloaded and matched every hash the release publishes.</summary>
/// <param name="Path">Where it was put: <c>RemoteDeck.exe</c> in the directory given to the download.</param>
/// <param name="Sha256">Its SHA-256, lower-case hexadecimal: what the swap checks again.</param>
public sealed record VerifiedDownload(string Path, string Sha256);

/// <summary>Why a check or a download failed (network, HTTP status, refused address, content). The
/// message is meant for the log and the About window; it never contains a full download address.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// The network half of the update: reads GitHub's latest release and downloads its executable. This
/// is what decides what gets installed, so it accepts little: https on the GitHub hosts of
/// <see cref="UpdateHosts"/> only, checked before the first request to any address and before every
/// redirection, which are followed here rather than by the handler; bounded sizes; and an executable
/// whose hash is the one <c>SHA256SUMS</c> lists, and the one the API reports when it reports one.
/// </summary>
/// <remarks>
/// The handler is a parameter so that tests can stand in for GitHub; the application passes one that
/// does not follow redirections itself. Failures are <see cref="UpdateException"/>; a cancellation
/// through the caller's token stays an <see cref="OperationCanceledException"/>. Nothing partly
/// downloaded is left behind either way.
/// </remarks>
public sealed class ReleaseClient : IDisposable
{
    public const string ExeName = "RemoteDeck.exe";
    public const int MaxRedirects = 5;
    public const long MaxApiBytes = 1024 * 1024;
    public const long MaxSumsBytes = 4 * 1024;
    public const long MaxExeBytes = 300L * 1024 * 1024;

    private const string PartialSuffix = ".partial";
    private const string ApiMediaType = "application/vnd.github+json";
    private const int BufferSize = 81920;

    /// <summary>For the API answer and <c>SHA256SUMS</c>; the executable has no overall limit, only
    /// the caller's cancellation.</summary>
    private static readonly TimeSpan SmallRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    /// <param name="handler">Owned and disposed by this client. It must not follow redirections.</param>
    /// <param name="version">The running version, sent as <c>User-Agent: RemoteDeck/&lt;version&gt;</c>
    /// (GitHub's API refuses requests without one).</param>
    /// <exception cref="ArgumentException"><paramref name="handler"/> is a <see cref="SocketsHttpHandler"/>
    /// or an <see cref="HttpClientHandler"/> that follows redirections itself, which would take each hop
    /// past the host check.</exception>
    public ReleaseClient(HttpMessageHandler handler, string version)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is SocketsHttpHandler { AllowAutoRedirect: true } or HttpClientHandler { AllowAutoRedirect: true })
            throw new ArgumentException("The handler follows redirections itself; set AllowAutoRedirect to false.", nameof(handler));

        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemoteDeck", version));
    }

    /// <summary>The latest release of <paramref name="repository"/> ("owner/name"), whatever its version.</summary>
    /// <exception cref="UpdateException">GitHub could not be reached, answered with an error, or
    /// answered something that is not a release this client would download.</exception>
    public async Task<LatestRelease> GetLatestAsync(string repository, CancellationToken ct)
    {
        try
        {
            var api = new Uri($"https://api.github.com/repos/{repository}/releases/latest");
            var json = await ReadSmallAsync(api, MaxApiBytes, ApiMediaType, ct).ConfigureAwait(false);
            // A captive portal or a proxy answering 200 with a web page ends here.
            var release = LatestRelease.Parse(Encoding.UTF8.GetString(json))
                ?? throw new UpdateException("GitHub's answer is not a usable release");
            // Refused now rather than after the user has clicked "Update".
            if (!UpdateHosts.IsAllowed(release.ExeUrl) || !UpdateHosts.IsAllowed(release.SumsUrl))
                throw new UpdateException("the release's files are not on an allowed host");
            if (release.ExeSize > MaxExeBytes)
                throw new UpdateException($"the release announces {release.ExeSize} bytes, more than {MaxExeBytes}");
            return release;
        }
        catch (Exception ex) when (Translate(ex, ct) is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>Downloads <c>SHA256SUMS</c>, then the executable, to
    /// <c>&lt;directory&gt;\RemoteDeck.exe</c> (the directory is created). The file only gets that name
    /// once its size and hash have been checked; until then it is <c>RemoteDeck.exe.partial</c>.</summary>
    /// <param name="progress">The fraction of the executable received, from 0 to 1.</param>
    /// <exception cref="UpdateException">The download failed or the file is not the one the release
    /// describes; nothing is left on disk.</exception>
    public async Task<VerifiedDownload> DownloadAsync(
        LatestRelease release, string directory, IProgress<double> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(progress);

        var target = Path.Combine(directory, ExeName);
        var partial = target + PartialSuffix;
        try
        {
            var sha256 = await DownloadVerifiedAsync(release, directory, partial, progress, ct).ConfigureAwait(false);
            File.Move(partial, target, overwrite: true);
            return new VerifiedDownload(target, sha256);
        }
        catch (Exception ex)
        {
            DeleteQuietly(partial);
            if (Translate(ex, ct) is { } failure) throw failure;
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Writes the executable to <paramref name="partial"/> and returns its hash once it has
    /// the announced size and the hash of <c>SHA256SUMS</c> (and of the API, when given).</summary>
    private async Task<string> DownloadVerifiedAsync(
        LatestRelease release, string directory, string partial, IProgress<double> progress, CancellationToken ct)
    {
        if (release.ExeSize > MaxExeBytes)
            throw new UpdateException($"the release announces {release.ExeSize} bytes, more than {MaxExeBytes}");

        // The expected hash first: there is no point in downloading a file that cannot be checked.
        var sums = await ReadSmallAsync(release.SumsUrl, MaxSumsBytes, accept: null, ct).ConfigureAwait(false);
        var expected = Sha256Sums.Find(Encoding.UTF8.GetString(sums), ExeName)
            ?? throw new UpdateException($"SHA256SUMS does not list {ExeName}");
        // No digest from the API is not an error; a different one is.
        if (release.ExeDigest is { } digest && !string.Equals(digest, expected, StringComparison.Ordinal))
            throw new UpdateException($"SHA256SUMS and the GitHub API give different hashes for {ExeName}");

        Directory.CreateDirectory(directory);
        long received;
        using (var response = await SendAsync(release.ExeUrl, accept: null, ct).ConfigureAwait(false))
        {
            var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                await using (file.ConfigureAwait(false))
                {
                    var percent = -1;
                    // Reading past the announced size already means a wrong file; that size is itself
                    // under MaxExeBytes, so this also caps what reaches the disk.
                    received = await CopyCappedAsync(source, file, release.ExeSize, total =>
                    {
                        var now = (int)(total * 100 / release.ExeSize);
                        if (now == percent) return;
                        percent = now;
                        progress.Report(total / (double)release.ExeSize);
                    }, ct).ConfigureAwait(false);
                }
            }
        }

        if (received != release.ExeSize)
            throw new UpdateException($"received {received} bytes, the release announces {release.ExeSize}");

        // Hashed from the disk once closed: the file that will be installed, not the bytes in transit.
        var actual = UpdateSwap.Sha256Of(partial);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new UpdateException($"the downloaded {ExeName} does not have the hash SHA256SUMS lists");
        return actual;
    }

    /// <summary>A small download held in memory, under a size cap and <see cref="SmallRequestTimeout"/>.</summary>
    private async Task<byte[]> ReadSmallAsync(Uri uri, long maxBytes, string? accept, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SmallRequestTimeout);

        using var response = await SendAsync(uri, accept, timeout.Token).ConfigureAwait(false);
        var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await CopyCappedAsync(source, buffer, maxBytes, onProgress: null, timeout.Token).ConfigureAwait(false);
            return buffer.ToArray();
        }
    }

    /// <summary>A GET that follows at most <see cref="MaxRedirects"/> redirections itself, each target
    /// resolved against the address that sent it and checked against <see cref="UpdateHosts"/> before
    /// any request is made to it. Returns a successful response, headers read, body not yet.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, string? accept, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            if (!UpdateHosts.IsAllowed(uri)) throw new UpdateException($"refused address {Describe(uri)}");

            HttpResponseMessage response;
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                if (accept is not null) request.Headers.Accept.ParseAdd(accept);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }

            if (!IsRedirect(response.StatusCode))
            {
                if (response.IsSuccessStatusCode) return response;
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new UpdateException($"HTTP {status} from {Describe(uri)}");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new UpdateException($"redirection without a target from {Describe(uri)}");
            if (hop == MaxRedirects) throw new UpdateException($"more than {MaxRedirects} redirections");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    /// <summary>Copies at most <paramref name="maxBytes"/>; one byte more and the copy stops with an
    /// <see cref="UpdateException"/>. Counted after decompression, so a small compressed answer cannot
    /// expand past the cap.</summary>
    private static async Task<long> CopyCappedAsync(
        Stream source, Stream destination, long maxBytes, Action<long>? onProgress, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new UpdateException($"answer larger than {maxBytes} bytes");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            onProgress?.Invoke(total);
        }

        return total;
    }

    /// <summary>The failure to report for <paramref name="ex"/>, or null when it must propagate as is:
    /// an <see cref="UpdateException"/> already, a cancellation the caller asked for, or a bug. The
    /// innermost message is the one that names the cause (DNS, TLS, proxy, disk).</summary>
    private static UpdateException? Translate(Exception ex, CancellationToken ct) => ex switch
    {
        UpdateException => null,
        // Only the caller's token is a cancellation; the other one is SmallRequestTimeout.
        OperationCanceledException when !ct.IsCancellationRequested =>
            new UpdateException($"GitHub did not answer within {SmallRequestTimeout.TotalSeconds:0} seconds"),
        HttpRequestException => new UpdateException($"network error: {ex.GetBaseException().Message}"),
        // A connection cut while reading the body, a full disk, a locked or forbidden file.
        IOException or UnauthorizedAccessException =>
            new UpdateException($"download failed: {ex.GetBaseException().Message}"),
        _ => null,
    };

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Scheme, host and port only: release download addresses carry signed query strings that
    /// have nothing to do in a log.</summary>
    private static string Describe(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or already gone: the work directory is cleared at the next start anyway.
        }
    }
}
