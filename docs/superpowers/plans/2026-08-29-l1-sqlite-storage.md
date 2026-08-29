# RemoteDeck — Lot 1 : stockage SQLite (modèle, migrations, repositories)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Une base SQLite locale, créée et migrée au premier lancement, avec les deux entités du §4 (`Credential`, `Connection`) et leurs repositories, entièrement testés en xUnit sans UI ni COM.

**Architecture:** Tout vit dans `RemoteDeck.Core` (net10.0, aucune référence Windows). `SqliteDatabase` ouvre des connexions (WAL, clés étrangères actives) ; `SchemaMigrator` applique des scripts numérotés et refuse une base plus récente ; deux repositories en SQL explicite (`Microsoft.Data.Sqlite`, pas d'ORM). L'App ne fait qu'ouvrir/migrer au démarrage et journaliser la version.

**Tech Stack:** .NET 10, `Microsoft.Data.Sqlite` 10.0.11, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-29-remotedeck-design.md` — §3 (structure), §4 (modèle de données, migrations), §10 (tests). Le lot 0 a établi les conventions du dépôt (voir Global Constraints).

## Global Constraints

- `RemoteDeck.Core` reste **sans référence WPF/WinForms/COM/Windows-only**. La seule dépendance ajoutée dans ce lot est `Microsoft.Data.Sqlite` **10.0.11** dans Core.
- Schéma **exactement** celui du §4 de la spec (noms de tables/colonnes, types, defaults, `ON DELETE SET NULL`, index) — reproduit verbatim en Task 2. `GroupName` (pas `Group`). Dates ISO-8601 UTC (`TEXT`, format `"O"`), suffixe `Utc`.
- `DisplayMode` : 0 Dynamic, 1 Scaled, 2 Fixed (énumération **du projet**, §4).
- Fichier : `%APPDATA%\RemoteDeck\connections.db`, journal WAL. Le chemin est **injecté** (tests sur fichier temporaire) ; l'App fournit le chemin réel.
- `PRAGMA foreign_keys = ON` à **chaque** connexion ouverte (SQLite le désactive par défaut — sans lui `ON DELETE SET NULL` est inerte).
- Une base dont `SchemaVersion` > version connue → `SchemaTooNewException` (message explicite), jamais de tentative d'écriture.
- Aucun secret en clair : `Credential.SecretBlob`/`Entropy` sont des `byte[]` opaques pour ce lot (le coffre DPAPI arrive au lot 2) ; aucun log de leur contenu.
- Warning-free (`TreatWarningsAsErrors`). Code/commentaires/commits en **anglais**. `git add` **par fichier**, jamais `-A`/`.` ; jamais `.superpowers/`, `docs/PROJET.md`, `bin/`, `obj/`. Commits : `git -c user.name="David Simon" -c user.email="david.simon@financieredelacite.com" commit -m "..."` + trailer `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`.
- TDD pour tout ce qui est dans Core : test d'abord (RED), implémentation (GREEN).
- Les tests écrivent dans un fichier temporaire par test et le suppriment (`Pooling=False` dans la chaîne de connexion — sinon Windows garde le handle et la suppression échoue).

---

## Carte des fichiers

| Fichier | Responsabilité |
|---|---|
| `src/RemoteDeck.Core/Model/DisplayMode.cs` | Énumération 0/1/2 |
| `src/RemoteDeck.Core/Model/Credential.cs` | Entité (Id, Label, Domain, UserName, SecretBlob, Entropy, ModifiedUtc) |
| `src/RemoteDeck.Core/Model/Connection.cs` | Entité (toutes les colonnes du §4) |
| `src/RemoteDeck.Core/Data/SqliteDatabase.cs` | Chemin, chaîne de connexion, `Open()` (WAL + FK), `EnsureCreated()` → migrator |
| `src/RemoteDeck.Core/Data/SchemaMigrator.cs` | Scripts numérotés, `SchemaVersion`, `SchemaTooNewException` |
| `src/RemoteDeck.Core/Data/CredentialRepository.cs` | CRUD `Credential` |
| `src/RemoteDeck.Core/Data/ConnectionRepository.cs` | CRUD + listing `Connection` |
| `src/RemoteDeck.Core/Data/SqliteExtensions.cs` | Helpers `AddParam`, lecture nullable |
| `tests/RemoteDeck.Core.Tests/Data/TempDatabase.cs` | Fixture : fichier temporaire, `IDisposable` |
| `tests/RemoteDeck.Core.Tests/Data/SchemaMigratorTests.cs` | Création, idempotence, refus version future |
| `tests/RemoteDeck.Core.Tests/Data/CredentialRepositoryTests.cs` | CRUD, unicité `Label` |
| `tests/RemoteDeck.Core.Tests/Data/ConnectionRepositoryTests.cs` | CRUD, tri favoris/groupe/nom, `ON DELETE SET NULL` |
| `src/RemoteDeck.App/App.xaml.cs` | Ouverture/migration au démarrage + log |
| `.github/workflows/ci.yml` | actions `@v5` (dépréciation Node 20) |

---

### Task 1: Modèle (Core)

**Files:**
- Create: `src/RemoteDeck.Core/Model/DisplayMode.cs`, `src/RemoteDeck.Core/Model/Credential.cs`, `src/RemoteDeck.Core/Model/Connection.cs`
- Modify: `src/RemoteDeck.Core/RemoteDeck.Core.csproj` (ajout du paquet)

**Interfaces:**
- Produces:
  - `public enum DisplayMode { Dynamic = 0, Scaled = 1, Fixed = 2 }`
  - `public sealed class Credential { long Id; string Label; string? Domain; string UserName; byte[] SecretBlob; byte[] Entropy; DateTime ModifiedUtc; }` (propriétés `{ get; set; }`, `Id = 0` = non persisté)
  - `public sealed class Connection { long Id; string Name; string Host; int Port = 3389; string GroupName = ""; long? CredentialId; bool IsFavorite; DisplayMode DisplayMode; int? FixedWidth; int? FixedHeight; bool RedirectClipboard = true; bool RedirectDrives; bool RedirectPrinters; bool RedirectAudio; bool AdminSession; bool UseWebAccount; int? AuthenticationLevel; string? AcceptedCertThumbprint; string Notes = ""; DateTime? LastConnectedUtc; DateTime CreatedUtc; }`

Pas de test dédié : ce sont des porteurs de données ; ils sont exercés par les repositories.

- [ ] **Step 1: Ajouter le paquet à Core**

Dans `src/RemoteDeck.Core/RemoteDeck.Core.csproj`, ajouter :

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.11" />
  </ItemGroup>
```

- [ ] **Step 2: Écrire les trois fichiers**

`src/RemoteDeck.Core/Model/DisplayMode.cs` :

```csharp
namespace RemoteDeck.Core.Model;

/// <summary>How the remote desktop follows the window size. Values are persisted; never renumber.</summary>
public enum DisplayMode
{
    /// <summary>Remote resolution follows the window (UpdateSessionDisplaySettings). Default.</summary>
    Dynamic = 0,
    /// <summary>Fixed remote resolution, image scaled to fit (SmartSizing).</summary>
    Scaled = 1,
    /// <summary>Fixed remote resolution, scrollbars when the window is smaller.</summary>
    Fixed = 2,
}
```

`src/RemoteDeck.Core/Model/Credential.cs` :

```csharp
namespace RemoteDeck.Core.Model;

/// <summary>
/// A reusable account. The secret is stored as an opaque DPAPI blob plus per-row entropy;
/// this type never holds the decrypted value (spec §5).
/// </summary>
public sealed class Credential
{
    public long Id { get; set; }
    public required string Label { get; set; }
    public string? Domain { get; set; }
    public required string UserName { get; set; }
    public required byte[] SecretBlob { get; set; }
    public required byte[] Entropy { get; set; }
    public DateTime ModifiedUtc { get; set; }
}
```

`src/RemoteDeck.Core/Model/Connection.cs` :

```csharp
namespace RemoteDeck.Core.Model;

/// <summary>One saved RDP target. Mirrors the Connection table (spec §4) one-to-one.</summary>
public sealed class Connection
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 3389;
    public string GroupName { get; set; } = "";
    public long? CredentialId { get; set; }
    public bool IsFavorite { get; set; }
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Dynamic;
    public int? FixedWidth { get; set; }
    public int? FixedHeight { get; set; }
    public bool RedirectClipboard { get; set; } = true;
    public bool RedirectDrives { get; set; }
    public bool RedirectPrinters { get; set; }
    public bool RedirectAudio { get; set; }
    public bool AdminSession { get; set; }
    public bool UseWebAccount { get; set; }
    public int? AuthenticationLevel { get; set; }
    public string? AcceptedCertThumbprint { get; set; }
    public string Notes { get; set; } = "";
    public DateTime? LastConnectedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build RemoteDeck.sln`
Expected: `Build succeeded.`, 0 avertissement.

- [ ] **Step 4: Commit**

```bash
git add src/RemoteDeck.Core/RemoteDeck.Core.csproj src/RemoteDeck.Core/Model/DisplayMode.cs src/RemoteDeck.Core/Model/Credential.cs src/RemoteDeck.Core/Model/Connection.cs
git commit -m "feat(core): data model for credentials and connections"
```

---

### Task 2: `SqliteDatabase` + `SchemaMigrator` (TDD)

**Files:**
- Create: `src/RemoteDeck.Core/Data/SqliteDatabase.cs`, `src/RemoteDeck.Core/Data/SchemaMigrator.cs`, `src/RemoteDeck.Core/Data/SchemaTooNewException.cs`, `src/RemoteDeck.Core/Data/SqliteExtensions.cs`
- Test: `tests/RemoteDeck.Core.Tests/Data/TempDatabase.cs`, `tests/RemoteDeck.Core.Tests/Data/SchemaMigratorTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class SqliteDatabase(string path)` — `string Path { get; }`, `SqliteConnection Open()` (ouverte, WAL, FK ON), `void EnsureCreated()` (crée le répertoire, migre), `static string DefaultPath()` → `%APPDATA%\RemoteDeck\connections.db`
  - `public static class SchemaMigrator` — `const int CurrentVersion = 1`, `int GetVersion(SqliteConnection)` (0 si table absente), `void Migrate(SqliteConnection)`
  - `public sealed class SchemaTooNewException(int found, int supported) : Exception`
  - `internal static class SqliteExtensions` — `SqliteCommand Cmd(this SqliteConnection, string sql)`, `void Add(this SqliteCommand, string name, object? value)` (null → `DBNull`), `string? GetStringOrNull(this SqliteDataReader, int i)`, `long? GetInt64OrNull(...)`, `int? GetInt32OrNull(...)`, `DateTime? GetUtcOrNull(...)`, `DateTime GetUtc(...)`
  - Test fixture `internal sealed class TempDatabase : IDisposable` — `SqliteDatabase Db { get; }`, crée un fichier `Path.GetTempFileName()` + `.db`, supprime à Dispose (+ `-wal`/`-shm`)

- [ ] **Step 1: Écrire la fixture et les tests**

`tests/RemoteDeck.Core.Tests/Data/TempDatabase.cs` :

```csharp
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
```

`tests/RemoteDeck.Core.Tests/Data/SchemaMigratorTests.cs` :

```csharp
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
```

`SqliteExtensions` est `internal` : ajouter dans `src/RemoteDeck.Core/RemoteDeck.Core.csproj` :

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="RemoteDeck.Core.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Vérifier que ça échoue**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: échec de compilation (`RemoteDeck.Core.Data` inexistant).

- [ ] **Step 3: Implémenter**

`src/RemoteDeck.Core/Data/SqliteExtensions.cs` :

```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RemoteDeck.Core.Data;

internal static class SqliteExtensions
{
    public static SqliteCommand Cmd(this SqliteConnection connection, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    public static void Add(this SqliteCommand cmd, string name, object? value)
        => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public static string? GetStringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static long? GetInt64OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    public static int? GetInt32OrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static DateTime GetUtc(this SqliteDataReader r, int i)
        => DateTime.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTime? GetUtcOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetUtc(i);

    /// <summary>ISO-8601 round-trip text; the only date format written to the database.</summary>
    public static string ToDb(this DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
```

`src/RemoteDeck.Core/Data/SchemaTooNewException.cs` :

```csharp
namespace RemoteDeck.Core.Data;

/// <summary>The database was written by a newer RemoteDeck; opening it read-write could corrupt it.</summary>
public sealed class SchemaTooNewException(int found, int supported)
    : Exception($"The database schema is version {found}, but this build supports up to version {supported}. Update RemoteDeck.")
{
    public int Found { get; } = found;
    public int Supported { get; } = supported;
}
```

`src/RemoteDeck.Core/Data/SchemaMigrator.cs` — le script V1 est le §4 de la spec, verbatim :

```csharp
using Microsoft.Data.Sqlite;

namespace RemoteDeck.Core.Data;

/// <summary>Numbered, forward-only migrations. Each script runs once inside a transaction.</summary>
public static class SchemaMigrator
{
    public const int CurrentVersion = 1;

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
```

`src/RemoteDeck.Core/Data/SqliteDatabase.cs` :

```csharp
using Microsoft.Data.Sqlite;

namespace RemoteDeck.Core.Data;

/// <summary>Location and connection policy for the local database (spec §4): WAL journal, foreign keys on.</summary>
public sealed class SqliteDatabase(string path)
{
    public string Path { get; } = path;

    /// <summary>%APPDATA%\RemoteDeck\connections.db — the directory's default ACL is already user-only.</summary>
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
```

- [ ] **Step 4: Vérifier que ça passe**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: 10 tests verts (5 du lot 0 + 5).

- [ ] **Step 5: Commit**

```bash
git add src/RemoteDeck.Core/RemoteDeck.Core.csproj src/RemoteDeck.Core/Data/SqliteExtensions.cs src/RemoteDeck.Core/Data/SchemaTooNewException.cs src/RemoteDeck.Core/Data/SchemaMigrator.cs src/RemoteDeck.Core/Data/SqliteDatabase.cs tests/RemoteDeck.Core.Tests/Data/TempDatabase.cs tests/RemoteDeck.Core.Tests/Data/SchemaMigratorTests.cs
git commit -m "feat(core): SQLite database with forward-only schema migrations"
```

---

### Task 3: `CredentialRepository` (TDD)

**Files:**
- Create: `src/RemoteDeck.Core/Data/CredentialRepository.cs`
- Test: `tests/RemoteDeck.Core.Tests/Data/CredentialRepositoryTests.cs`

**Interfaces:**
- Consumes: `SqliteDatabase.Open()`, `SqliteExtensions`, `Credential`
- Produces: `public sealed class CredentialRepository(SqliteDatabase db)` — `long Insert(Credential)` (assigne et retourne `Id`, pose `ModifiedUtc`), `void Update(Credential)` (pose `ModifiedUtc`; `KeyNotFoundException` si absent), `void Delete(long id)`, `Credential? Get(long id)`, `IReadOnlyList<Credential> GetAll()` (tri `Label`, ordinal insensible à la casse). `Insert`/`Update` avec un `Label` déjà pris → `SqliteException` (contrainte UNIQUE) propagée telle quelle.

- [ ] **Step 1: Écrire les tests**

`tests/RemoteDeck.Core.Tests/Data/CredentialRepositoryTests.cs` :

```csharp
using Microsoft.Data.Sqlite;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Tests.Data;

public sealed class CredentialRepositoryTests : IDisposable
{
    private readonly TempDatabase _tmp = new();
    private readonly CredentialRepository _repo;

    public CredentialRepositoryTests()
    {
        _tmp.Db.EnsureCreated();
        _repo = new CredentialRepository(_tmp.Db);
    }

    public void Dispose() => _tmp.Dispose();

    private static Credential Make(string label) => new()
    {
        Label = label, UserName = "admin", Domain = "corp", SecretBlob = [1, 2, 3], Entropy = new byte[32],
    };

    [Fact]
    public void Insert_assigns_id_and_roundtrips_all_fields()
    {
        var c = Make("Domain admin");

        var id = _repo.Insert(c);

        Assert.True(id > 0);
        Assert.Equal(id, c.Id);
        var back = _repo.Get(id);
        Assert.NotNull(back);
        Assert.Equal("Domain admin", back.Label);
        Assert.Equal("admin", back.UserName);
        Assert.Equal("corp", back.Domain);
        Assert.Equal(new byte[] { 1, 2, 3 }, back.SecretBlob);
        Assert.Equal(32, back.Entropy.Length);
        Assert.Equal(DateTimeKind.Utc, back.ModifiedUtc.Kind);
        Assert.True((DateTime.UtcNow - back.ModifiedUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Label_is_unique()
    {
        _repo.Insert(Make("Same"));

        Assert.Throws<SqliteException>(() => _repo.Insert(Make("Same")));
    }

    [Fact]
    public void Update_changes_fields_and_timestamp()
    {
        var c = Make("Old");
        _repo.Insert(c);
        var before = _repo.Get(c.Id)!.ModifiedUtc;
        Thread.Sleep(5);

        c.Label = "New";
        c.Domain = null;
        c.SecretBlob = [9];
        _repo.Update(c);

        var back = _repo.Get(c.Id)!;
        Assert.Equal("New", back.Label);
        Assert.Null(back.Domain);
        Assert.Equal(new byte[] { 9 }, back.SecretBlob);
        Assert.True(back.ModifiedUtc > before);
    }

    [Fact]
    public void Update_unknown_id_throws()
    {
        var c = Make("Ghost");
        c.Id = 12345;

        Assert.Throws<KeyNotFoundException>(() => _repo.Update(c));
    }

    [Fact]
    public void Delete_removes_row()
    {
        var c = Make("Gone");
        _repo.Insert(c);

        _repo.Delete(c.Id);

        Assert.Null(_repo.Get(c.Id));
    }

    [Fact]
    public void GetAll_sorted_by_label_case_insensitive()
    {
        _repo.Insert(Make("beta"));
        _repo.Insert(Make("Alpha"));
        _repo.Insert(Make("gamma"));

        var labels = _repo.GetAll().Select(x => x.Label).ToArray();

        Assert.Equal(new[] { "Alpha", "beta", "gamma" }, labels);
    }
}
```

- [ ] **Step 2: Vérifier que ça échoue**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: échec de compilation (`CredentialRepository` inexistant).

- [ ] **Step 3: Implémenter**

`src/RemoteDeck.Core/Data/CredentialRepository.cs` :

```csharp
using Microsoft.Data.Sqlite;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Data;

/// <summary>CRUD for <see cref="Credential"/>. Secrets are opaque blobs here; encryption lives in the vault.</summary>
public sealed class CredentialRepository(SqliteDatabase db)
{
    private const string Columns = "Id, Label, Domain, UserName, SecretBlob, Entropy, ModifiedUtc";

    public long Insert(Credential credential)
    {
        credential.ModifiedUtc = DateTime.UtcNow;
        using var c = db.Open();
        var cmd = c.Cmd("""
            INSERT INTO Credential (Label, Domain, UserName, SecretBlob, Entropy, ModifiedUtc)
            VALUES ($label, $domain, $user, $blob, $entropy, $modified);
            SELECT last_insert_rowid();
            """);
        Bind(cmd, credential);
        credential.Id = (long)cmd.ExecuteScalar()!;
        return credential.Id;
    }

    public void Update(Credential credential)
    {
        credential.ModifiedUtc = DateTime.UtcNow;
        using var c = db.Open();
        var cmd = c.Cmd("""
            UPDATE Credential SET Label = $label, Domain = $domain, UserName = $user,
                SecretBlob = $blob, Entropy = $entropy, ModifiedUtc = $modified
            WHERE Id = $id
            """);
        Bind(cmd, credential);
        cmd.Add("$id", credential.Id);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException($"Credential {credential.Id} does not exist.");
    }

    public void Delete(long id)
    {
        using var c = db.Open();
        var cmd = c.Cmd("DELETE FROM Credential WHERE Id = $id");
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    public Credential? Get(long id)
    {
        using var c = db.Open();
        var cmd = c.Cmd($"SELECT {Columns} FROM Credential WHERE Id = $id");
        cmd.Add("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public IReadOnlyList<Credential> GetAll()
    {
        using var c = db.Open();
        using var r = c.Cmd($"SELECT {Columns} FROM Credential ORDER BY Label COLLATE NOCASE").ExecuteReader();
        var list = new List<Credential>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    private static void Bind(SqliteCommand cmd, Credential x)
    {
        cmd.Add("$label", x.Label);
        cmd.Add("$domain", x.Domain);
        cmd.Add("$user", x.UserName);
        cmd.Add("$blob", x.SecretBlob);
        cmd.Add("$entropy", x.Entropy);
        cmd.Add("$modified", x.ModifiedUtc.ToDb());
    }

    private static Credential Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Label = r.GetString(1),
        Domain = r.GetStringOrNull(2),
        UserName = r.GetString(3),
        SecretBlob = (byte[])r.GetValue(4),
        Entropy = (byte[])r.GetValue(5),
        ModifiedUtc = r.GetUtc(6),
    };
}
```

- [ ] **Step 4: Vérifier que ça passe**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: 16 tests verts.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteDeck.Core/Data/CredentialRepository.cs tests/RemoteDeck.Core.Tests/Data/CredentialRepositoryTests.cs
git commit -m "feat(core): credential repository"
```

---

### Task 4: `ConnectionRepository` (TDD)

**Files:**
- Create: `src/RemoteDeck.Core/Data/ConnectionRepository.cs`
- Test: `tests/RemoteDeck.Core.Tests/Data/ConnectionRepositoryTests.cs`

**Interfaces:**
- Produces: `public sealed class ConnectionRepository(SqliteDatabase db)` — `long Insert(Connection)` (pose `CreatedUtc`), `void Update(Connection)` (`KeyNotFoundException` si absent), `void Delete(long id)`, `Connection? Get(long id)`, `IReadOnlyList<Connection> GetAll()` (tri : favoris d'abord, puis `GroupName`, puis `Name`, NOCASE), `void TouchLastConnected(long id)` (pose `LastConnectedUtc = now`).

- [ ] **Step 1: Écrire les tests**

`tests/RemoteDeck.Core.Tests/Data/ConnectionRepositoryTests.cs` :

```csharp
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Tests.Data;

public sealed class ConnectionRepositoryTests : IDisposable
{
    private readonly TempDatabase _tmp = new();
    private readonly ConnectionRepository _repo;
    private readonly CredentialRepository _credentials;

    public ConnectionRepositoryTests()
    {
        _tmp.Db.EnsureCreated();
        _repo = new ConnectionRepository(_tmp.Db);
        _credentials = new CredentialRepository(_tmp.Db);
    }

    public void Dispose() => _tmp.Dispose();

    private static Connection Make(string name, string group = "", bool favorite = false) => new()
    {
        Name = name, Host = name.ToLowerInvariant() + ".example.com", GroupName = group, IsFavorite = favorite,
    };

    [Fact]
    public void Insert_roundtrips_every_column()
    {
        var x = new Connection
        {
            Name = "Prod DC", Host = "dc01", Port = 3390, GroupName = "Prod", IsFavorite = true,
            DisplayMode = DisplayMode.Fixed, FixedWidth = 1920, FixedHeight = 1080,
            RedirectClipboard = false, RedirectDrives = true, RedirectPrinters = true, RedirectAudio = true,
            AdminSession = true, UseWebAccount = true, AuthenticationLevel = 1, AcceptedCertThumbprint = "AB",
            Notes = "notes",
        };

        var id = _repo.Insert(x);

        var b = _repo.Get(id)!;
        Assert.Equal("Prod DC", b.Name);
        Assert.Equal("dc01", b.Host);
        Assert.Equal(3390, b.Port);
        Assert.Equal("Prod", b.GroupName);
        Assert.True(b.IsFavorite);
        Assert.Equal(DisplayMode.Fixed, b.DisplayMode);
        Assert.Equal(1920, b.FixedWidth);
        Assert.Equal(1080, b.FixedHeight);
        Assert.False(b.RedirectClipboard);
        Assert.True(b.RedirectDrives);
        Assert.True(b.RedirectPrinters);
        Assert.True(b.RedirectAudio);
        Assert.True(b.AdminSession);
        Assert.True(b.UseWebAccount);
        Assert.Equal(1, b.AuthenticationLevel);
        Assert.Equal("AB", b.AcceptedCertThumbprint);
        Assert.Equal("notes", b.Notes);
        Assert.Null(b.LastConnectedUtc);
        Assert.Null(b.CredentialId);
        Assert.Equal(DateTimeKind.Utc, b.CreatedUtc.Kind);
    }

    [Fact]
    public void Defaults_match_the_schema()
    {
        var id = _repo.Insert(Make("Plain"));

        var b = _repo.Get(id)!;
        Assert.Equal(3389, b.Port);
        Assert.Equal("", b.GroupName);
        Assert.Equal(DisplayMode.Dynamic, b.DisplayMode);
        Assert.True(b.RedirectClipboard);
        Assert.False(b.RedirectDrives);
        Assert.False(b.UseWebAccount);
        Assert.Null(b.AuthenticationLevel);
    }

    [Fact]
    public void GetAll_orders_favorites_then_group_then_name()
    {
        _repo.Insert(Make("zeta", "Dev"));
        _repo.Insert(Make("alpha", "Prod"));
        _repo.Insert(Make("Beta", "Dev"));
        _repo.Insert(Make("omega", "Prod", favorite: true));

        var names = _repo.GetAll().Select(c => c.Name).ToArray();

        Assert.Equal(new[] { "omega", "Beta", "zeta", "alpha" }, names);
    }

    [Fact]
    public void Deleting_a_credential_sets_connection_reference_to_null()
    {
        var cred = new Credential { Label = "L", UserName = "u", SecretBlob = [1], Entropy = new byte[32] };
        _credentials.Insert(cred);
        var x = Make("Uses cred");
        x.CredentialId = cred.Id;
        _repo.Insert(x);

        _credentials.Delete(cred.Id);

        Assert.NotNull(_repo.Get(x.Id));
        Assert.Null(_repo.Get(x.Id)!.CredentialId);
    }

    [Fact]
    public void Insert_with_unknown_credential_is_rejected()
    {
        var x = Make("Bad ref");
        x.CredentialId = 4242;

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _repo.Insert(x));
    }

    [Fact]
    public void Update_and_Delete_work_and_Update_unknown_throws()
    {
        var x = Make("A");
        _repo.Insert(x);

        x.Name = "B";
        x.IsFavorite = true;
        _repo.Update(x);
        Assert.Equal("B", _repo.Get(x.Id)!.Name);
        Assert.True(_repo.Get(x.Id)!.IsFavorite);

        _repo.Delete(x.Id);
        Assert.Null(_repo.Get(x.Id));
        Assert.Throws<KeyNotFoundException>(() => _repo.Update(x));
    }

    [Fact]
    public void TouchLastConnected_sets_timestamp()
    {
        var x = Make("T");
        _repo.Insert(x);

        _repo.TouchLastConnected(x.Id);

        var last = _repo.Get(x.Id)!.LastConnectedUtc;
        Assert.NotNull(last);
        Assert.True((DateTime.UtcNow - last.Value).Duration() < TimeSpan.FromMinutes(1));
    }
}
```

- [ ] **Step 2: Vérifier que ça échoue**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: échec de compilation (`ConnectionRepository` inexistant).

- [ ] **Step 3: Implémenter**

`src/RemoteDeck.Core/Data/ConnectionRepository.cs` :

```csharp
using Microsoft.Data.Sqlite;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Data;

/// <summary>CRUD and listing for <see cref="Connection"/> (spec §4). Ordering: favorites, then group, then name.</summary>
public sealed class ConnectionRepository(SqliteDatabase db)
{
    private const string Columns = """
        Id, Name, Host, Port, GroupName, CredentialId, IsFavorite, DisplayMode, FixedWidth, FixedHeight,
        RedirectClipboard, RedirectDrives, RedirectPrinters, RedirectAudio, AdminSession, UseWebAccount,
        AuthenticationLevel, AcceptedCertThumbprint, Notes, LastConnectedUtc, CreatedUtc
        """;

    public long Insert(Connection x)
    {
        x.CreatedUtc = DateTime.UtcNow;
        using var c = db.Open();
        var cmd = c.Cmd("""
            INSERT INTO Connection (Name, Host, Port, GroupName, CredentialId, IsFavorite, DisplayMode, FixedWidth, FixedHeight,
                RedirectClipboard, RedirectDrives, RedirectPrinters, RedirectAudio, AdminSession, UseWebAccount,
                AuthenticationLevel, AcceptedCertThumbprint, Notes, LastConnectedUtc, CreatedUtc)
            VALUES ($name, $host, $port, $group, $cred, $fav, $mode, $fw, $fh,
                $clip, $drives, $printers, $audio, $admin, $web,
                $auth, $thumb, $notes, $last, $created);
            SELECT last_insert_rowid();
            """);
        Bind(cmd, x);
        x.Id = (long)cmd.ExecuteScalar()!;
        return x.Id;
    }

    public void Update(Connection x)
    {
        using var c = db.Open();
        var cmd = c.Cmd("""
            UPDATE Connection SET Name = $name, Host = $host, Port = $port, GroupName = $group, CredentialId = $cred,
                IsFavorite = $fav, DisplayMode = $mode, FixedWidth = $fw, FixedHeight = $fh,
                RedirectClipboard = $clip, RedirectDrives = $drives, RedirectPrinters = $printers, RedirectAudio = $audio,
                AdminSession = $admin, UseWebAccount = $web, AuthenticationLevel = $auth, AcceptedCertThumbprint = $thumb,
                Notes = $notes, LastConnectedUtc = $last, CreatedUtc = $created
            WHERE Id = $id
            """);
        Bind(cmd, x);
        cmd.Add("$id", x.Id);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException($"Connection {x.Id} does not exist.");
    }

    public void Delete(long id)
    {
        using var c = db.Open();
        var cmd = c.Cmd("DELETE FROM Connection WHERE Id = $id");
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    public Connection? Get(long id)
    {
        using var c = db.Open();
        var cmd = c.Cmd($"SELECT {Columns} FROM Connection WHERE Id = $id");
        cmd.Add("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public IReadOnlyList<Connection> GetAll()
    {
        using var c = db.Open();
        using var r = c.Cmd($"SELECT {Columns} FROM Connection ORDER BY IsFavorite DESC, GroupName COLLATE NOCASE, Name COLLATE NOCASE").ExecuteReader();
        var list = new List<Connection>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public void TouchLastConnected(long id)
    {
        using var c = db.Open();
        var cmd = c.Cmd("UPDATE Connection SET LastConnectedUtc = $now WHERE Id = $id");
        cmd.Add("$now", DateTime.UtcNow.ToDb());
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, Connection x)
    {
        cmd.Add("$name", x.Name);
        cmd.Add("$host", x.Host);
        cmd.Add("$port", x.Port);
        cmd.Add("$group", x.GroupName);
        cmd.Add("$cred", x.CredentialId);
        cmd.Add("$fav", x.IsFavorite ? 1 : 0);
        cmd.Add("$mode", (int)x.DisplayMode);
        cmd.Add("$fw", x.FixedWidth);
        cmd.Add("$fh", x.FixedHeight);
        cmd.Add("$clip", x.RedirectClipboard ? 1 : 0);
        cmd.Add("$drives", x.RedirectDrives ? 1 : 0);
        cmd.Add("$printers", x.RedirectPrinters ? 1 : 0);
        cmd.Add("$audio", x.RedirectAudio ? 1 : 0);
        cmd.Add("$admin", x.AdminSession ? 1 : 0);
        cmd.Add("$web", x.UseWebAccount ? 1 : 0);
        cmd.Add("$auth", x.AuthenticationLevel);
        cmd.Add("$thumb", x.AcceptedCertThumbprint);
        cmd.Add("$notes", x.Notes);
        cmd.Add("$last", x.LastConnectedUtc?.ToDb());
        cmd.Add("$created", x.CreatedUtc.ToDb());
    }

    private static Connection Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Host = r.GetString(2),
        Port = r.GetInt32(3),
        GroupName = r.GetString(4),
        CredentialId = r.GetInt64OrNull(5),
        IsFavorite = r.GetInt32(6) != 0,
        DisplayMode = (DisplayMode)r.GetInt32(7),
        FixedWidth = r.GetInt32OrNull(8),
        FixedHeight = r.GetInt32OrNull(9),
        RedirectClipboard = r.GetInt32(10) != 0,
        RedirectDrives = r.GetInt32(11) != 0,
        RedirectPrinters = r.GetInt32(12) != 0,
        RedirectAudio = r.GetInt32(13) != 0,
        AdminSession = r.GetInt32(14) != 0,
        UseWebAccount = r.GetInt32(15) != 0,
        AuthenticationLevel = r.GetInt32OrNull(16),
        AcceptedCertThumbprint = r.GetStringOrNull(17),
        Notes = r.GetString(18),
        LastConnectedUtc = r.GetUtcOrNull(19),
        CreatedUtc = r.GetUtc(20),
    };
}
```

- [ ] **Step 4: Vérifier que ça passe**

Run: `dotnet test tests/RemoteDeck.Core.Tests`
Expected: 23 tests verts.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteDeck.Core/Data/ConnectionRepository.cs tests/RemoteDeck.Core.Tests/Data/ConnectionRepositoryTests.cs
git commit -m "feat(core): connection repository with favorites-first ordering and FK set-null"
```

---

### Task 5: Base créée au démarrage de l'App + CI actions v5

**Files:**
- Modify: `src/RemoteDeck.App/App.xaml.cs`, `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `SqliteDatabase.DefaultPath()`, `EnsureCreated()`, `SchemaMigrator.CurrentVersion`, `SchemaTooNewException`, `ProbeLog.Write`.
- Produces: `public SqliteDatabase Database { get; }` sur `App` (accès `((App)System.Windows.Application.Current).Database` pour les lots 2–3).

- [ ] **Step 1: Modifier `App.xaml.cs`**

Remplacer le contenu par :

```csharp
using System.Windows;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Data;

namespace RemoteDeck.App;

public partial class App : System.Windows.Application
{
    /// <summary>The local database, opened and migrated at startup (spec §4).</summary>
    public SqliteDatabase Database { get; } = new(SqliteDatabase.DefaultPath());

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProbeLog.Write("startup", $"RemoteDeck starting, log at {ProbeLog.Path}");
        try
        {
            Database.EnsureCreated();
            ProbeLog.Write("startup", $"Database ready at {Database.Path}, schema v{SchemaMigrator.CurrentVersion}");
        }
        catch (SchemaTooNewException ex)
        {
            // Refusing to touch a newer database is the safe outcome; the shell still opens for RDP-only use.
            ProbeLog.Write("startup", $"Database not opened: {ex.Message}");
        }
        catch (Exception ex)
        {
            ProbeLog.Write("startup", $"Database initialisation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
```

(Le `ShellWindow` n'affiche pas encore l'état de la base : la liste des connexions arrive au lot 3. Le message ne doit jamais être une `MessageBox`.)

- [ ] **Step 2: `ci.yml` — actions v5**

Remplacer `actions/checkout@v4` par `actions/checkout@v5` et `actions/setup-dotnet@v4` par `actions/setup-dotnet@v5` (annotation de dépréciation Node 20 sur le premier run CI).

- [ ] **Step 3: Build, tests, lancement**

Run: `dotnet build RemoteDeck.sln && dotnet test RemoteDeck.sln`
Expected: 0 avertissement, 23 tests verts.

Lancer l'app ~10 s, puis lire `%APPDATA%\RemoteDeck\logs\probe-l0.log` : ligne `[startup] Database ready at …\RemoteDeck\connections.db, schema v1` ; le fichier `connections.db` existe. Fermer (WM_CLOSE, exit 0).

- [ ] **Step 4: Commit**

```bash
git add src/RemoteDeck.App/App.xaml.cs .github/workflows/ci.yml
git commit -m "feat(app): create and migrate the local database at startup; bump CI actions to v5"
```

---

## Auto-revue du plan

**Couverture spec** : §4 schéma verbatim (T2), `ON DELETE SET NULL` testé (T4), migrations + refus version future (T2), WAL + FK par connexion (T2), chemin `%APPDATA%` (T2/T5), tri favoris (T4), Core sans Windows (T1 csproj — `Microsoft.Data.Sqlite` est multiplateforme), §10 tests repositories/migration (T2–T4). Hors lot : coffre DPAPI (L2), UI (L3), `SettingsStore` json (L3 — spec §7.2, préférences d'affichage, inutile sans UI).

**Cohérence des types** : `SqliteDatabase(string)`/`Open()`/`EnsureCreated()` consommés à l'identique en T3/T4/T5 ; `SqliteExtensions.Cmd/Add/GetUtc/ToDb` utilisés partout avec les mêmes noms ; `Credential`/`Connection` avec `required` sur les colonnes `NOT NULL` sans default ; compte de tests 5 → 10 → 16 → 23.

**Points d'attention pour l'exécutant** : `PRAGMA journal_mode` retourne `"wal"` en minuscules ; `ExecuteScalar()` de `COUNT(*)` retourne `long` ; `required` + initialiseur d'objet dans les tests ; `Thread.Sleep(5)` en T3 pour garantir `ModifiedUtc` strictement croissant.
