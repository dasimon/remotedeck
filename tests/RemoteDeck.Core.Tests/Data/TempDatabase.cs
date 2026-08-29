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
            var f = Path + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
