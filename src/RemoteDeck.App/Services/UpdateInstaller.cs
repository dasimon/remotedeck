using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using RemoteDeck.Core.Updates;

namespace RemoteDeck.App.Services;

/// <summary>How putting a verified download in place of the running executable ended.</summary>
internal enum InstallOutcome
{
    /// <summary>The executable on disk is the new one; the running one is now <c>.old</c>.</summary>
    Installed,

    /// <summary>The folder needs elevation and the user refused it (or it could not be obtained);
    /// nothing was changed.</summary>
    ElevationRefused,

    /// <summary>The copy beside the executable does not have the verified hash; nothing was changed.</summary>
    HashMismatch,

    /// <summary>A step failed; the executable is the one that was there before (see the log for the
    /// rare case where restoring it failed too).</summary>
    Failed,
}

/// <summary>
/// The machine-facing half of the update: other instances, write access to the executable's folder,
/// the swap itself (in this process, or in an elevated copy of the installed executable) and the
/// fallback to the Downloads folder.
/// </summary>
/// <remarks>
/// The elevated process is always the installed executable (<see cref="Environment.ProcessPath"/>),
/// which sits in a folder the user cannot write to, never the downloaded file: elevation is granted
/// to code that is already trusted, which then checks the download's hash itself before using it.
/// </remarks>
internal static class UpdateInstaller
{
    private const string LogProbe = "update";

    /// <summary>Exit codes of the <c>--apply-update</c> mode, read back by <see cref="Install"/>.</summary>
    public const int ExitInstalled = 0;
    public const int ExitFailed = 1;
    public const int ExitHashMismatch = 2;
    public const int ExitBadArguments = 3;

    /// <summary>MOVEFILE_DELAY_UNTIL_REBOOT: Windows performs the move, here a deletion, at its next start.</summary>
    private const uint MoveFileDelayUntilReboot = 0x4;

    /// <summary>ERROR_CANCELLED: the user answered no to the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>How many other processes run this very executable. One whose path cannot be read
    /// (another user's, typically) is counted: it may be the same file, and replacing an executable
    /// someone else is running is what this check exists to prevent.</summary>
    public static int OtherInstancesOfThisExe()
    {
        var self = Environment.ProcessPath;
        using var current = Process.GetCurrentProcess();
        var count = 0;
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id) continue;
                try
                {
                    if (string.Equals(process.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase)) count++;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    count++;
                    ProbeLog.Write(LogProbe, $"process {process.Id}: path unreadable ({ex.GetType().Name}), counted as this executable");
                }
            }
        }

        return count;
    }

    /// <summary>True when this user can create a file in the executable's folder, which the swap
    /// needs (the staged copy, then the renames).</summary>
    public static bool CanWriteNextToExe()
    {
        if (Path.GetDirectoryName(Environment.ProcessPath) is not { } folder) return false;

        var probe = Path.Combine(folder, $".remotedeck-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>Puts <paramref name="download"/> in place of the running executable: in this process
    /// when its folder is writable, otherwise through an elevated <c>--apply-update</c> run of the
    /// installed executable, waited for. Either way the file is hashed again once copied beside the
    /// target, against <see cref="VerifiedDownload.Sha256"/>. Blocks: call it off the UI thread.</summary>
    public static InstallOutcome Install(VerifiedDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (Environment.ProcessPath is not { } target)
        {
            ProbeLog.Write(LogProbe, "install: the executable's own path is unknown");
            return InstallOutcome.Failed;
        }

        if (CanWriteNextToExe())
        {
            ProbeLog.Write(LogProbe, $"install: {Path.GetDirectoryName(target)} is writable, swapping in this process");
            return SwapAndLog(download.Path, download.Sha256, target) switch
            {
                SwapResult.Done => InstallOutcome.Installed,
                SwapResult.HashMismatch => InstallOutcome.HashMismatch,
                _ => InstallOutcome.Failed,
            };
        }

        // The elevated step writes a log of its own, named here so that this one leads to it.
        ProbeLog.Write(LogProbe, $"install: {Path.GetDirectoryName(target)} is not writable, asking for elevation; "
            + $"the elevated step logs to {ProbeLog.ApplyUpdatePath}");
        var start = new ProcessStartInfo(target)
        {
            Verb = "runas",
            UseShellExecute = true,
            Arguments = ApplyUpdateArguments.Format(download.Path, download.Sha256),
        };

        int exitCode;
        try
        {
            using var elevated = Process.Start(start);
            if (elevated is null)
            {
                ProbeLog.Write(LogProbe, "install: the elevated process did not start");
                return InstallOutcome.Failed;
            }

            elevated.WaitForExit();
            exitCode = elevated.ExitCode;
        }
        catch (Win32Exception ex)
        {
            // Cancelled is the user's no; any other code is an elevation that could not be had
            // (policy, no administrator account), which the spec handles the same way.
            ProbeLog.Write(LogProbe, ex.NativeErrorCode == ErrorCancelled
                ? "install: elevation refused by the user"
                : $"install: elevation failed, error {ex.NativeErrorCode}: {ex.Message}");
            return InstallOutcome.ElevationRefused;
        }

        ProbeLog.Write(LogProbe, $"install: elevated process exited with {exitCode}");
        return exitCode switch
        {
            ExitInstalled => InstallOutcome.Installed,
            ExitHashMismatch => InstallOutcome.HashMismatch,
            _ => InstallOutcome.Failed,
        };
    }

    /// <summary>The swap, with its outcome in the log. When it failed and the target is gone (the
    /// restore failed too), the paths of what is left are logged for a manual recovery.</summary>
    public static SwapResult SwapAndLog(string source, string expectedSha256, string target)
    {
        var result = UpdateSwap.Apply(source, expectedSha256, target);
        switch (result)
        {
            case SwapResult.Done:
                ProbeLog.Write(LogProbe, $"swap: {target} replaced, previous kept as .old");
                break;
            case SwapResult.HashMismatch:
                ProbeLog.Write(LogProbe, $"swap: the copy of {source} does not have hash {expectedSha256}; nothing changed");
                break;
            default:
                if (File.Exists(target))
                {
                    ProbeLog.Write(LogProbe, $"swap failed; {target} is still in place");
                }
                else
                {
                    ProbeLog.Write(LogProbe, $"swap failed and {target} could not be restored; "
                        + $"previous executable: {target}.old (exists: {File.Exists(target + ".old")}), "
                        + $"new one: {target}.new (exists: {File.Exists(target + ".new")})");
                }

                break;
        }

        return result;
    }

    /// <summary>
    /// Has Windows delete <c>target.old</c> at its next restart. For the elevated step, after a
    /// successful swap: in a protected folder the clean-up at the next launch runs as the user and is
    /// refused, and the file cannot be deleted now, since both RemoteDeck processes still run from it.
    /// Windows records the request in the machine's registry, which takes administrator rights. A
    /// failure is logged and nothing more: the new executable is already in place.
    /// </summary>
    public static void DeleteOldAtReboot(string target)
    {
        var old = target + ".old";
        if (MoveFileEx(old, null, MoveFileDelayUntilReboot))
        {
            ProbeLog.Write(LogProbe, $"swap: {old} will be deleted at the next Windows restart");
        }
        else
        {
            ProbeLog.Write(LogProbe, $"swap: {old} could not be scheduled for deletion at restart "
                + $"(error {Marshal.GetLastPInvokeError()}); it stays until the next update replaces it");
        }
    }

    /// <summary>The exit code of the <c>--apply-update</c> mode for a swap result.</summary>
    public static int ExitCodeOf(SwapResult result) => result switch
    {
        SwapResult.Done => ExitInstalled,
        SwapResult.HashMismatch => ExitHashMismatch,
        _ => ExitFailed,
    };

    /// <summary>Copies the verified executable to the user's Downloads folder as
    /// <c>RemoteDeck-&lt;version&gt;.exe</c>, checks the copy's hash, and shows it in Explorer.</summary>
    /// <returns>The path of the copy.</returns>
    /// <exception cref="UpdateException">The Downloads folder is unknown or the copy is not the verified file.</exception>
    /// <exception cref="IOException">The copy failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The copy was refused.</exception>
    public static string SaveToDownloads(VerifiedDownload download, ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(download);

        var folderId = DownloadsFolderId;
        var hr = SHGetKnownFolderPath(ref folderId, 0, 0, out var downloads);
        if (hr != 0 || string.IsNullOrEmpty(downloads))
            throw new UpdateException($"the Downloads folder could not be found (0x{hr:X8})");

        var destination = Path.Combine(downloads, $"RemoteDeck-{version}.exe");
        File.Copy(download.Path, destination, overwrite: true);
        if (!string.Equals(UpdateSwap.Sha256Of(destination), download.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new UpdateException($"the copy in {downloads} does not have the verified hash");
        }

        ProbeLog.Write(LogProbe, $"saved {version} to {destination}");
        try
        {
            // Explorer by its full path: a bare name would be looked up through the search path.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(explorer, $"/select,\"{destination}\"");
        }
        catch (Win32Exception ex)
        {
            // The file is there and the message names it; not opening the folder is no failure.
            ProbeLog.Write(LogProbe, $"could not open Explorer on {destination}: {ex.Message}");
        }

        return destination;
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token, out string? path);
}
