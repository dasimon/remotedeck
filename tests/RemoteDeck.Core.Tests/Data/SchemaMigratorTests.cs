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
        Assert.Equal(1L, c.Cmd("SELECT COUNT(*) FROM SchemaVersion").ExecuteScalar());
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
}
