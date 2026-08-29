using System.IO;

namespace RemoteDeck.App.Services;

/// <summary>
/// Append-only diagnostic log for lot-0 probes. Never receives secrets: callers log
/// outcomes and codes, not inputs.
/// </summary>
internal static class ProbeLog
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteDeck", "logs", "probe-l0.log");

    public static void Write(string probe, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{probe}] {message}";
        lock (Gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, line + Environment.NewLine);
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
