using Microsoft.Data.Sqlite;

namespace RemoteDeck.Core.Data;

/// <summary>Numbered, forward-only migrations. Each script runs once inside a transaction.</summary>
public static class SchemaMigrator
{
    /// <summary>Highest version this build can migrate to; derived from the script table, so adding a script bumps it.</summary>
    public static int CurrentVersion => Scripts.Length;

    // Index = version - 1. Never edit a shipped script; add a new one.
    private static readonly string[] Scripts =
    [
        // V1 — spec §4
        """
        CREATE TABLE Credential (
          Id          INTEGER PRIMARY KEY,
          Label       TEXT    NOT NULL UNIQUE,
          Domain      TEXT    NULL,
          UserName    TEXT    NOT NULL,
          SecretBlob  BLOB    NOT NULL,
          Entropy     BLOB    NOT NULL,
          ModifiedUtc TEXT    NOT NULL);

        CREATE TABLE Connection (
          Id                     INTEGER PRIMARY KEY,
          Name                   TEXT    NOT NULL,
          Host                   TEXT    NOT NULL,
          Port                   INTEGER NOT NULL DEFAULT 3389,
          GroupName              TEXT    NOT NULL DEFAULT '',
          CredentialId           INTEGER NULL REFERENCES Credential(Id) ON DELETE SET NULL,
          IsFavorite             INTEGER NOT NULL DEFAULT 0,
          DisplayMode            INTEGER NOT NULL DEFAULT 0,
          FixedWidth             INTEGER NULL,
          FixedHeight            INTEGER NULL,
          RedirectClipboard      INTEGER NOT NULL DEFAULT 1,
          RedirectDrives         INTEGER NOT NULL DEFAULT 0,
          RedirectPrinters       INTEGER NOT NULL DEFAULT 0,
          RedirectAudio          INTEGER NOT NULL DEFAULT 0,
          AdminSession           INTEGER NOT NULL DEFAULT 0,
          UseWebAccount          INTEGER NOT NULL DEFAULT 0,
          AuthenticationLevel    INTEGER NULL,
          AcceptedCertThumbprint TEXT    NULL,
          Notes                  TEXT    NOT NULL DEFAULT '',
          LastConnectedUtc       TEXT    NULL,
          CreatedUtc             TEXT    NOT NULL);

        CREATE INDEX IX_Connection_GroupName ON Connection(GroupName);
        CREATE INDEX IX_Connection_Favorite  ON Connection(IsFavorite) WHERE IsFavorite = 1;
        """,
        // V2 — espaces de travail (spec espaces §3.1)
        """
        CREATE TABLE Workspace (
          Id          INTEGER PRIMARY KEY,
          Name        TEXT    NOT NULL UNIQUE,
          AutoConnect INTEGER NOT NULL DEFAULT 1,
          CreatedUtc  TEXT    NOT NULL);

        CREATE TABLE WorkspaceItem (
          WorkspaceId  INTEGER NOT NULL REFERENCES Workspace(Id)  ON DELETE CASCADE,
          ConnectionId INTEGER NOT NULL REFERENCES Connection(Id) ON DELETE CASCADE,
          Ordinal      INTEGER NOT NULL,
          Detached     INTEGER NOT NULL DEFAULT 0,
          Left         REAL    NULL,
          Top          REAL    NULL,
          Width        REAL    NULL,
          Height       REAL    NULL,
          FullScreen   INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (WorkspaceId, ConnectionId));

        CREATE INDEX IX_WorkspaceItem_Connection ON WorkspaceItem(ConnectionId);
        """,
    ];

    public static int GetVersion(SqliteConnection connection)
    {
        var exists = connection.Cmd("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SchemaVersion'").ExecuteScalar();
        if (Convert.ToInt64(exists) == 0) return 0;
        var max = connection.Cmd("SELECT MAX(Version) FROM SchemaVersion").ExecuteScalar();
        return max is null or DBNull ? 0 : Convert.ToInt32(max);
    }

    public static void Migrate(SqliteConnection connection)
    {
        var version = GetVersion(connection);
        if (version > CurrentVersion) throw new SchemaTooNewException(version, CurrentVersion);

        if (version == 0)
        {
            connection.Cmd("CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL, AppliedUtc TEXT NOT NULL)").ExecuteNonQuery();
        }

        for (var v = version + 1; v <= CurrentVersion; v++)
        {
            using var tx = connection.BeginTransaction();
            var script = connection.Cmd(Scripts[v - 1]);
            script.Transaction = tx;
            script.ExecuteNonQuery();
            var stamp = connection.Cmd("INSERT INTO SchemaVersion(Version, AppliedUtc) VALUES ($v, $t)");
            stamp.Transaction = tx;
            stamp.Add("$v", v);
            stamp.Add("$t", DateTime.UtcNow.ToDb());
            stamp.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
