using RemoteDeck.Core.Data;

namespace RemoteDeck.Core.Tests.Data;

/// <summary>One throwaway SQLite file per test. Pooling is off so the file can be deleted on Windows.</summary>
internal sealed class TempDatabase : IDisposable
{
    public SqliteDatabase Db { get; }
    public string Path { get; }

    public TempDatabase()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"remotedeck-test-{Guid.NewGuid():N}.db");
        Db = new SqliteDatabase(Path);
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            DeleteWithRetry(Path + suffix);
        }

        // The copies a migration takes of it (connections.v1.bak and the like): any test that
        // upgrades an old database writes one, whether or not it is about backups.
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        foreach (var copy in Directory.GetFiles(dir, System.IO.Path.GetFileNameWithoutExtension(Path) + ".v*.bak*"))
        {
            DeleteWithRetry(copy);
        }
    }

    /// <summary>
    /// A file just closed can still be held for a moment by something outside the test — seen as an
    /// intermittent "being used by another process" on a test whose own connections were all
    /// disposed, typically an antivirus scanning the fresh file. A short retry, not a failure.
    /// </summary>
    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}
