using System.IO;
using System.Net.Http;
using RemoteDeck.Core.Updates;

namespace RemoteDeck.App.Services;

/// <summary>What the coordinator is doing; anything but <see cref="Idle"/> makes it busy.</summary>
internal enum UpdatePhase
{
    Idle,
    Checking,
    Downloading,

    /// <summary>Every byte is in; the file is being hashed and checked.</summary>
    Verifying,

    /// <summary>The download is verified: the sessions confirmation, then the swap.</summary>
    Installing,

    /// <summary>Installed; the main window is closing and the new executable starts after it.</summary>
    Restarting,
}

/// <summary>How a check ended.</summary>
internal enum CheckResult
{
    /// <summary>The running version is the latest one.</summary>
    UpToDate,

    /// <summary>A newer release is in <see cref="UpdateCoordinator.Available"/>.</summary>
    Available,

    /// <summary>GitHub could not be reached or answered something unusable; see the reason.</summary>
    Failed,

    /// <summary>A development build: no channel or no plain version, so nothing is ever asked.</summary>
    DevBuild,

    /// <summary>Another check or an update was running; nothing was done.</summary>
    Busy,
}

/// <param name="Result">What happened.</param>
/// <param name="Reason">For <see cref="CheckResult.Failed"/>: why, in English, for the log and the
/// About window (no download address). Null otherwise.</param>
internal sealed record CheckOutcome(CheckResult Result, string? Reason = null);

/// <summary>How an update attempt ended.</summary>
internal enum UpdateResult
{
    /// <summary>Installed: the main window is closing and the new version starts after it.</summary>
    Restarting,

    /// <summary>Another process runs this executable; nothing was downloaded or nothing was installed.</summary>
    OtherInstances,

    /// <summary>The caller's token was cancelled; nothing was changed.</summary>
    Cancelled,

    /// <summary>The user did not confirm closing the open sessions; the download was deleted.</summary>
    Declined,

    /// <summary>The download or its verification failed; see the reason. Nothing was changed.</summary>
    DownloadFailed,

    /// <summary>The copy beside the executable did not have the verified hash; nothing was changed.</summary>
    HashMismatch,

    /// <summary>Installing failed; see the reason. The executable is the one that was there before.</summary>
    Failed,

    /// <summary>Elevation was refused: the verified executable is in Downloads, at the detail path.</summary>
    SavedToDownloads,

    /// <summary>Another check or an update was running; nothing was done.</summary>
    Busy,

    /// <summary>No release is available (<see cref="UpdateCoordinator.Available"/> is null).</summary>
    NothingAvailable,
}

/// <param name="Result">What happened.</param>
/// <param name="Detail">The reason, in English, for <see cref="UpdateResult.DownloadFailed"/> and
/// <see cref="UpdateResult.Failed"/>; the saved file's path for <see cref="UpdateResult.SavedToDownloads"/>;
/// null otherwise.</param>
internal sealed record UpdateOutcome(UpdateResult Result, string? Detail = null);

/// <summary>
/// One update at a time, from the check to the restart. Owned by the shell window and used from the
/// UI thread only: <see cref="Changed"/> is raised there, and the single-flight rule (<see cref="IsBusy"/>)
/// relies on it. A check or an update started while another runs returns <c>Busy</c> at once.
/// </summary>
/// <remarks>
/// Order of an update, from the spec: other instances; download and verify; other instances again;
/// the sessions confirmation; install; restart. The sessions are only at stake once everything that
/// can fail without touching them has succeeded.
/// </remarks>
internal sealed class UpdateCoordinator : IDisposable
{
    private const string LogProbe = "update";

    private readonly UpdateClient _client = new();
    private readonly Action _closeMainWindow;

    /// <param name="closeMainWindow">Closes the main window, which ends the application through its
    /// normal close (settings, last session, graceful close of the sessions). Called once, after a
    /// successful install.</param>
    public UpdateCoordinator(Action closeMainWindow)
    {
        ArgumentNullException.ThrowIfNull(closeMainWindow);
        _closeMainWindow = closeMainWindow;
    }

    /// <summary>Raised on the UI thread whenever <see cref="Phase"/> or <see cref="Available"/> changes.</summary>
    public event Action? Changed;

    public UpdatePhase Phase { get; private set; }

    public bool IsBusy => Phase != UpdatePhase.Idle;

    /// <summary>The newer release found by the last successful check; null when there is none.</summary>
    public LatestRelease? Available { get; private set; }

    /// <summary>Asks GitHub for the latest release. Never throws for a network or content failure:
    /// an automatic check (<paramref name="manual"/> false) only logs it, a manual one also returns
    /// the reason for the About window. Either way the outcome is returned.</summary>
    public async Task<CheckOutcome> CheckAsync(bool manual)
    {
        var kind = manual ? "manual" : "automatic";
        if (!BuildInfo.CanUpdate) return new CheckOutcome(CheckResult.DevBuild);
        if (IsBusy)
        {
            ProbeLog.Write(LogProbe, $"{kind} check skipped: {Phase} in progress");
            return new CheckOutcome(CheckResult.Busy);
        }

        SetPhase(UpdatePhase.Checking);
        try
        {
            var release = await _client.CheckAsync(CancellationToken.None);
            Available = release;
            ProbeLog.Write(LogProbe, release is null
                ? $"{kind} check: up to date"
                : $"{kind} check: {release.Version} available");
            return new CheckOutcome(release is null ? CheckResult.UpToDate : CheckResult.Available);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException)
        {
            // UpdateClient already turns network failures into UpdateException; the other two are
            // a second net, so that a check can never take the application down.
            ProbeLog.Write(LogProbe, $"{kind} check failed: {ex.Message}");
            return new CheckOutcome(CheckResult.Failed, ex.Message);
        }
        finally
        {
            SetPhase(UpdatePhase.Idle);
        }
    }

    /// <summary>Downloads, verifies and installs <see cref="Available"/>, then restarts on it.</summary>
    /// <param name="confirmSessions">Asked once the download is verified, just before installing:
    /// true to go on. It must return true without asking when no session is open.</param>
    /// <param name="progress">The fraction of the executable received, 0 to 1, on the UI thread. When
    /// it reaches 1 the phase becomes <see cref="UpdatePhase.Verifying"/>.</param>
    /// <param name="ct">Cancels the download; once the download is verified, it is only looked at once
    /// more, before the sessions confirmation. The install itself is never interrupted.</param>
    public async Task<UpdateOutcome> UpdateAsync(Func<bool> confirmSessions, IProgress<double> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmSessions);
        ArgumentNullException.ThrowIfNull(progress);

        if (IsBusy)
        {
            ProbeLog.Write(LogProbe, $"update skipped: {Phase} in progress");
            return new UpdateOutcome(UpdateResult.Busy);
        }

        if (Available is not { } release) return new UpdateOutcome(UpdateResult.NothingAvailable);

        SetPhase(UpdatePhase.Downloading);
        VerifiedDownload? download = null;
        var restarting = false;
        try
        {
            if (RefusedForOtherInstances("before downloading")) return new UpdateOutcome(UpdateResult.OtherInstances);

            // Created here, on the UI thread, so that its reports come back here. A report can arrive
            // after the download has returned: only Downloading moves to Verifying.
            var received = new Progress<double>(fraction =>
            {
                progress.Report(fraction);
                if (fraction >= 1.0 && Phase == UpdatePhase.Downloading) SetPhase(UpdatePhase.Verifying);
            });

            ProbeLog.Write(LogProbe, $"downloading {release.Version}");
            try
            {
                download = await _client.DownloadAsync(release, received, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ProbeLog.Write(LogProbe, "download cancelled");
                return new UpdateOutcome(UpdateResult.Cancelled);
            }
            catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException)
            {
                ProbeLog.Write(LogProbe, $"download failed: {ex.Message}");
                return new UpdateOutcome(UpdateResult.DownloadFailed, ex.Message);
            }

            SetPhase(UpdatePhase.Installing);
            if (RefusedForOtherInstances("after downloading")) return new UpdateOutcome(UpdateResult.OtherInstances);
            if (ct.IsCancellationRequested)
            {
                ProbeLog.Write(LogProbe, "update cancelled after the download");
                return new UpdateOutcome(UpdateResult.Cancelled);
            }

            if (!confirmSessions())
            {
                ProbeLog.Write(LogProbe, "update declined: the open sessions were not to be closed");
                return new UpdateOutcome(UpdateResult.Declined);
            }

            // Off the UI thread: the swap hashes the whole file, and the elevated route waits for a process.
            var outcome = await Task.Run(() => UpdateInstaller.Install(download));
            ProbeLog.Write(LogProbe, $"install of {release.Version}: {outcome}");
            switch (outcome)
            {
                case InstallOutcome.Installed:
                    restarting = true;
                    SetPhase(UpdatePhase.Restarting);
                    App.Current.RestartAfterExit(Environment.ProcessPath!);
                    _closeMainWindow();
                    return new UpdateOutcome(UpdateResult.Restarting);

                case InstallOutcome.ElevationRefused:
                    try
                    {
                        var saved = UpdateInstaller.SaveToDownloads(download, release.Version);
                        return new UpdateOutcome(UpdateResult.SavedToDownloads, saved);
                    }
                    catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
                    {
                        ProbeLog.Write(LogProbe, $"saving to Downloads failed: {ex.Message}");
                        return new UpdateOutcome(UpdateResult.Failed, ex.Message);
                    }

                case InstallOutcome.HashMismatch:
                    return new UpdateOutcome(UpdateResult.HashMismatch);

                default:
                    return new UpdateOutcome(UpdateResult.Failed, "the executable could not be replaced");
            }
        }
        finally
        {
            // Whatever happened, the downloaded file has served: installed, copied to Downloads, or
            // not to be used. Its folder goes at the next start (UpdateSwap.CleanUp).
            if (download is not null) DeleteQuietly(download.Path);
            if (!restarting) SetPhase(UpdatePhase.Idle);
        }
    }

    public void Dispose() => _client.Dispose();

    private static bool RefusedForOtherInstances(string when)
    {
        var others = UpdateInstaller.OtherInstancesOfThisExe();
        if (others == 0) return false;
        ProbeLog.Write(LogProbe, $"update refused {when}: {others} other instance(s) of this executable running");
        return true;
    }

    private void SetPhase(UpdatePhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        Changed?.Invoke();
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProbeLog.Write(LogProbe, $"could not delete {path}: {ex.Message}");
        }
    }
}
