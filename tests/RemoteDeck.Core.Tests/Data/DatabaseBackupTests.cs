using Microsoft.Data.Sqlite;
using RemoteDeck.Core.Data;

namespace RemoteDeck.Core.Tests.Data;

/// <summary>
/// The copy taken before a migration. A migration is forward-only and the build that wrote the old
/// schema refuses the new one, so this copy is the only way back to the version the user had.
/// </summary>
public sealed class DatabaseBackupTests
{
    /// <summary>A V1 database with one connection in it — the oldest shape a user can still have.</summary>
    private static void SeedV1(TempDatabase tmp)
    {
        using var c = tmp.Db.Open();
        c.Cmd("CREATE TABLE SchemaVersion (Version INTEGER NOT NULL, AppliedUtc TEXT NOT NULL)").ExecuteNonQuery();
        c.Cmd("""
            CREATE TABLE Connection (
              Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Host TEXT NOT NULL, Port INTEGER NOT NULL DEFAULT 3389,
              GroupName TEXT NOT NULL DEFAULT '', CredentialId INTEGER NULL,
              IsFavorite INTEGER NOT NULL DEFAULT 0, DisplayMode INTEGER NOT NULL DEFAULT 0,
              FixedWidth INTEGER NULL, FixedHeight INTEGER NULL, RedirectClipboard INTEGER NOT NULL DEFAULT 1,
              RedirectDrives INTEGER NOT NULL DEFAULT 0, RedirectPrinters INTEGER NOT NULL DEFAULT 0,
              RedirectAudio INTEGER NOT NULL DEFAULT 0, AdminSession INTEGER NOT NULL DEFAULT 0,
              UseWebAccount INTEGER NOT NULL DEFAULT 0, AuthenticationLevel INTEGER NULL,
              AcceptedCertThumbprint TEXT NULL, Notes TEXT NOT NULL DEFAULT '',
              LastConnectedUtc TEXT NULL, CreatedUtc TEXT NOT NULL);
            CREATE TABLE Credential (
              Id INTEGER PRIMARY KEY, Label TEXT NOT NULL UNIQUE, Domain TEXT NULL, UserName TEXT NOT NULL,
              SecretBlob BLOB NOT NULL, Entropy BLOB NOT NULL, ModifiedUtc TEXT NOT NULL);
            """).ExecuteNonQuery();
        c.Cmd("INSERT INTO SchemaVersion(Version, AppliedUtc) VALUES (1, '2026-01-01T00:00:00.0000000Z')").ExecuteNonQuery();
        c.Cmd("INSERT INTO Connection(Name, Host, CreatedUtc) VALUES ('SQL', 'contososql00001', '2026-01-01T00:00:00.0000000Z')").ExecuteNonQuery();
    }

    private static string BackupOf(TempDatabase tmp, int version) =>
        Path.Combine(Path.GetDirectoryName(tmp.Path)!, $"{Path.GetFileNameWithoutExtension(tmp.Path)}.v{version}.bak");

    private static SqliteConnection OpenReadOnly(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());

    [Fact]
    public void An_upgrade_writes_a_copy_of_the_old_database_first()
    {
        using var tmp = new TempDatabase();
        SeedV1(tmp);
        var expected = BackupOf(tmp, 1);

        try
        {
            var written = tmp.Db.EnsureCreated();

            Assert.Equal(expected, written);
            using var copy = OpenReadOnly(expected);
            copy.Open();
            // The old schema, untouched: version 1, its row, and none of the tables later versions add.
            Assert.Equal(1, SchemaMigrator.GetVersion(copy));
            Assert.Equal(1L, copy.Cmd("SELECT COUNT(*) FROM Connection").ExecuteScalar());
            Assert.Equal(0L, copy.Cmd("SELECT COUNT(*) FROM sqlite_master WHERE name = 'Workspace'").ExecuteScalar());
            // One file to copy back by hand, not a WAL database with companions.
            Assert.Equal("delete", copy.Cmd("PRAGMA journal_mode").ExecuteScalar());
        }
        finally
        {
            File.Delete(expected);
        }
    }

    [Fact]
    public void A_new_database_has_nothing_to_back_up()
    {
        using var tmp = new TempDatabase();

        Assert.Null(tmp.Db.EnsureCreated());
        Assert.False(File.Exists(BackupOf(tmp, 0)));
    }

    [Fact]
    public void A_database_already_current_is_not_copied_again()
    {
        using var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();

        Assert.Null(tmp.Db.EnsureCreated());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(tmp.Path)!, Path.GetFileNameWithoutExtension(tmp.Path) + ".v*.bak"));
    }

    [Fact]
    public void An_older_copy_of_the_same_version_is_replaced()
    {
        // The copy must be the database as it was just before *this* migration. A stale one left by an
        // earlier round trip (upgrade, restore, months of use on the old build) would silently lose
        // everything done since.
        using var tmp = new TempDatabase();
        SeedV1(tmp);
        var backup = BackupOf(tmp, 1);
        File.WriteAllText(backup, "stale");

        try
        {
            tmp.Db.EnsureCreated();

            using var copy = OpenReadOnly(backup);
            copy.Open();
            Assert.Equal(1L, copy.Cmd("SELECT COUNT(*) FROM Connection").ExecuteScalar());
        }
        finally
        {
            File.Delete(backup);
        }
    }

    [Fact]
    public void A_copy_that_cannot_be_written_stops_the_migration()
    {
        using var tmp = new TempDatabase();
        SeedV1(tmp);
        var backup = BackupOf(tmp, 1);
        File.WriteAllText(backup, "the previous good copy");
        // A directory where the copy's working file goes: SQLite cannot open it, deterministically.
        var blocker = Directory.CreateDirectory(backup + ".tmp");

        try
        {
            var ex = Assert.Throws<DatabaseBackupException>(() => tmp.Db.EnsureCreated());

            Assert.Equal(backup, ex.BackupPath);
            // Nothing migrated: the user still has the version their other build can open…
            using (var c = tmp.Db.Open())
            {
                Assert.Equal(1, SchemaMigrator.GetVersion(c));
            }

            // …and a failed copy never replaces a good one.
            Assert.Equal("the previous good copy", File.ReadAllText(backup));
        }
        finally
        {
            blocker.Delete(recursive: true);
            File.Delete(backup);
        }
    }
}
