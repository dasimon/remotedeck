using System.Security;
using System.Security.Cryptography;

namespace RemoteDeck.Core.Updates;

/// <summary>How replacing the executable ended.</summary>
public enum SwapResult
{
    /// <summary>The target is now the downloaded file; the previous one sits next to it as <c>.old</c>.</summary>
    Done,

    /// <summary>The downloaded file does not have the expected hash; nothing was changed.</summary>
    HashMismatch,

    /// <summary>The file system refused a step; the target is the executable it was before.</summary>
    Failed,
}

/// <summary>Replaces the running executable by a downloaded one. The order of the steps is the
/// guarantee that the user is never left without a valid executable: the new file is copied and
/// verified beside the target first, and only then is the target renamed away and replaced.</summary>
public static class UpdateSwap
{
    private const string OldSuffix = ".old";
    private const string NewSuffix = ".new";

    /// <summary>Installs <paramref name="source"/> as <paramref name="target"/> when its SHA-256 equals
    /// <paramref name="expectedSha256"/> (hexadecimal, case-insensitive). The previous target is kept as
    /// <c>target.old</c>. On <see cref="SwapResult.HashMismatch"/> and <see cref="SwapResult.Failed"/> the
    /// target is left as it was.</summary>
    public static SwapResult Apply(string source, string expectedSha256, string target)
    {
        var staged = target + NewSuffix;
        var old = target + OldSuffix;

        // 1-2. Copy beside the target and hash the copy: it is the file that will be installed.
        try
        {
            File.Copy(source, staged, overwrite: true);
            if (!string.Equals(Sha256Of(staged), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuietly(staged);
                return SwapResult.HashMismatch;
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            DeleteQuietly(staged);
            return SwapResult.Failed;
        }

        // 3. Windows lets a running executable be renamed, not deleted or overwritten.
        try
        {
            File.Move(target, old, overwrite: true);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            DeleteQuietly(staged);
            return SwapResult.Failed;
        }

        // 4. Put the new file in place; if that fails, put the previous one back.
        try
        {
            File.Move(staged, target);
            return SwapResult.Done;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            try
            {
                File.Move(old, target);
                DeleteQuietly(staged);
            }
            catch (Exception restoreEx) when (IsFileSystemFailure(restoreEx))
            {
                // Nothing more can be done: both files stay on disk for a manual recovery.
            }

            return SwapResult.Failed;
        }
    }

    /// <summary>The SHA-256 of the file, in lower-case hexadecimal.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Removes what an update leaves behind: <c>target.old</c>, <c>target.new</c> and the work
    /// directory. A file still locked by an older instance is skipped; it goes at the next start.</summary>
    public static void CleanUp(string target, string workDirectory)
    {
        DeleteQuietly(target + OldSuffix);
        DeleteQuietly(target + NewSuffix);

        try
        {
            if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // Locked or already gone: the next start tries again.
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            // Locked or already gone: nothing to do.
        }
    }

    private static bool IsFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or SecurityException;
}
