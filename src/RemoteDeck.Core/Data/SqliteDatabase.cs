using Microsoft.Data.Sqlite;

namespace RemoteDeck.Core.Data;

/// <summary>Location and connection policy for the local database: WAL journal, foreign keys on.</summary>
public sealed class SqliteDatabase(string path)
{
    public string Path { get; } = path;

    /// <summary>%APPDATA%\RemoteDeck\connections.db. The ACL is inherited from %APPDATA%, so SYSTEM and the local
    /// Administrators group can read the file as well. Local administrators are outside the threat model,
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
    /// <returns>The copy written before an upgrade, or <c>null</c> when nothing was upgraded.</returns>
    /// <exception cref="DatabaseBackupException">The copy could not be written; nothing was migrated.</exception>
    /// <exception cref="SchemaTooNewException">A newer build wrote the database; nothing was touched.</exception>
    public string? EnsureCreated()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var connection = Open();

        // Only an existing database that is behind. A new one has nothing to lose, a current one is
        // not migrated, and a newer one is refused by Migrate before anything is written.
        var version = SchemaMigrator.GetVersion(connection);
        string? backup = null;
        if (version > 0 && version < SchemaMigrator.CurrentVersion)
        {
            backup = BackUp(connection, version);
        }

        SchemaMigrator.Migrate(connection);
        return backup;
    }

    /// <summary>
    /// Copies the database as it is now to <c>connections.v&lt;version&gt;.bak</c>, beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SQLite's online backup rather than a file copy: in WAL mode the latest writes can still be in
    /// the <c>-wal</c> file, and copying the main file alone would silently leave them out.
    /// </para>
    /// <para>
    /// Written to a working file and moved into place only once complete, so a copy that fails half
    /// way never replaces a good one. The finished copy does replace an older one of the same version:
    /// it has to be the database as it was just before <em>this</em> migration. It holds the same
    /// DPAPI-protected secrets as the database, readable by the same Windows account only.
    /// </para>
    /// </remarks>
    private string BackUp(SqliteConnection connection, int version)
    {
        var target = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path) ?? "",
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}.v{version}.bak");
        var working = target + ".tmp";

        try
        {
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = working,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString()))
            {
                destination.Open();
                connection.BackupDatabase(destination);
                // The copy inherits WAL from the source, and a WAL file grows -wal and -shm companions
                // the moment it is opened. A backup is something to copy back by hand: one file.
                destination.Cmd("PRAGMA journal_mode = DELETE;").ExecuteNonQuery();
            }

            File.Move(working, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            TryDelete(working);
            throw new DatabaseBackupException(target, ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover working file is harmless, and the next attempt overwrites it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
