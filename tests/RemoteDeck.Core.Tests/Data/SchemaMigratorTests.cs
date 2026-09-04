using Microsoft.Data.Sqlite;
using RemoteDeck.Core.Data;

namespace RemoteDeck.Core.Tests.Data;

public sealed class SchemaMigratorTests
{
    [Fact]
    public void EnsureCreated_creates_file_and_current_schema()
    {
        using var tmp = new TempDatabase();

        tmp.Db.EnsureCreated();

        Assert.True(File.Exists(tmp.Path));
        using var c = tmp.Db.Open();
        Assert.Equal(SchemaMigrator.CurrentVersion, SchemaMigrator.GetVersion(c));
        var tables = new List<string>();
        using var r = c.Cmd("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ExecuteReader();
        while (r.Read()) tables.Add(r.GetString(0));
        Assert.Contains("Credential", tables);
        Assert.Contains("Connection", tables);
        Assert.Contains("SchemaVersion", tables);
    }

    [Fact]
    public void EnsureCreated_is_idempotent()
    {
        using var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();

        tmp.Db.EnsureCreated();

        using var c = tmp.Db.Open();
        Assert.Equal(SchemaMigrator.CurrentVersion, SchemaMigrator.GetVersion(c));
        Assert.Equal((long)SchemaMigrator.CurrentVersion, c.Cmd("SELECT COUNT(*) FROM SchemaVersion").ExecuteScalar());
    }

    [Fact]
    public void Open_enables_foreign_keys_and_wal()
    {
        using var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();

        using var c = tmp.Db.Open();

        Assert.Equal(1L, c.Cmd("PRAGMA foreign_keys").ExecuteScalar());
        Assert.Equal("wal", c.Cmd("PRAGMA journal_mode").ExecuteScalar());
    }

    [Fact]
    public void Migrate_refuses_a_newer_database()
    {
        using var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();
        using (var c = tmp.Db.Open())
        {
            c.Cmd("INSERT INTO SchemaVersion(Version, AppliedUtc) VALUES (999, '2099-01-01T00:00:00.0000000Z')").ExecuteNonQuery();
        }

        var ex = Assert.Throws<SchemaTooNewException>(() => tmp.Db.EnsureCreated());

        Assert.Equal(999, ex.Found);
        Assert.Equal(SchemaMigrator.CurrentVersion, ex.Supported);
    }

    [Fact]
    public void GetVersion_is_zero_on_empty_database()
    {
        using var tmp = new TempDatabase();
        using var c = tmp.Db.Open();

        Assert.Equal(0, SchemaMigrator.GetVersion(c));
    }

    [Fact]
    public void EnsureCreated_creates_the_workspace_tables()
    {
        using var tmp = new TempDatabase();

        tmp.Db.EnsureCreated();

        using var c = tmp.Db.Open();
        var tables = new List<string>();
        using var r = c.Cmd("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ExecuteReader();
        while (r.Read()) tables.Add(r.GetString(0));
        Assert.Contains("Workspace", tables);
        Assert.Contains("WorkspaceItem", tables);
    }

    [Fact]
    public void Migrate_upgrades_a_populated_v1_database_without_losing_rows()
    {
        using var tmp = new TempDatabase();

        // Une base V1 telle qu'elle existe sur le poste d'un utilisateur : le script V1 seul,
        // estampillé version 1, avec une connexion dedans.
        using (var c = tmp.Db.Open())
        {
            c.Cmd("CREATE TABLE SchemaVersion (Version INTEGER NOT NULL, AppliedUtc TEXT NOT NULL)").ExecuteNonQuery();
            c.Cmd("""
                CREATE TABLE Credential (
                  Id INTEGER PRIMARY KEY, Label TEXT NOT NULL UNIQUE, Domain TEXT NULL, UserName TEXT NOT NULL,
                  SecretBlob BLOB NOT NULL, Entropy BLOB NOT NULL, ModifiedUtc TEXT NOT NULL);
                CREATE TABLE Connection (
                  Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Host TEXT NOT NULL, Port INTEGER NOT NULL DEFAULT 3389,
                  GroupName TEXT NOT NULL DEFAULT '', CredentialId INTEGER NULL REFERENCES Credential(Id) ON DELETE SET NULL,
                  IsFavorite INTEGER NOT NULL DEFAULT 0, DisplayMode INTEGER NOT NULL DEFAULT 0,
                  FixedWidth INTEGER NULL, FixedHeight INTEGER NULL, RedirectClipboard INTEGER NOT NULL DEFAULT 1,
                  RedirectDrives INTEGER NOT NULL DEFAULT 0, RedirectPrinters INTEGER NOT NULL DEFAULT 0,
                  RedirectAudio INTEGER NOT NULL DEFAULT 0, AdminSession INTEGER NOT NULL DEFAULT 0,
                  UseWebAccount INTEGER NOT NULL DEFAULT 0, AuthenticationLevel INTEGER NULL,
                  AcceptedCertThumbprint TEXT NULL, Notes TEXT NOT NULL DEFAULT '',
                  LastConnectedUtc TEXT NULL, CreatedUtc TEXT NOT NULL);
                """).ExecuteNonQuery();
            c.Cmd("INSERT INTO SchemaVersion(Version, AppliedUtc) VALUES (1, '2026-01-01T00:00:00.0000000Z')").ExecuteNonQuery();
            c.Cmd("INSERT INTO Connection(Name, Host, CreatedUtc) VALUES ('SQL', 'fdcsql00001', '2026-01-01T00:00:00.0000000Z')").ExecuteNonQuery();
        }

        tmp.Db.EnsureCreated();

        using var after = tmp.Db.Open();
        Assert.Equal(SchemaMigrator.CurrentVersion, SchemaMigrator.GetVersion(after));
        Assert.Equal(1L, after.Cmd("SELECT COUNT(*) FROM Connection").ExecuteScalar());
        Assert.Equal(0L, after.Cmd("SELECT COUNT(*) FROM WorkspaceItem").ExecuteScalar());
    }

    [Fact]
    public void V3_adds_VpnProfile_to_an_existing_populated_database()
    {
        // The ALTER runs over rows that already exist, so the column has to arrive nullable and
        // leave every one of them saying "no VPN required".
        using var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();
        using var c = tmp.Db.Open();
        c.Cmd("INSERT INTO Connection(Name, Host, CreatedUtc) VALUES ('SQL', 'fdcsql00001', '2026-01-01T00:00:00.0000000Z')").ExecuteNonQuery();

        using var r = c.Cmd("SELECT VpnProfile FROM Connection").ExecuteReader();

        Assert.True(r.Read());
        Assert.True(r.IsDBNull(0));
    }

    [Fact]
    public void Deleting_a_connection_cascades_to_its_workspace_items()
    {
        using var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();
        using var c = tmp.Db.Open();
        c.Cmd("INSERT INTO Connection(Id, Name, Host, CreatedUtc) VALUES (7, 'SQL', 'fdcsql00001', '2026-01-01T00:00:00.0000000Z')").ExecuteNonQuery();
        c.Cmd("INSERT INTO Workspace(Id, Name, AutoConnect, CreatedUtc) VALUES (1, 'PROD', 1, '2026-01-01T00:00:00.0000000Z')").ExecuteNonQuery();
        c.Cmd("INSERT INTO WorkspaceItem(WorkspaceId, ConnectionId, Ordinal) VALUES (1, 7, 0)").ExecuteNonQuery();

        c.Cmd("DELETE FROM Connection WHERE Id = 7").ExecuteNonQuery();

        // L'espace survit, son item non : c'est ce qui empêche un id de connexion mort d'y rester.
        Assert.Equal(0L, c.Cmd("SELECT COUNT(*) FROM WorkspaceItem").ExecuteScalar());
        Assert.Equal(1L, c.Cmd("SELECT COUNT(*) FROM Workspace").ExecuteScalar());
    }
}
