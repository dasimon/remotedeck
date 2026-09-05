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

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteDeck", "logs", "probe-l0.log");

    /// <summary>Where the previous log goes when the current one rolls.</summary>
    public static string PreviousPath { get; } = Path + ".1";

    public static void Write(string probe, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{probe}] {message}";
        lock (Gate)
        {
            var writer = _writer ??= Open();
            if (writer.BaseStream.Length > RollAt)
            {
                writer.Dispose();
                _writer = null;
                File.Move(Path, PreviousPath, overwrite: true);
                writer = _writer = Open();
            }

            writer.WriteLine(line);
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    private static StreamWriter Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream) { AutoFlush = true };
    }
}
