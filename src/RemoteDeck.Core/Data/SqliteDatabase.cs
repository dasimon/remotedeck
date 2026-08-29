using Microsoft.Data.Sqlite;

namespace RemoteDeck.Core.Data;

/// <summary>Location and connection policy for the local database (spec §4): WAL journal, foreign keys on.</summary>
public sealed class SqliteDatabase(string path)
{
    public string Path { get; } = path;

    /// <summary>%APPDATA%\RemoteDeck\connections.db. The ACL is inherited from %APPDATA%, so SYSTEM and the local
    /// Administrators group can read the file as well. Local administrators are outside the threat model (spec §5.4),
    /// and the secret blobs stay protected by DPAPI CurrentUser regardless.</summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDeck", "connections.db");

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
    }.ToString();

    /// <summary>Opens a connection with the per-connection PRAGMAs applied. Caller disposes.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        connection.Cmd("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;").ExecuteNonQuery();
        return connection;
    }

    /// <summary>Creates the directory and file if needed and brings the schema to the current version.</summary>
    public void EnsureCreated()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var connection = Open();
        SchemaMigrator.Migrate(connection);
    }
}
