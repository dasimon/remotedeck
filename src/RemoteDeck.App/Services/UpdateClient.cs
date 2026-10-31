using System.IO;
using System.Net;
using System.Net.Http;
using RemoteDeck.Core.Updates;

namespace RemoteDeck.App.Services;

/// <summary>
/// The update check and download as this build performs them: the repository and version it was
/// stamped with (<see cref="BuildInfo"/>), the system proxy, <c>%LOCALAPPDATA%</c> for the files and
/// <see cref="ProbeLog"/> for what happened. What is accepted from the network is decided by
/// <see cref="ReleaseClient"/>.
/// </summary>
/// <remarks>
/// Failures are <see cref="UpdateException"/>, for the caller to log; a cancellation through the
/// caller's token stays an <see cref="OperationCanceledException"/>.
/// </remarks>
internal sealed class UpdateClient : IDisposable
{
    private const string LogProbe = "update";

    private readonly ReleaseClient _releases = new(
        // Redirections are followed by ReleaseClient, so that each hop goes through the host check.
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            AutomaticDecompression = DecompressionMethods.All,
        },
        BuildInfo.Version?.ToString() ?? "unknown");

    /// <summary>Where downloads go, one folder per version: <c>%LOCALAPPDATA%\RemoteDeck\update</c>.</summary>
    public static string WorkDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDeck", "update");

    /// <summary>The latest release when it is newer than the running build; null when it is not, and
    /// without any request when this build cannot update (<see cref="BuildInfo.CanUpdate"/>).</summary>
    /// <exception cref="UpdateException">GitHub could not be reached, answered with an error, or
    /// answered something that is not a release this build would download.</exception>
    public async Task<LatestRelease?> CheckAsync(CancellationToken ct)
    {
        if (BuildInfo.UpdateRepository is not { } repository || BuildInfo.Version is not { } running) return null;

        var release = await _releases.GetLatestAsync(repository, ct).ConfigureAwait(false);
        ProbeLog.Write(LogProbe, $"latest release of {repository} is {release.Version}, running {running}");
        return release.Version > running ? release : null;
    }

    /// <summary>Downloads the executable of <paramref name="release"/> to
    /// <c>%LOCALAPPDATA%\RemoteDeck\update\&lt;version&gt;\RemoteDeck.exe</c>, checked against
    /// <c>SHA256SUMS</c> and the API's digest.</summary>
    /// <param name="progress">The fraction of the executable received, from 0 to 1.</param>
    /// <exception cref="UpdateException">The download failed or the file is not the one the release
    /// describes; nothing is left on disk.</exception>
    public async Task<VerifiedDownload> DownloadAsync(LatestRelease release, IProgress<double> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);

        var directory = Path.Combine(WorkDirectory, release.Version.ToString());
        var download = await _releases.DownloadAsync(release, directory, progress, ct).ConfigureAwait(false);
        ProbeLog.Write(LogProbe, $"downloaded {release.Version}: {release.ExeSize} bytes, sha256 {download.Sha256}");
        return download;
    }

    public void Dispose() => _releases.Dispose();
}
