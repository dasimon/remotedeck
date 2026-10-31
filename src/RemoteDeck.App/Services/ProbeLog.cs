using System.IO;

namespace RemoteDeck.App.Services;

/// <summary>
/// Append-only diagnostic log for lot-0 probes. Never receives secrets: callers log
/// outcomes and codes, not inputs.
/// </summary>
/// <remarks>
/// <para>
/// One writer, kept open, flushed on every line. The first version opened and closed the file
/// for each line — <c>File.AppendAllText</c> under a lock, on whichever thread called, usually
/// the UI one — which is one file open per log line for the life of the process. The file is
/// shared for reading, so a viewer can follow it while RemoteDeck writes.
/// </para>
/// <para>
/// Bounded. Measured on the reference client on 2026-09-05: 586 KB and 1,245 lines with no
/// upper limit anywhere. Past <see cref="RollAt"/> the file is moved aside as <c>.1</c>, replacing
/// the previous <c>.1</c>, so the disk holds at most two of them. What the lines contain is
/// stated in <c>SECURITY.md</c>: connection names, hosts and user names, never a secret.
/// </para>
/// </remarks>
internal static class ProbeLog
{
    /// <summary>Size past which the file is rolled. Generous for a log that grows by a few hundred
    /// lines a day, and small enough to read in one sitting.</summary>
    private const long RollAt = 1024 * 1024;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    private static readonly string Folder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDeck", "logs");

    /// <summary>The file this process writes to: <c>probe-l0.log</c>, or <see cref="ApplyUpdatePath"/>
    /// once <see cref="UseApplyUpdateLog"/> has been called.</summary>
    public static string Path { get; private set; } = System.IO.Path.Combine(Folder, "probe-l0.log");

    /// <summary>
    /// The log of the elevated <c>--apply-update</c> step, beside the main one. A file of its own
    /// because that step runs while the RemoteDeck that started it waits with <c>probe-l0.log</c>
    /// open: two processes appending to one file each write at their own position, and the
    /// waiting one's next line lands on top of the elevated one's (measured). For an elevation by
    /// another administrator account, it is in that account's application data.
    /// </summary>
    public static string ApplyUpdatePath { get; } = System.IO.Path.Combine(Folder, "probe-l0-apply-update.log");

    /// <summary>Where the previous log goes when the current one rolls.</summary>
    public static string PreviousPath => Path + ".1";

    /// <summary>Sends every later line to <see cref="ApplyUpdatePath"/>. Called by the
    /// <c>--apply-update</c> mode before it writes anything.</summary>
    public static void UseApplyUpdateLog()
    {
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
            Path = ApplyUpdatePath;
        }
    }

    public static void Write(string probe, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{probe}] {message}";
        lock (Gate)
        {
            // Never throws: callers write from connect and catch paths, and a log that cannot be
            // written must not become the error the user sees in place of the real one.
            try
            {
                var writer = _writer ??= Open();
                if (writer.BaseStream.Length > RollAt)
                {
                    writer = _writer = Roll(writer);
                }

                writer.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writer?.Dispose();
                _writer = null;
                System.Diagnostics.Debug.WriteLine($"[probe-log] write failed: {ex.Message}");
            }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    /// <summary>
    /// Moves the file aside and starts a new one. A move that fails — a viewer holding the file
    /// without delete sharing — leaves the log growing past the limit until the next line tries
    /// again, rather than losing every line after it.
    /// </summary>
    private static StreamWriter Roll(StreamWriter writer)
    {
        writer.Dispose();
        try
        {
            File.Move(Path, PreviousPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[probe-log] roll failed, still appending: {ex.Message}");
        }

        return Open();
    }

    private static StreamWriter Open()
    {
        Directory.CreateDirectory(Folder);
        var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream) { AutoFlush = true };
    }
}
