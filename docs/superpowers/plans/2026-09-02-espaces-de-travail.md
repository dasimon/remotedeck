# Espaces de travail — plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ouvrir d'un geste un ensemble de sessions RDP déjà disposé sur les écrans, nommé et rejouable, plus une reprise optionnelle de la dernière session au démarrage.

**Architecture:** La décision de montage est une fonction pure dans `RemoteDeck.Core` (`WorkspacePlan`), au même titre que `ClosePlan`, `ScreenFit` et `ReconnectPolicy` — c'est ce qui la rend testable sans WPF. La persistance des espaces nommés passe par un schéma SQLite V2 et un `WorkspaceRepository` calqué sur `ConnectionRepository` ; la reprise de dernière session reste dans `settings.json`. `RemoteDeck.App` ne fait qu'exécuter le plan en réutilisant les chemins `Connect`, `DetachTab` et `Reattach` existants — aucun nouveau chemin de connexion.

**Tech Stack:** .NET 10, C#, WPF + WPF-UI (Fluent), Microsoft.Data.Sqlite (bundle `e_sqlite3`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-02-espaces-de-travail-design.md`

## Global Constraints

- **Branche** : à décider avec David avant la tâche 1. La branche courante est `app-icon`, dont le nom ne correspond plus au contenu ; elle porte déjà les modifications non commitées du double-clic et du sélecteur plein écran.
- **Aucune chaîne d'interface en dur.** Tout passe par `src/RemoteDeck.App/Resources/Strings.resx` **et** `Strings.fr.resx`. Le fichier `Strings.Designer.cs` est régénéré par le build.
- **Zéro avertissement de compilation.** `dotnet build RemoteDeck.sln` doit rester à `0 Avertissement(s)`.
- **Baseline de tests : 171 verts** au 2026-09-01. Aucun test existant ne doit passer au rouge.
- **Scripts de migration forward-only.** Ne jamais éditer un script livré dans `SchemaMigrator.Scripts` ; en ajouter un. `CurrentVersion` est dérivé de `Scripts.Length` et se met à jour tout seul.
- **`DateTime` en base** : toujours via `.ToDb()` (ISO-8601 round-trip, UTC). Lecture via `GetUtc()` / `GetUtcOrNull()`.
- **`RemoteDeck.Core` ne référence jamais WPF.** Ni `System.Windows`, ni `Rect`, ni `Screen`. Les écrans arrivent en `ScreenBounds`.
- **Une connexion a au plus un onglet.** Invariant existant de `SessionsViewModel.Find` ; le plan s'appuie dessus.
- **Un espace ne ferme jamais une session.** Aucune tâche n'appelle le protocole §6.5.
- Commandes de vérification : `dotnet build RemoteDeck.sln` et `dotnet test RemoteDeck.sln`. Un test seul : `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~NomDuTest"`.

---

### Task 1: Schéma V2 — tables `Workspace` et `WorkspaceItem`

Persistance nue, vérifiée en SQL brut. Aucun modèle C# encore : ce qui est testé ici, c'est que la migration s'applique sur une base V1 peuplée et que le CASCADE mord réellement.

**Files:**
- Modify: `src/RemoteDeck.Core/Data/SchemaMigrator.cs` (tableau `Scripts`)
- Test: `tests/RemoteDeck.Core.Tests/Data/SchemaMigratorTests.cs`

**Interfaces:**
- Consumes: `SqliteDatabase.EnsureCreated()`, `SqliteDatabase.Open()`, `SchemaMigrator.CurrentVersion`, `SchemaMigrator.GetVersion(SqliteConnection)`, `TempDatabase` (helper de test existant).
- Produces: tables `Workspace(Id, Name, AutoConnect, CreatedUtc)` et `WorkspaceItem(WorkspaceId, ConnectionId, Ordinal, Detached, Left, Top, Width, Height, FullScreen)`. `SchemaMigrator.CurrentVersion` passe de 1 à 2.

- [ ] **Step 1: Écrire les tests qui échouent**

Ajouter ces trois tests à la fin de la classe `SchemaMigratorTests` dans `tests/RemoteDeck.Core.Tests/Data/SchemaMigratorTests.cs` :

```csharp
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
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~SchemaMigratorTests"`
Expected: FAIL — `SqliteException: no such table: Workspace` sur les trois nouveaux tests. Les cinq tests existants de la classe restent verts.

- [ ] **Step 3: Ajouter le script V2**

Dans `src/RemoteDeck.Core/Data/SchemaMigrator.cs`, ajouter une seconde entrée au tableau `Scripts`, **après** la V1, sans toucher à la V1 :

```csharp
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
```

`CurrentVersion` vaut alors 2 sans autre modification : il est dérivé de `Scripts.Length`.

- [ ] **Step 4: Lancer les tests pour vérifier qu'ils passent**

Run: `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~SchemaMigratorTests"`
Expected: PASS, 8 tests. En particulier `Open_enables_foreign_keys_and_wal` reste vert — c'est lui qui garantit que le CASCADE mord (spec §3.1).

- [ ] **Step 5: Lancer la suite complète**

Run: `dotnet test RemoteDeck.sln`
Expected: PASS, 174 tests (171 + 3).

- [ ] **Step 6: Commit**

```bash
git add src/RemoteDeck.Core/Data/SchemaMigrator.cs tests/RemoteDeck.Core.Tests/Data/SchemaMigratorTests.cs
git commit -m "feat(db): schema V2 with Workspace and WorkspaceItem tables"
```

---

### Task 2: Modèles et `WorkspaceRepository`

**Files:**
- Create: `src/RemoteDeck.Core/Model/Workspace.cs`
- Create: `src/RemoteDeck.Core/Data/WorkspaceRepository.cs`
- Test: `tests/RemoteDeck.Core.Tests/Data/WorkspaceRepositoryTests.cs`

**Interfaces:**
- Consumes: schéma V2 (tâche 1), `SqliteDatabase`, `DetachedWindowPlacement(double Left, double Top, double Width, double Height, bool FullScreen)` de `RemoteDeck.Core.Settings`, les extensions internes `Cmd`, `Add`, `GetUtc`, `ToDb`.
- Produces:
  - `RemoteDeck.Core.Model.Workspace` — `long Id`, `string Name`, `bool AutoConnect`, `DateTime CreatedUtc`, `List<WorkspaceItem> Items`.
  - `RemoteDeck.Core.Model.WorkspaceItem` — `long ConnectionId`, `int Ordinal`, `bool Detached`, `DetachedWindowPlacement? Placement`.
  - `RemoteDeck.Core.Data.WorkspaceRepository(SqliteDatabase db)` — `long Save(Workspace x)`, `IReadOnlyList<Workspace> GetAll()`, `Workspace? Get(long id)`, `Workspace? FindByName(string name)`, `void Delete(long id)`.

- [ ] **Step 1: Écrire les tests qui échouent**

Créer `tests/RemoteDeck.Core.Tests/Data/WorkspaceRepositoryTests.cs` :

```csharp
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Tests.Data;

public sealed class WorkspaceRepositoryTests
{
    /// <summary>Une base prête, avec deux connexions dont les espaces peuvent se servir.</summary>
    private static (TempDatabase Tmp, WorkspaceRepository Repo, long A, long B) Fixture()
    {
        var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();
        var connections = new ConnectionRepository(tmp.Db);
        long a = connections.Insert(new Connection { Name = "SQL", Host = "fdcsql00001" });
        long b = connections.Insert(new Connection { Name = "APP", Host = "fdcapp00003" });
        return (tmp, new WorkspaceRepository(tmp.Db), a, b);
    }

    [Fact]
    public void Save_then_Get_round_trips_items_and_placement()
    {
        var (tmp, repo, a, b) = Fixture();
        using var _ = tmp;
        var workspace = new Workspace
        {
            Name = "PROD",
            AutoConnect = false,
            Items =
            [
                new WorkspaceItem { ConnectionId = a, Ordinal = 0, Detached = false },
                new WorkspaceItem
                {
                    ConnectionId = b,
                    Ordinal = 1,
                    Detached = true,
                    Placement = new DetachedWindowPlacement(100, 200, 1280, 800, FullScreen: true),
                },
            ],
        };

        long id = repo.Save(workspace);
        var read = repo.Get(id);

        Assert.NotNull(read);
        Assert.Equal("PROD", read.Name);
        Assert.False(read.AutoConnect);
        Assert.Equal(2, read.Items.Count);
        Assert.Equal(a, read.Items[0].ConnectionId);
        Assert.False(read.Items[0].Detached);
        Assert.Null(read.Items[0].Placement);
        Assert.True(read.Items[1].Detached);
        Assert.Equal(new DetachedWindowPlacement(100, 200, 1280, 800, true), read.Items[1].Placement);
    }

    [Fact]
    public void Save_preserves_item_order()
    {
        var (tmp, repo, a, b) = Fixture();
        using var _ = tmp;
        var workspace = new Workspace
        {
            Name = "PROD",
            Items =
            [
                new WorkspaceItem { ConnectionId = b, Ordinal = 0 },
                new WorkspaceItem { ConnectionId = a, Ordinal = 1 },
            ],
        };

        var read = repo.Get(repo.Save(workspace))!;

        Assert.Equal([b, a], read.Items.Select(i => i.ConnectionId));
    }

    [Fact]
    public void Save_replaces_a_workspace_of_the_same_name_instead_of_duplicating()
    {
        var (tmp, repo, a, b) = Fixture();
        using var _ = tmp;
        long first = repo.Save(new Workspace { Name = "PROD", Items = [new WorkspaceItem { ConnectionId = a, Ordinal = 0 }] });

        long second = repo.Save(new Workspace { Name = "PROD", Items = [new WorkspaceItem { ConnectionId = b, Ordinal = 0 }] });

        Assert.Equal(first, second);              // le même espace, réécrit — pas un second
        Assert.Single(repo.GetAll());
        Assert.Equal([b], repo.Get(first)!.Items.Select(i => i.ConnectionId));
    }

    [Fact]
    public void FindByName_is_case_insensitive_and_returns_null_when_absent()
    {
        var (tmp, repo, a, _) = Fixture();
        using var __ = tmp;
        repo.Save(new Workspace { Name = "PROD", Items = [new WorkspaceItem { ConnectionId = a, Ordinal = 0 }] });

        Assert.NotNull(repo.FindByName("prod"));
        Assert.Null(repo.FindByName("RECETTE"));
    }

    [Fact]
    public void Delete_removes_the_workspace_and_its_items()
    {
        var (tmp, repo, a, _) = Fixture();
        using var __ = tmp;
        long id = repo.Save(new Workspace { Name = "PROD", Items = [new WorkspaceItem { ConnectionId = a, Ordinal = 0 }] });

        repo.Delete(id);

        Assert.Null(repo.Get(id));
        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void Delete_of_an_unknown_id_is_a_no_op()
    {
        var (tmp, repo, _, _) = Fixture();
        using var __ = tmp;

        repo.Delete(4242);   // ne doit pas lever

        Assert.Empty(repo.GetAll());
    }

    [Fact]
    public void Deleting_a_connection_drops_it_from_the_workspace_but_keeps_the_workspace()
    {
        var (tmp, repo, a, b) = Fixture();
        using var _ = tmp;
        long id = repo.Save(new Workspace
        {
            Name = "PROD",
            Items = [new WorkspaceItem { ConnectionId = a, Ordinal = 0 }, new WorkspaceItem { ConnectionId = b, Ordinal = 1 }],
        });

        new ConnectionRepository(tmp.Db).Delete(a);

        var read = repo.Get(id);
        Assert.NotNull(read);
        Assert.Equal([b], read.Items.Select(i => i.ConnectionId));
    }

    [Fact]
    public void GetAll_orders_by_name()
    {
        var (tmp, repo, a, _) = Fixture();
        using var __ = tmp;
        repo.Save(new Workspace { Name = "RECETTE", Items = [new WorkspaceItem { ConnectionId = a, Ordinal = 0 }] });
        repo.Save(new Workspace { Name = "prod", Items = [new WorkspaceItem { ConnectionId = a, Ordinal = 0 }] });

        Assert.Equal(["prod", "RECETTE"], repo.GetAll().Select(w => w.Name));
    }
}
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~WorkspaceRepositoryTests"`
Expected: FAIL à la compilation — `Workspace`, `WorkspaceItem` et `WorkspaceRepository` n'existent pas.

- [ ] **Step 3: Écrire les modèles**

Créer `src/RemoteDeck.Core/Model/Workspace.cs` :

```csharp
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Model;

/// <summary>
/// Un jeu de connexions et la disposition qu'elles avaient quand l'utilisateur l'a capturé
/// (spec espaces §3). Le nom est unique : c'est la seule façon de désigner un espace dans la palette.
/// </summary>
public sealed class Workspace
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Connecter les sessions au montage. Réglé à la capture, nulle part ailleurs (§4.4).</summary>
    public bool AutoConnect { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    /// <summary>Les connexions de l'espace, dans l'ordre du ruban. Jamais nul.</summary>
    public List<WorkspaceItem> Items { get; set; } = [];
}

/// <summary>
/// Une connexion dans un espace, et l'état dans lequel l'espace la veut.
/// </summary>
/// <remarks>
/// <see cref="Placement"/> est nul pour un item ancré — une session ancrée n'a pas de fenêtre à
/// placer — et peut l'être aussi pour un item détaché dont la place n'a jamais été enregistrée ;
/// la mémorisation par connexion de <c>settings.json</c> sert alors de repli (spec §7).
/// </remarks>
public sealed class WorkspaceItem
{
    public long ConnectionId { get; set; }

    /// <summary>Position dans le ruban, à partir de 0.</summary>
    public int Ordinal { get; set; }

    public bool Detached { get; set; }

    public DetachedWindowPlacement? Placement { get; set; }
}
```

- [ ] **Step 4: Écrire le repository**

Créer `src/RemoteDeck.Core/Data/WorkspaceRepository.cs` :

```csharp
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Data;

/// <summary>
/// Lecture et écriture des espaces de travail (spec espaces §3.1). Un espace est toujours écrit
/// entier : il n'y a pas d'éditeur, donc pas de mise à jour partielle à représenter.
/// </summary>
public sealed class WorkspaceRepository(SqliteDatabase db)
{
    /// <summary>
    /// Insère l'espace, ou remplace intégralement celui qui porte déjà ce nom. Le remplacement est
    /// la manière normale de faire évoluer un espace (spec §5), et il conserve l'<c>Id</c> existant
    /// pour que rien ne pointe dans le vide.
    /// </summary>
    /// <returns>L'id de l'espace écrit.</returns>
    public long Save(Workspace x)
    {
        ArgumentNullException.ThrowIfNull(x);

        using var c = db.Open();
        using var tx = c.BeginTransaction();

        var find = c.Cmd("SELECT Id FROM Workspace WHERE Name = $name COLLATE NOCASE");
        find.Transaction = tx;
        find.Add("$name", x.Name);
        long id = find.ExecuteScalar() is { } found and not DBNull ? Convert.ToInt64(found) : 0;

        if (id == 0)
        {
            var now = DateTime.UtcNow;
            var insert = c.Cmd("""
                INSERT INTO Workspace (Name, AutoConnect, CreatedUtc) VALUES ($name, $auto, $created);
                SELECT last_insert_rowid();
                """);
            insert.Transaction = tx;
            insert.Add("$name", x.Name);
            insert.Add("$auto", x.AutoConnect ? 1 : 0);
            insert.Add("$created", now.ToDb());
            id = (long)insert.ExecuteScalar()!;
            x.CreatedUtc = now;
        }
        else
        {
            var update = c.Cmd("UPDATE Workspace SET Name = $name, AutoConnect = $auto WHERE Id = $id");
            update.Transaction = tx;
            update.Add("$name", x.Name);
            update.Add("$auto", x.AutoConnect ? 1 : 0);
            update.Add("$id", id);
            update.ExecuteNonQuery();

            // Les items sont réécrits en bloc : c'est ce que « remplacer » veut dire ici, et cela
            // évite d'avoir à calculer une différence pour une liste qui fait deux à six lignes.
            var clear = c.Cmd("DELETE FROM WorkspaceItem WHERE WorkspaceId = $id");
            clear.Transaction = tx;
            clear.Add("$id", id);
            clear.ExecuteNonQuery();
        }

        foreach (var item in x.Items)
        {
            var cmd = c.Cmd("""
                INSERT INTO WorkspaceItem (WorkspaceId, ConnectionId, Ordinal, Detached, Left, Top, Width, Height, FullScreen)
                VALUES ($ws, $conn, $ord, $detached, $left, $top, $width, $height, $full)
                """);
            cmd.Transaction = tx;
            cmd.Add("$ws", id);
            cmd.Add("$conn", item.ConnectionId);
            cmd.Add("$ord", item.Ordinal);
            cmd.Add("$detached", item.Detached ? 1 : 0);
            cmd.Add("$left", item.Placement?.Left);
            cmd.Add("$top", item.Placement?.Top);
            cmd.Add("$width", item.Placement?.Width);
            cmd.Add("$height", item.Placement?.Height);
            cmd.Add("$full", item.Placement?.FullScreen == true ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        x.Id = id;
        return id;
    }

    public Workspace? Get(long id)
    {
        using var c = db.Open();
        var cmd = c.Cmd("SELECT Id, Name, AutoConnect, CreatedUtc FROM Workspace WHERE Id = $id");
        cmd.Add("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var workspace = ReadWorkspace(r);
        r.Close();
        LoadItems(c, workspace);
        return workspace;
    }

    public Workspace? FindByName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        using var c = db.Open();
        var cmd = c.Cmd("SELECT Id, Name, AutoConnect, CreatedUtc FROM Workspace WHERE Name = $name COLLATE NOCASE");
        cmd.Add("$name", name);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var workspace = ReadWorkspace(r);
        r.Close();
        LoadItems(c, workspace);
        return workspace;
    }

    public IReadOnlyList<Workspace> GetAll()
    {
        using var c = db.Open();
        var list = new List<Workspace>();
        using (var r = c.Cmd("SELECT Id, Name, AutoConnect, CreatedUtc FROM Workspace ORDER BY Name COLLATE NOCASE").ExecuteReader())
        {
            while (r.Read()) list.Add(ReadWorkspace(r));
        }

        foreach (var workspace in list) LoadItems(c, workspace);
        return list;
    }

    /// <summary>Idempotent, comme <see cref="ConnectionRepository.Delete"/> : un id déjà parti est
    /// un non-événement. Les items partent par cascade.</summary>
    public void Delete(long id)
    {
        using var c = db.Open();
        var cmd = c.Cmd("DELETE FROM Workspace WHERE Id = $id");
        cmd.Add("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static Workspace ReadWorkspace(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        AutoConnect = r.GetInt64(2) != 0,
        CreatedUtc = r.GetUtc(3),
    };

    private static void LoadItems(SqliteConnection c, Workspace workspace)
    {
        var cmd = c.Cmd("""
            SELECT ConnectionId, Ordinal, Detached, Left, Top, Width, Height, FullScreen
            FROM WorkspaceItem WHERE WorkspaceId = $id ORDER BY Ordinal
            """);
        cmd.Add("$id", workspace.Id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // Une place n'existe que si les quatre coordonnées sont là. Un item détaché dont la
            // place n'a jamais été enregistrée est légitime : le repli par connexion s'en charge.
            DetachedWindowPlacement? placement = r.IsDBNull(3) || r.IsDBNull(4) || r.IsDBNull(5) || r.IsDBNull(6)
                ? null
                : new DetachedWindowPlacement(r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetInt64(7) != 0);

            workspace.Items.Add(new WorkspaceItem
            {
                ConnectionId = r.GetInt64(0),
                Ordinal = r.GetInt32(1),
                Detached = r.GetInt64(2) != 0,
                Placement = placement,
            });
        }
    }
}
```

Ajouter en tête du fichier les `using Microsoft.Data.Sqlite;` nécessaires à `SqliteConnection` et `SqliteDataReader`.

- [ ] **Step 5: Lancer les tests pour vérifier qu'ils passent**

Run: `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~WorkspaceRepositoryTests"`
Expected: PASS, 8 tests.

- [ ] **Step 6: Lancer la suite complète**

Run: `dotnet test RemoteDeck.sln`
Expected: PASS, 182 tests.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteDeck.Core/Model/Workspace.cs src/RemoteDeck.Core/Data/WorkspaceRepository.cs tests/RemoteDeck.Core.Tests/Data/WorkspaceRepositoryTests.cs
git commit -m "feat(core): Workspace model and repository"
```

---

### Task 3: `WorkspacePlan` — la décision de montage

Le cœur de la fonctionnalité, et la seule partie de la logique de montage qui soit testable. Implémente la table §4.1 et la table §4.1.1 de la spec.

**Files:**
- Create: `src/RemoteDeck.Core/Sessions/WorkspacePlan.cs`
- Test: `tests/RemoteDeck.Core.Tests/Sessions/WorkspacePlanTests.cs`

**Interfaces:**
- Consumes: `Workspace`, `WorkspaceItem` (tâche 2), `ScreenFit.Choose(DetachedWindowPlacement?, IReadOnlyList<ScreenBounds>, double, double)`, `ScreenBounds(double Left, double Top, double Width, double Height)`.
- Produces:
  - `RemoteDeck.Core.Sessions.WorkspaceActionKind` — `Activate`, `MoveDetached`, `Detach`, `Reattach`, `OpenDocked`, `OpenDetached`.
  - `RemoteDeck.Core.Sessions.WorkspaceAction(WorkspaceActionKind Kind, long ConnectionId, DetachedWindowPlacement? Placement)`.
  - `RemoteDeck.Core.Sessions.WorkspacePlan.Build(Workspace workspace, IReadOnlySet<long> existingConnectionIds, IReadOnlyDictionary<long, bool> openSessions, IReadOnlyList<ScreenBounds> screens)` → `IReadOnlyList<WorkspaceAction>`.

- [ ] **Step 1: Écrire les tests qui échouent**

Créer `tests/RemoteDeck.Core.Tests/Sessions/WorkspacePlanTests.cs` :

```csharp
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Sessions;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Tests.Sessions;

public sealed class WorkspacePlanTests
{
    private static readonly IReadOnlyList<ScreenBounds> OneScreen = [new ScreenBounds(0, 0, 1920, 1040)];

    private static Workspace With(params WorkspaceItem[] items) =>
        new() { Id = 1, Name = "PROD", Items = [.. items] };

    private static WorkspaceItem Docked(long id, int ordinal = 0) =>
        new() { ConnectionId = id, Ordinal = ordinal, Detached = false };

    private static WorkspaceItem Detached(long id, int ordinal = 0, DetachedWindowPlacement? at = null) =>
        new() { ConnectionId = id, Ordinal = ordinal, Detached = true, Placement = at ?? new DetachedWindowPlacement(100, 100, 1280, 800, false) };

    [Fact]
    public void A_connection_with_no_session_and_a_docked_item_is_opened_docked()
    {
        var plan = WorkspacePlan.Build(With(Docked(7)), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.OpenDocked, action.Kind);
        Assert.Equal(7, action.ConnectionId);
        Assert.Null(action.Placement);
    }

    [Fact]
    public void A_connection_with_no_session_and_a_detached_item_is_opened_detached_at_the_fitted_rectangle()
    {
        var plan = WorkspacePlan.Build(With(Detached(7)), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.OpenDetached, action.Kind);
        Assert.Equal(new DetachedWindowPlacement(100, 100, 1280, 800, false), action.Placement);
    }

    [Fact]
    public void An_open_docked_session_wanted_docked_is_only_activated()
    {
        var open = new Dictionary<long, bool> { [7] = false };   // false = ancrée

        var plan = WorkspacePlan.Build(With(Docked(7)), new HashSet<long> { 7 }, open, OneScreen);

        Assert.Equal(WorkspaceActionKind.Activate, Assert.Single(plan).Kind);
    }

    [Fact]
    public void An_open_docked_session_wanted_detached_is_detached()
    {
        var open = new Dictionary<long, bool> { [7] = false };

        var plan = WorkspacePlan.Build(With(Detached(7)), new HashSet<long> { 7 }, open, OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.Detach, action.Kind);
        Assert.Equal(new DetachedWindowPlacement(100, 100, 1280, 800, false), action.Placement);
    }

    [Fact]
    public void An_open_detached_session_wanted_docked_is_reattached()
    {
        var open = new Dictionary<long, bool> { [7] = true };    // true = détachée

        var plan = WorkspacePlan.Build(With(Docked(7)), new HashSet<long> { 7 }, open, OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.Reattach, action.Kind);
        Assert.Null(action.Placement);
    }

    [Fact]
    public void An_open_detached_session_wanted_detached_is_moved()
    {
        var open = new Dictionary<long, bool> { [7] = true };

        var plan = WorkspacePlan.Build(With(Detached(7)), new HashSet<long> { 7 }, open, OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.MoveDetached, action.Kind);
        Assert.Equal(new DetachedWindowPlacement(100, 100, 1280, 800, false), action.Placement);
    }

    [Fact]
    public void A_deleted_connection_is_skipped_silently()
    {
        var plan = WorkspacePlan.Build(With(Docked(7), Docked(8, 1)), new HashSet<long> { 8 }, new Dictionary<long, bool>(), OneScreen);

        Assert.Equal(8, Assert.Single(plan).ConnectionId);
    }

    [Fact]
    public void An_empty_workspace_yields_no_action()
    {
        Assert.Empty(WorkspacePlan.Build(With(), new HashSet<long>(), new Dictionary<long, bool>(), OneScreen));
    }

    [Fact]
    public void Actions_follow_the_item_order()
    {
        var workspace = With(Docked(9, 1), Docked(7, 0));   // volontairement dans le désordre

        var plan = WorkspacePlan.Build(workspace, new HashSet<long> { 7, 9 }, new Dictionary<long, bool>(), OneScreen);

        Assert.Equal([7, 9], plan.Select(a => a.ConnectionId));
    }

    [Fact]
    public void A_placement_on_a_screen_that_is_gone_falls_back_to_no_placement()
    {
        // Fenêtre mémorisée sur un second écran à droite, débranché depuis.
        var item = Detached(7, at: new DetachedWindowPlacement(3000, 100, 1280, 800, false));

        var plan = WorkspacePlan.Build(With(item), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.OpenDetached, action.Kind);
        // Pas de rectangle : l'appelant retombe sur la mémorisation par connexion, puis sur le centrage.
        Assert.Null(action.Placement);
    }

    [Fact]
    public void A_detached_item_without_a_placement_yields_no_placement()
    {
        var item = new WorkspaceItem { ConnectionId = 7, Ordinal = 0, Detached = true, Placement = null };

        var plan = WorkspacePlan.Build(With(item), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen);

        var action = Assert.Single(plan);
        Assert.Equal(WorkspaceActionKind.OpenDetached, action.Kind);
        Assert.Null(action.Placement);
    }

    [Fact]
    public void A_placement_larger_than_the_screen_is_clamped_into_it()
    {
        var item = Detached(7, at: new DetachedWindowPlacement(0, 0, 4000, 3000, false));

        var action = Assert.Single(WorkspacePlan.Build(With(item), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen));

        Assert.Equal(1920, action.Placement!.Width);
        Assert.Equal(1040, action.Placement.Height);
    }

    [Fact]
    public void Full_screen_survives_the_fit()
    {
        var item = Detached(7, at: new DetachedWindowPlacement(100, 100, 1280, 800, FullScreen: true));

        var action = Assert.Single(WorkspacePlan.Build(With(item), new HashSet<long> { 7 }, new Dictionary<long, bool>(), OneScreen));

        Assert.True(action.Placement!.FullScreen);
    }

    [Fact]
    public void Build_rejects_a_null_workspace()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkspacePlan.Build(null!, new HashSet<long>(), new Dictionary<long, bool>(), OneScreen));
    }
}
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~WorkspacePlanTests"`
Expected: FAIL à la compilation — `WorkspacePlan`, `WorkspaceAction` et `WorkspaceActionKind` n'existent pas.

- [ ] **Step 3: Écrire l'implémentation**

Créer `src/RemoteDeck.Core/Sessions/WorkspacePlan.cs` :

```csharp
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Sessions;

/// <summary>Ce qu'il faut faire d'une connexion pour monter un espace.</summary>
public enum WorkspaceActionKind
{
    /// <summary>La session est déjà là et déjà dans le bon conteneur : l'amener au premier plan.</summary>
    Activate = 0,
    /// <summary>La session est détachée et l'espace la veut détachée : l'amener au premier plan et la replacer.</summary>
    MoveDetached = 1,
    /// <summary>La session est ancrée et l'espace la veut détachée.</summary>
    Detach = 2,
    /// <summary>La session est détachée et l'espace la veut ancrée.</summary>
    Reattach = 3,
    /// <summary>Pas de session : ouvrir un onglet.</summary>
    OpenDocked = 4,
    /// <summary>Pas de session : ouvrir puis détacher.</summary>
    OpenDetached = 5,
}

/// <summary>
/// Une action du montage. <paramref name="Placement"/> est le rectangle déjà ajusté aux écrans
/// présents, ou <c>null</c> quand l'espace n'en a pas ou que celui qu'il avait appartient à un
/// écran disparu — l'appelant retombe alors sur la mémorisation par connexion, puis sur le centrage,
/// exactement comme un détachement ordinaire.
/// </summary>
public sealed record WorkspaceAction(WorkspaceActionKind Kind, long ConnectionId, DetachedWindowPlacement? Placement);

/// <summary>
/// Traduit un espace en une liste d'actions, en fonction des connexions qui existent encore, des
/// sessions déjà ouvertes et des écrans présents maintenant (spec espaces §4.1).
///
/// Pur : pas d'E/S, pas d'interface, pas d'état. C'est la raison d'être de ce type — la décision se
/// teste, l'exécution WPF non.
/// </summary>
public static class WorkspacePlan
{
    /// <param name="existingConnectionIds">Les connexions qui existent encore en base. Un item qui
    /// n'y est pas est ignoré en silence : la cascade a pu le retirer entre la lecture et ici, et
    /// c'est une course, pas une erreur de l'utilisateur.</param>
    /// <param name="openSessions">Id de connexion → la session est-elle détachée. Une connexion a au
    /// plus une session, invariant de <c>SessionsViewModel.Find</c>.</param>
    public static IReadOnlyList<WorkspaceAction> Build(
        Workspace workspace,
        IReadOnlySet<long> existingConnectionIds,
        IReadOnlyDictionary<long, bool> openSessions,
        IReadOnlyList<ScreenBounds> screens)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(existingConnectionIds);
        ArgumentNullException.ThrowIfNull(openSessions);
        ArgumentNullException.ThrowIfNull(screens);

        var actions = new List<WorkspaceAction>();

        foreach (var item in workspace.Items.OrderBy(i => i.Ordinal))
        {
            if (!existingConnectionIds.Contains(item.ConnectionId)) continue;

            // Ajusté ici une fois pour toutes : aucune branche ci-dessous n'a à savoir ce qu'est un
            // écran. Un item ancré n'a pas de place, et ScreenFit rend null sur une entrée nulle.
            var placement = item.Detached ? ScreenFit.Choose(item.Placement, screens) : null;

            var kind = openSessions.TryGetValue(item.ConnectionId, out bool isDetached)
                ? (isDetached, item.Detached) switch
                {
                    (true, true) => WorkspaceActionKind.MoveDetached,
                    (false, true) => WorkspaceActionKind.Detach,
                    (true, false) => WorkspaceActionKind.Reattach,
                    (false, false) => WorkspaceActionKind.Activate,
                }
                : item.Detached ? WorkspaceActionKind.OpenDetached : WorkspaceActionKind.OpenDocked;

            actions.Add(new WorkspaceAction(kind, item.ConnectionId, placement));
        }

        return actions;
    }
}
```

- [ ] **Step 4: Lancer les tests pour vérifier qu'ils passent**

Run: `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~WorkspacePlanTests"`
Expected: PASS, 14 tests.

- [ ] **Step 5: Lancer la suite complète**

Run: `dotnet test RemoteDeck.sln`
Expected: PASS, 196 tests.

- [ ] **Step 6: Commit**

```bash
git add src/RemoteDeck.Core/Sessions/WorkspacePlan.cs tests/RemoteDeck.Core.Tests/Sessions/WorkspacePlanTests.cs
git commit -m "feat(core): WorkspacePlan turns a workspace into mount actions"
```

---

### Task 4: Reprise de la dernière session dans `settings.json`

**Files:**
- Modify: `src/RemoteDeck.Core/Settings/AppSettings.cs`
- Modify: `src/RemoteDeck.Core/Settings/SettingsStore.cs:33` (le bloc de normalisation d'après `Deserialize`)
- Test: `tests/RemoteDeck.Core.Tests/Settings/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `SettingsStore.Load()` / `Save(AppSettings)`, `DetachedWindowPlacement`.
- Produces:
  - `RemoteDeck.Core.Settings.LastSessionEntry` — `long ConnectionId`, `int Ordinal`, `bool Detached`, `DetachedWindowPlacement? Placement`.
  - `AppSettings.RestoreLastSession` (`bool`, défaut `false`), `AppSettings.LastSession` (`List<LastSessionEntry>`, jamais nul après `Load()`).

- [ ] **Step 1: Écrire les tests qui échouent**

Ajouter à la classe existante `SettingsStoreTests` dans `tests/RemoteDeck.Core.Tests/Settings/SettingsStoreTests.cs`. Les tests existants montrent le motif de fichier temporaire à réutiliser ; si un helper y existe déjà, s'en servir plutôt que d'en créer un second.

```csharp
    [Fact]
    public void RestoreLastSession_defaults_to_false()
    {
        // Ouvrir RemoteDeck ne doit se connecter à rien tant que l'utilisateur ne l'a pas demandé
        // (spec espaces §7).
        Assert.False(new AppSettings().RestoreLastSession);
    }

    [Fact]
    public void LastSession_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"remotedeck-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                RestoreLastSession = true,
                LastSession =
                [
                    new LastSessionEntry { ConnectionId = 7, Ordinal = 0, Detached = false },
                    new LastSessionEntry
                    {
                        ConnectionId = 9,
                        Ordinal = 1,
                        Detached = true,
                        Placement = new DetachedWindowPlacement(10, 20, 1280, 800, true),
                    },
                ],
            });

            var read = store.Load();

            Assert.True(read.RestoreLastSession);
            Assert.Equal(2, read.LastSession.Count);
            Assert.Equal(7, read.LastSession[0].ConnectionId);
            Assert.Null(read.LastSession[0].Placement);
            Assert.True(read.LastSession[1].Detached);
            Assert.Equal(new DetachedWindowPlacement(10, 20, 1280, 800, true), read.LastSession[1].Placement);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LastSession_is_never_null_even_when_the_file_says_null()
    {
        // Le fichier est éditable à la main : "lastSession": null écrase l'initialiseur de propriété.
        var path = Path.Combine(Path.GetTempPath(), $"remotedeck-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "lastSession": null, "detachedWindows": null }""");

            var read = new SettingsStore(path).Load();

            Assert.NotNull(read.LastSession);
            Assert.Empty(read.LastSession);
            Assert.NotNull(read.DetachedWindows);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: FAIL à la compilation — `RestoreLastSession`, `LastSession` et `LastSessionEntry` n'existent pas.

- [ ] **Step 3: Étendre `AppSettings`**

Ajouter à la fin de la classe `AppSettings` dans `src/RemoteDeck.Core/Settings/AppSettings.cs` :

```csharp
    /// <summary>
    /// Rouvrir au démarrage les sessions qui étaient là à la fermeture. Faux par défaut : lancer
    /// l'application ne doit se connecter à rien tant que l'utilisateur ne l'a pas demandé
    /// (spec espaces §7).
    /// </summary>
    public bool RestoreLastSession { get; set; }

    /// <summary>
    /// Ce qui était ouvert à la dernière fermeture propre, dans l'ordre du ruban. Réécrit à chaque
    /// fermeture propre et seulement là : une fermeture par crash laisse la précédente, ce qui est
    /// le comportement utile. Jamais nul après un <c>Load()</c>.
    /// </summary>
    public List<LastSessionEntry> LastSession { get; set; } = [];
}

/// <summary>
/// Une session de la dernière fermeture. Mêmes champs qu'un <c>WorkspaceItem</c> moins l'espace :
/// la reprise est de l'état de fenêtrage, pas du contenu composé, d'où sa place ici et non en base
/// (spec espaces §3).
/// </summary>
public sealed class LastSessionEntry
{
    public long ConnectionId { get; set; }

    public int Ordinal { get; set; }

    public bool Detached { get; set; }

    public DetachedWindowPlacement? Placement { get; set; }
```

(La dernière accolade fermante du fichier ferme désormais `LastSessionEntry` ; vérifier que `AppSettings` est bien refermée avant elle.)

- [ ] **Step 4: Normaliser à la lecture**

Dans `src/RemoteDeck.Core/Settings/SettingsStore.cs`, à côté de `settings.DetachedWindows ??= [];` :

```csharp
            settings.DetachedWindows ??= [];
            settings.LastSession ??= [];
```

- [ ] **Step 5: Lancer les tests pour vérifier qu'ils passent**

Run: `dotnet test RemoteDeck.sln --filter "FullyQualifiedName~SettingsStoreTests"`
Expected: PASS — les tests existants de la classe plus les 3 nouveaux.

- [ ] **Step 6: Lancer la suite complète**

Run: `dotnet test RemoteDeck.sln`
Expected: PASS, 199 tests.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteDeck.Core/Settings/AppSettings.cs src/RemoteDeck.Core/Settings/SettingsStore.cs tests/RemoteDeck.Core.Tests/Settings/SettingsStoreTests.cs
git commit -m "feat(core): remember the last session in settings.json"
```

---

### Task 5: Fenêtre de nommage et chaînes localisées

Le seul élément d'interface neuf. Aucune logique métier : un nom, une case, deux boutons.

**Files:**
- Create: `src/RemoteDeck.App/Views/WorkspaceNameWindow.xaml`
- Create: `src/RemoteDeck.App/Views/WorkspaceNameWindow.xaml.cs`
- Modify: `src/RemoteDeck.App/Resources/Strings.resx`
- Modify: `src/RemoteDeck.App/Resources/Strings.fr.resx`

**Interfaces:**
- Consumes: `Wpf.Ui.Controls.FluentWindow`, les jetons `RdSurface0`, `RdText`, `RdTextMuted`, `RdBorder`, `RdControlHeight`, `RdRadius` de `Resources/Theme.xaml`, `RemoteDeck.App.Resources.Strings`.
- Produces: `internal sealed partial class WorkspaceNameWindow : FluentWindow` avec `WorkspaceNameWindow(string? proposedName, bool autoConnect)`, `public string WorkspaceName { get; }`, `public bool AutoConnect { get; }`, et le `bool?` de `ShowDialog()`.

> **Pas de test automatisé.** Une `FluentWindow` ne s'instancie pas hors d'un `Application` WPF, et `RemoteDeck.App` n'a pas de projet de test — c'est le cas de toutes les vues du dépôt. La vérification est la case correspondante de `docs/manual-checklist.md`, ajoutée en tâche 8.

- [ ] **Step 1: Ajouter les chaînes anglaises**

Dans `src/RemoteDeck.App/Resources/Strings.resx`, ajouter ces entrées (garder l'ordre alphabétique du fichier) :

| Nom | Valeur |
|---|---|
| `WorkspaceName_Title` | `Save layout as…` |
| `WorkspaceName_NameLabel` | `Name` |
| `WorkspaceName_NamePlaceholder` | `PROD` |
| `WorkspaceName_AutoConnect` | `Connect the sessions when the workspace opens` |
| `WorkspaceName_Save` | `Save` |
| `WorkspaceName_Cancel` | `Cancel` |
| `WorkspaceName_NameRequired` | `A workspace needs a name.` |
| `WorkspaceName_ReplaceTitle` | `Replace “{0}”?` |
| `WorkspaceName_ReplaceMessage` | `A workspace called “{0}” already exists. Saving replaces what it holds — there is no undo.` |
| `WorkspaceName_Replace` | `Replace` |

- [ ] **Step 2: Ajouter les chaînes françaises**

Mêmes noms dans `src/RemoteDeck.App/Resources/Strings.fr.resx` :

| Nom | Valeur |
|---|---|
| `WorkspaceName_Title` | `Enregistrer la disposition sous…` |
| `WorkspaceName_NameLabel` | `Nom` |
| `WorkspaceName_NamePlaceholder` | `PROD` |
| `WorkspaceName_AutoConnect` | `Connecter les sessions à l'ouverture de l'espace` |
| `WorkspaceName_Save` | `Enregistrer` |
| `WorkspaceName_Cancel` | `Annuler` |
| `WorkspaceName_NameRequired` | `Un espace doit avoir un nom.` |
| `WorkspaceName_ReplaceTitle` | `Remplacer « {0} » ?` |
| `WorkspaceName_ReplaceMessage` | `Un espace nommé « {0} » existe déjà. L'enregistrer remplace son contenu — c'est sans retour.` |
| `WorkspaceName_Replace` | `Remplacer` |

- [ ] **Step 3: Écrire la vue**

Créer `src/RemoteDeck.App/Views/WorkspaceNameWindow.xaml`. Reprendre la structure de `CredentialEditorWindow.xaml` — l'ouvrir d'abord et en copier l'en-tête `FluentWindow`, sa `TitleBar` et son pied de boutons, pour que la fenêtre soit indiscernable des autres :

```xml
<ui:FluentWindow x:Class="RemoteDeck.App.Views.WorkspaceNameWindow"
                 x:ClassModifier="internal"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                 xmlns:res="clr-namespace:RemoteDeck.App.Resources"
                 Title="{x:Static res:Strings.WorkspaceName_Title}"
                 Width="420" SizeToContent="Height"
                 ResizeMode="NoResize"
                 WindowStartupLocation="CenterOwner"
                 ExtendsContentIntoTitleBar="True"
                 WindowBackdropType="Mica"
                 WindowCornerPreference="Round">
    <Grid Margin="0,0,0,16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <ui:TitleBar Grid.Row="0" Title="{x:Static res:Strings.WorkspaceName_Title}" ShowMaximize="False" ShowMinimize="False" />

        <TextBlock Grid.Row="1" Margin="16,8,16,4"
                   Text="{x:Static res:Strings.WorkspaceName_NameLabel}"
                   Foreground="{DynamicResource RdTextMuted}" />

        <ui:TextBox x:Name="NameBox" Grid.Row="2" Margin="16,0,16,12"
                    Height="{StaticResource RdControlHeight}"
                    PlaceholderText="{x:Static res:Strings.WorkspaceName_NamePlaceholder}"
                    TextChanged="OnNameChanged" />

        <CheckBox x:Name="AutoConnectBox" Grid.Row="3" Margin="16,0,16,16"
                  Content="{x:Static res:Strings.WorkspaceName_AutoConnect}"
                  Foreground="{DynamicResource RdText}" />

        <StackPanel Grid.Row="4" Orientation="Horizontal" HorizontalAlignment="Right" Margin="16,0,16,0">
            <ui:Button x:Name="SaveButton" MinWidth="96"
                       Appearance="Primary"
                       Content="{x:Static res:Strings.WorkspaceName_Save}"
                       IsDefault="True"
                       Click="OnSaveClick" />
            <ui:Button MinWidth="96" Margin="8,0,0,0"
                       Content="{x:Static res:Strings.WorkspaceName_Cancel}"
                       IsCancel="True"
                       Click="OnCancelClick" />
        </StackPanel>
    </Grid>
</ui:FluentWindow>
```

- [ ] **Step 4: Écrire le code-behind**

Créer `src/RemoteDeck.App/Views/WorkspaceNameWindow.xaml.cs` :

```csharp
using System.Windows;

namespace RemoteDeck.App.Views;

/// <summary>
/// Le nom d'un espace, et s'il connecte ses sessions à l'ouverture. La seule fenêtre que les espaces
/// ajoutent : il n'y a pas d'éditeur d'espace, un espace se capture (spec espaces §5).
/// </summary>
/// <remarks>
/// Elle ne valide que le vide. Le doublon de nom n'est pas une erreur ici — c'est la façon normale
/// de faire évoluer un espace — et il est confirmé par l'appelant, qui est le seul à avoir le
/// dépôt sous la main.
/// </remarks>
// Wpf.Ui.Controls.* est qualifié à dessein : UseWindowsForms met System.Windows.Forms dans la portée
// via les usings implicites, et un `using Wpf.Ui.Controls;` nu rendrait Button et TextBox ambigus.
internal sealed partial class WorkspaceNameWindow : Wpf.Ui.Controls.FluentWindow
{
    public WorkspaceNameWindow(string? proposedName, bool autoConnect)
    {
        InitializeComponent();

        NameBox.Text = proposedName ?? string.Empty;
        AutoConnectBox.IsChecked = autoConnect;
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

        // Le champ prend le focus et le texte est présélectionné : la fenêtre sert à taper un nom, et
        // proposer un nom c'est proposer de le remplacer d'une frappe.
        Loaded += (_, _) =>
        {
            _ = NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Le nom saisi, débarrassé de ses espaces de bord. Valide seulement après un
    /// <c>ShowDialog()</c> qui a rendu <c>true</c>.</summary>
    public string WorkspaceName { get; private set; } = string.Empty;

    public bool AutoConnect { get; private set; }

    /// <summary>Le bouton suit le champ : un espace sans nom ne peut pas être désigné dans la
    /// palette, qui est la seule façon de l'ouvrir.</summary>
    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        WorkspaceName = name;
        AutoConnect = AutoConnectBox.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
```

- [ ] **Step 5: Compiler**

Run: `dotnet build RemoteDeck.sln`
Expected: succès, `0 Avertissement(s)`. Si `ui:TextBox` ou `PlaceholderText` n'existent pas dans la version de WPF-UI du projet, s'aligner sur ce qu'utilise `ConnectionEditorWindow.xaml` plutôt que d'inventer.

- [ ] **Step 6: Lancer la suite complète**

Run: `dotnet test RemoteDeck.sln`
Expected: PASS, 199 tests — inchangé, cette tâche n'ajoute aucun test.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteDeck.App/Views/WorkspaceNameWindow.xaml src/RemoteDeck.App/Views/WorkspaceNameWindow.xaml.cs src/RemoteDeck.App/Resources/Strings.resx src/RemoteDeck.App/Resources/Strings.fr.resx
git commit -m "feat(app): workspace naming dialog"
```

---

### Task 6: Capturer, ouvrir et supprimer un espace depuis la palette

**Files:**
- Modify: `src/RemoteDeck.App/App.xaml.cs:67` (enregistrement DI)
- Modify: `src/RemoteDeck.App/Views/ShellWindow.xaml.cs` — champ à côté de `_connections` (`:170`), construction de la palette (autour de `:640-686`), `RunPaletteChoice` (autour de `:760-787`), `RememberPlacement` (`:966`), `OnConnectRequested` (`:1122`), section detach/reattach
- Modify: `src/RemoteDeck.App/Resources/Strings.resx`
- Modify: `src/RemoteDeck.App/Resources/Strings.fr.resx`

**Interfaces:**
- Consumes: `WorkspaceRepository` (tâche 2), `WorkspacePlan.Build` et `WorkspaceAction` (tâche 3), `WorkspaceNameWindow` (tâche 5), et l'existant, **vérifié dans le code** :
  - `PaletteItem(PaletteItemKind, string Id, string Title, string Subtitle, int Priority, string Shortcut = "", string Group = "")`
  - `SessionsViewModel.Tabs` / `Find(long)` / `Activate(SessionTabViewModel?)` / `DetachedWindowOf(SessionTabViewModel?)`
  - `SessionWindow.CurrentPlacement()` → `DetachedWindowPlacement` (gère déjà les cas maximisé et plein écran en rendant les bornes de restauration) et `SessionWindow.IsFullScreen` / `ToggleFullScreen()`
  - `ShellWindow.Screens(DpiScale)` → `IReadOnlyList<ScreenBounds>` (`:918`), appelé partout via `Screens(VisualTreeHelper.GetDpi(this))`
  - `ShellWindow.DetachTab(SessionTabViewModel, System.Windows.Point?)`, `Reattach(SessionWindow)`, `GoToSession(SessionTabViewModel)`, `PlacementKey(SessionTabViewModel)`
- Produces: identifiants de palette `cmd:workspace-save`, `ws:<id>`, `wsdel:<id>` ; `ShellWindow.PlacementOf(SessionWindow)`, `OpenConnectionAsync(Connection, bool)`, `DetachTab(SessionTabViewModel, DetachedWindowPlacement?)`, `MountWorkspaceAsync(Workspace, bool)`.

> **Pas de test automatisé** : `ShellWindow` est une vue. La logique décidable est déjà couverte par `WorkspacePlanTests` (tâche 3) — c'est précisément pourquoi elle y a été extraite. Vérification par la checklist manuelle (tâche 8).

- [ ] **Step 1: Ajouter les chaînes**

`Strings.resx` :

| Nom | Valeur |
|---|---|
| `Palette_GroupWorkspaces` | `Workspaces` |
| `Palette_SaveLayout` | `Save layout as…` |
| `Palette_SaveLayoutSubtitle` | `Remember the open sessions and where they sit` |
| `Palette_OpenWorkspace` | `Open workspace “{0}”` |
| `Palette_OpenWorkspaceSubtitle` | `{0} connections` |
| `Palette_DeleteWorkspace` | `Delete workspace “{0}”` |
| `Palette_DeleteWorkspaceSubtitle` | `Removes the workspace, never the connections` |
| `Shell_WorkspaceSaved` | `Workspace “{0}” saved` |
| `Shell_WorkspaceSavedMessage` | `{0} connections and where each one sits.` |
| `Shell_WorkspaceEmptyTitle` | `Nothing to open` |
| `Shell_WorkspaceEmptyMessage` | `“{0}” no longer references any existing connection.` |

`Strings.fr.resx` :

| Nom | Valeur |
|---|---|
| `Palette_GroupWorkspaces` | `Espaces de travail` |
| `Palette_SaveLayout` | `Enregistrer la disposition sous…` |
| `Palette_SaveLayoutSubtitle` | `Mémorise les sessions ouvertes et leur place` |
| `Palette_OpenWorkspace` | `Ouvrir l'espace « {0} »` |
| `Palette_OpenWorkspaceSubtitle` | `{0} connexions` |
| `Palette_DeleteWorkspace` | `Supprimer l'espace « {0} »` |
| `Palette_DeleteWorkspaceSubtitle` | `Retire l'espace, jamais les connexions` |
| `Shell_WorkspaceSaved` | `Espace « {0} » enregistré` |
| `Shell_WorkspaceSavedMessage` | `{0} connexions, et la place de chacune.` |
| `Shell_WorkspaceEmptyTitle` | `Rien à ouvrir` |
| `Shell_WorkspaceEmptyMessage` | `« {0} » ne référence plus aucune connexion existante.` |

- [ ] **Step 2: Enregistrer et tenir un `WorkspaceRepository`**

Le dépôt passe par l'injection de dépendances, comme `ConnectionRepository` — pas par un `new`. Dans `src/RemoteDeck.App/App.xaml.cs`, juste après la ligne 67 :

```csharp
            services.AddSingleton<ConnectionRepository>();
            services.AddSingleton<WorkspaceRepository>();
```

Il est à l'intérieur du même bloc conditionnel que `ConnectionRepository` : les deux ne sont enregistrés que quand `Database` a pu être créé, et un dépôt absent est un mode dégradé que le shell sait déjà traiter.

Dans `ShellWindow.xaml.cs`, à côté du champ `_connections`, ajouter :

```csharp
    /// <summary>Les espaces de travail, ou <c>null</c> en mode dégradé — même règle que
    /// <see cref="_connections"/>, dont il partage la base.</summary>
    private WorkspaceRepository? _workspaces;
```

et, juste après la ligne 170 (`_connections = App.Current.Services.GetService<ConnectionRepository>();`) :

```csharp
        _workspaces = App.Current.Services.GetService<WorkspaceRepository>();
```

- [ ] **Step 3: Offrir les commandes dans la palette**

Dans la méthode qui construit la liste `items` (celle qui contient déjà `items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:pane", …))`), ajouter avant le `return items;` :

```csharp
        // Enregistrer n'a de sens qu'avec quelque chose à enregistrer, et seulement depuis le shell :
        // la capture lit le ruban entier, pas la fenêtre d'où la palette a été ouverte.
        if (from is null && _sessions.Tabs.Count > 0)
        {
            items.Add(new PaletteItem(PaletteItemKind.Command, "cmd:workspace-save",
                Strings.Palette_SaveLayout, Strings.Palette_SaveLayoutSubtitle, CommandPriority,
                Group: Strings.Palette_GroupWorkspaces));
        }

        foreach (var workspace in _workspaces?.GetAll() ?? [])
        {
            items.Add(new PaletteItem(PaletteItemKind.Command,
                string.Create(CultureInfo.InvariantCulture, $"ws:{workspace.Id}"),
                Text.Of(Strings.Palette_OpenWorkspace, workspace.Name),
                Text.Of(Strings.Palette_OpenWorkspaceSubtitle, workspace.Items.Count), CommandPriority,
                Group: Strings.Palette_GroupWorkspaces));
            items.Add(new PaletteItem(PaletteItemKind.Command,
                string.Create(CultureInfo.InvariantCulture, $"wsdel:{workspace.Id}"),
                Text.Of(Strings.Palette_DeleteWorkspace, workspace.Name),
                Strings.Palette_DeleteWorkspaceSubtitle, CommandPriority,
                Group: Strings.Palette_GroupWorkspaces));
        }
```

- [ ] **Step 4: Router les trois identifiants**

Dans `RunPaletteChoice`, avant le `switch (id)`, à côté du bloc qui traite déjà `ConnectionIdPrefix` :

```csharp
        if (id.StartsWith("ws:", StringComparison.Ordinal))
        {
            if (long.TryParse(id.AsSpan(3), CultureInfo.InvariantCulture, out long openId)
                && _workspaces?.Get(openId) is { } toOpen)
            {
                // Feu et oubli : le montage est asynchrone parce qu'il connecte en série, et la
                // palette n'a rien à attendre. Les échecs sont déjà rapportés par session.
                _ = MountWorkspaceAsync(toOpen);
            }

            return;
        }

        if (id.StartsWith("wsdel:", StringComparison.Ordinal))
        {
            if (long.TryParse(id.AsSpan(6), CultureInfo.InvariantCulture, out long deleteId))
            {
                _workspaces?.Delete(deleteId);
            }

            return;
        }
```

et, dans le `switch`, une branche :

```csharp
            case "cmd:workspace-save":
                SaveCurrentLayout();
                break;
```

- [ ] **Step 5: Écrire la capture**

Ajouter à la section detach/reattach de `ShellWindow.xaml.cs` :

```csharp
    // ---------------------------------------------------------------- espaces de travail

    /// <summary>
    /// Capture les sessions ouvertes en un espace nommé (spec espaces §5). La place de chaque
    /// fenêtre détachée est lue sur la fenêtre réelle, pas sur ce qui est mémorisé : ce que l'espace
    /// enregistre est ce que l'utilisateur voit à l'écran au moment où il l'enregistre.
    /// </summary>
    private void SaveCurrentLayout()
    {
        if (_workspaces is null || _sessions.Tabs.Count == 0)
        {
            return;
        }

        var dialog = new WorkspaceNameWindow(proposedName: null, autoConnect: true) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // Le doublon n'est pas une erreur : remplacer est la seule façon de faire évoluer un espace,
        // puisqu'il n'y a pas d'éditeur. Mais il écrase, donc il se confirme.
        if (_workspaces.FindByName(dialog.WorkspaceName) is not null)
        {
            var confirm = System.Windows.MessageBox.Show(this,
                Text.Of(Strings.WorkspaceName_ReplaceMessage, dialog.WorkspaceName),
                Text.Of(Strings.WorkspaceName_ReplaceTitle, dialog.WorkspaceName),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }
        }

        var workspace = new Workspace { Name = dialog.WorkspaceName, AutoConnect = dialog.AutoConnect };
        for (int i = 0; i < _sessions.Tabs.Count; i++)
        {
            var tab = _sessions.Tabs[i];
            var window = _sessions.DetachedWindowOf(tab);
            workspace.Items.Add(new WorkspaceItem
            {
                ConnectionId = tab.Session.Connection.Id,
                Ordinal = i,
                Detached = window is not null,
                Placement = window is null ? null : PlacementOf(window),
            });
        }

        _workspaces.Save(workspace);
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Success,
            Text.Of(Strings.Shell_WorkspaceSaved, workspace.Name),
            Text.Of(Strings.Shell_WorkspaceSavedMessage, workspace.Items.Count));
    }
```

- [ ] **Step 5b: Extraire `PlacementOf` de `RememberPlacement`**

`SaveCurrentLayout` et `CaptureLastSession` (tâche 7) ont besoin de lire la place d'une fenêtre sans l'écrire dans `_settings`. `RememberPlacement` (`ShellWindow.xaml.cs:966`) fait déjà exactement cette lecture, garde de la fenêtre minimisée comprise. L'extraire plutôt que la dupliquer — remplacer le corps existant par ces deux méthodes :

```csharp
    /// <summary>
    /// La place d'une fenêtre détachée telle qu'elle est à cet instant, ou <c>null</c> quand elle ne
    /// décrit rien d'utilisable. Une fenêtre minimisée n'en a pas : elle ne montre rien que
    /// l'utilisateur reconnaîtrait, et ce qui est déjà enregistré — là où elle était avant d'être
    /// minimisée — est la meilleure réponse. <see cref="SessionWindow.CurrentPlacement"/> traite
    /// lui-même les cas maximisé et plein écran, en rendant les bornes de restauration.
    /// </summary>
    private static DetachedWindowPlacement? PlacementOf(SessionWindow window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            return null;
        }

        var placement = window.CurrentPlacement();
        return placement is { Width: > 0, Height: > 0 } ? placement : null;
    }

    /// <summary>
    /// Enregistre où est une fenêtre détachée, pour la prochaine fois que cette connexion sera
    /// détachée. Appelée sur les deux chemins de sortie d'une fenêtre détachée — sa propre
    /// fermeture, et le shell qui sauve en descendant — ainsi que sur un rattachement, qui est une
    /// fenêtre qui disparaît tout autant.
    /// </summary>
    private void RememberPlacement(SessionWindow window)
    {
        if (PlacementOf(window) is { } placement)
        {
            _settings.DetachedWindows[PlacementKey(window.Tab)] = placement;
        }
    }
```

Le comportement est identique : mêmes gardes, même ordre. Vérifier après coup qu'aucun appelant de `RememberPlacement` n'a changé de signature.

- [ ] **Step 6a: Rendre l'ouverture d'une connexion attendable**

`OnConnectRequested` (`:1122`) est `async void` : on ne peut pas l'attendre, donc l'appeler en boucle connecterait tout en parallèle — exactement ce que la spec §4.2 refuse. Extraire son corps utile dans une méthode attendable, et laisser `OnConnectRequested` l'appeler.

Découper ainsi, **sans changer une ligne de la logique existante** — les gardes, le `_connecting`, le `UpdateLayout()` et le `catch` restent tels quels :

```csharp
    /// <summary>Le clic « Connecter » de la liste et de la palette.</summary>
    private async void OnConnectRequested(Connection connection)
    {
        if (connection is null || _connecting || _closeInProgress)
        {
            return;
        }

        if (_sessions.Find(connection.Id) is { } existing)
        {
            _sessions.Activate(existing);
            return;
        }

        await OpenConnectionAsync(connection, start: true);
    }

    /// <summary>
    /// Ouvre un onglet pour <paramref name="connection"/> et, si <paramref name="start"/>, démarre la
    /// session. Attendable, ce que <see cref="OnConnectRequested"/> ne peut pas être : monter un
    /// espace connecte en série (spec espaces §4.2), et une série a besoin d'un point d'attente.
    /// </summary>
    /// <param name="start">Faux pour un espace dont <c>AutoConnect</c> est décoché : l'onglet
    /// existe, la session attend d'être sélectionnée (spec §4.4).</param>
    private async Task OpenConnectionAsync(Connection connection, bool start)
    {
        if (_version is null)
        {
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Shell_SessionUnavailableTitle,
                Text.Of(Strings.Shell_SessionUnavailableMessage, ProbeLog.Path));
            return;
        }

        _connecting = true;
        try
        {
            var session = new RdpSession(connection, _version, host => SupplyAndConnectAsync(connection, host));
            _sessions.Open(session);
            UpdateSessionsArea();

            // StartAsync creates the OCX, and an AxHost only produces its COM object once it owns a
            // window handle — which WindowsFormsHost gives it during a layout pass in a *visible*
            // container. Forcing that pass here is what makes the very first tab connectable.
            SessionsArea.UpdateLayout();

            _settings.LastConnectionId = connection.Id;
            if (start)
            {
                await session.StartAsync();
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Write("session", $"Opening '{connection.Name}' failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Error, Strings.Shell_ConnectFailedTitle,
                Text.Of(Strings.Shell_ConnectFailedMessage, ex.GetType().Name,
                    ex.HResult.ToString("X8", CultureInfo.InvariantCulture), ex.Message));
        }
        finally
        {
            _connecting = false;
        }
    }
```

> Le `catch` et le `finally` ci-dessus sont **le corps existant, recopié à l'identique** depuis `OnConnectRequested` (`:1157-1167`). Les déplacer, ne pas les réécrire : `start: false` doit échouer exactement comme `start: true`.

- [ ] **Step 6b: Surcharger `DetachTab` avec une place explicite**

`DetachTab(SessionTabViewModel, System.Windows.Point?)` choisit sa place dans cet ordre : mémorisation par connexion, puis point du pointeur, puis centrage. Un espace apporte sa propre place, et elle **prime** (spec §7). Ajouter la surcharge à côté :

```csharp
    /// <summary>
    /// Détache <paramref name="tab"/> à une place imposée. Celle d'un espace prime sur la
    /// mémorisation par connexion, qui ne sert que de repli quand l'espace n'en a pas (spec espaces
    /// §7) — sans quoi ouvrir « INCIDENT » rendrait « PROD » inopérant.
    /// </summary>
    private void DetachTab(SessionTabViewModel tab, DetachedWindowPlacement? placement)
    {
        if (_closeInProgress || tab.IsDetached)
        {
            return;
        }

        var window = new SessionWindow(tab, _sessions);
        var chosen = placement ?? RememberedPlacement(tab, window);
        if (chosen is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = chosen.Left;
            window.Top = chosen.Top;
            window.Width = chosen.Width;
            window.Height = chosen.Height;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ReattachRequested += OnSessionWindowReattachRequested;
        window.CloseRequested += OnSessionWindowCloseRequested;
        window.CaptionDragMoved += OnSessionWindowCaptionDragMoved;
        window.CaptionDragEnded += OnSessionWindowCaptionDragEnded;
        window.SessionRequested += GoToSession;
        window.Show();

        if (_sessions.Detach(tab, window))
        {
            if (chosen?.FullScreen == true)
            {
                window.ToggleFullScreen();
            }

            return;
        }

        window.AllowClose();
        window.Close();
        StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Warning, Strings.Shell_DetachRefusedTitle,
            Text.Of(Strings.Shell_DetachRefusedMessage, tab.Title));
    }
```

Les deux surcharges partagent alors tout sauf le choix de la place. Si la duplication gêne, faire appeler celle-ci par la surcharge `Point?` en lui passant `RememberedPlacement(...) ?? PlaceUnder(point, window)` — mais cela demande la fenêtre avant le calcul, donc garder les deux corps est acceptable ici.

- [ ] **Step 6c: Écrire le montage**

À la suite, dans la même section :

```csharp
    /// <summary>
    /// Monte un espace : chaque connexion est amenée dans l'état que l'espace décrit (spec espaces
    /// §4.2). Rien n'est fermé — un espace ajoute, il ne remplace pas — et rien ne reconnecte : une
    /// session déjà ouverte est déplacée, ce qui est du re-parenting.
    /// </summary>
    /// <param name="announceEmpty">Faux pour la reprise au démarrage : une reprise vide y est le cas
    /// normal, pas un incident dont il faut avertir.</param>
    /// <remarks>
    /// En série et non en parallèle : six négociations RDP simultanées sur un réseau qui vient de
    /// monter, c'est six échecs dont aucun n'est la faute d'une machine.
    /// </remarks>
    private async Task MountWorkspaceAsync(Workspace workspace, bool announceEmpty = true)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (_connections is null || _closeInProgress)
        {
            return;
        }

        var existing = _connections.GetAll().Select(c => c.Id).ToHashSet();
        var open = _sessions.Tabs.ToDictionary(t => t.Session.Connection.Id, t => t.IsDetached);
        var plan = WorkspacePlan.Build(workspace, existing, open, Screens(VisualTreeHelper.GetDpi(this)));

        if (plan.Count == 0)
        {
            if (announceEmpty)
            {
                StatusBar.Show(Wpf.Ui.Controls.InfoBarSeverity.Informational,
                    Strings.Shell_WorkspaceEmptyTitle,
                    Text.Of(Strings.Shell_WorkspaceEmptyMessage, workspace.Name));
            }

            return;
        }

        foreach (var action in plan)
        {
            await ApplyWorkspaceActionAsync(action, workspace.AutoConnect);
        }
    }

    /// <summary>Une action du plan. Les six branches sont celles de la spec §4.1 et §4.1.1.</summary>
    private async Task ApplyWorkspaceActionAsync(WorkspaceAction action, bool autoConnect)
    {
        var tab = _sessions.Find(action.ConnectionId);

        switch (action.Kind)
        {
            case WorkspaceActionKind.Activate when tab is not null:
                GoToSession(tab);
                break;

            case WorkspaceActionKind.MoveDetached when tab is not null:
                if (_sessions.DetachedWindowOf(tab) is { } window && action.Placement is { } target)
                {
                    // Sortir du plein écran d'abord : déplacer une fenêtre plein écran ne veut rien
                    // dire, et SetFullScreen restaure ses propres bornes en sortant.
                    if (window.IsFullScreen && !target.FullScreen)
                    {
                        window.ToggleFullScreen();
                    }

                    if (!window.IsFullScreen)
                    {
                        window.Left = target.Left;
                        window.Top = target.Top;
                        window.Width = target.Width;
                        window.Height = target.Height;
                    }

                    if (target.FullScreen && !window.IsFullScreen)
                    {
                        window.ToggleFullScreen();
                    }
                }

                GoToSession(tab);
                break;

            case WorkspaceActionKind.Detach when tab is not null:
                DetachTab(tab, action.Placement);
                break;

            case WorkspaceActionKind.Reattach when tab is not null:
                if (_sessions.DetachedWindowOf(tab) is { } toReattach)
                {
                    Reattach(toReattach);
                }

                break;

            case WorkspaceActionKind.OpenDocked:
            case WorkspaceActionKind.OpenDetached:
                if (_connections?.Get(action.ConnectionId) is { } connection)
                {
                    await OpenConnectionAsync(connection, start: autoConnect);

                    // Find à nouveau : l'onglet n'existait pas avant l'ouverture.
                    if (action.Kind == WorkspaceActionKind.OpenDetached
                        && _sessions.Find(action.ConnectionId) is { } opened)
                    {
                        DetachTab(opened, action.Placement);
                    }
                }

                break;
        }
    }
```

> `ToggleFullScreen()` n'est légal que sur une session connectée (`SessionWindow` le refuse autrement). Un espace dont un item est en plein écran et dont la session vient d'échouer restera donc fenêtré — c'est déjà la règle des fenêtres détachées, et il n'y a rien à ajouter.

- [ ] **Step 7: Compiler**

Run: `dotnet build RemoteDeck.sln`
Expected: succès, `0 Avertissement(s)`. Ajouter les `using RemoteDeck.Core.Model;` et `using RemoteDeck.Core.Sessions;` manquants si le compilateur les réclame.

- [ ] **Step 8: Lancer la suite complète**

Run: `dotnet test RemoteDeck.sln`
Expected: PASS, 199 tests.

- [ ] **Step 9: Vérification manuelle minimale**

Lancer l'application, ouvrir deux connexions, en détacher une, `Ctrl+K` → *Enregistrer la disposition sous…* → nommer `TEST`. Fermer les deux sessions, puis `Ctrl+K` → *Ouvrir l'espace « TEST »*. Les deux sessions reviennent, l'une ancrée, l'autre détachée à sa place.

- [ ] **Step 10: Commit**

```bash
git add src/RemoteDeck.App/Views/ShellWindow.xaml.cs src/RemoteDeck.App/Resources/Strings.resx src/RemoteDeck.App/Resources/Strings.fr.resx
git commit -m "feat(app): save, open and delete workspaces from the palette"
```

---

### Task 7: Reprise de la dernière session au démarrage

**Files:**
- Modify: `src/RemoteDeck.App/Views/ShellWindow.xaml.cs` — la fermeture (autour de `:1587`, où `RememberPlacement` est appelé pour chaque fenêtre détachée) et le démarrage
- Modify: `src/RemoteDeck.App/Resources/Strings.resx`
- Modify: `src/RemoteDeck.App/Resources/Strings.fr.resx`

**Interfaces:**
- Consumes: `AppSettings.RestoreLastSession`, `AppSettings.LastSession`, `LastSessionEntry` (tâche 4), `Workspace` / `WorkspaceItem` (tâche 2), `ShellWindow.MountWorkspaceAsync(Workspace, bool)` et `PlacementOf(SessionWindow)` (tâche 6), `SessionsViewModel.Tabs` / `DetachedWindowOf`.
- Produces: `ShellWindow.CaptureLastSession()` (void) et `ShellWindow.RestoreLastSessionIfAsked()` (`Task`).

- [ ] **Step 1: Ajouter les chaînes du réglage**

`Strings.resx` : `Palette_ToggleRestore` = `Reopen last session at startup`, `Palette_ToggleRestoreSubtitle` = `Currently: {0}`, `Palette_On` = `on`, `Palette_Off` = `off`.

`Strings.fr.resx` : `Palette_ToggleRestore` = `Rouvrir la dernière session au démarrage`, `Palette_ToggleRestoreSubtitle` = `Actuellement : {0}`, `Palette_On` = `activé`, `Palette_Off` = `désactivé`.

- [ ] **Step 2: Capturer à la fermeture**

Dans la méthode de fermeture qui parcourt déjà les fenêtres détachées pour appeler `RememberPlacement`, ajouter la capture de `LastSession` sur le même parcours du ruban :

```csharp
    /// <summary>
    /// Photographie le ruban pour la reprise au prochain démarrage. Écrite à chaque fermeture propre
    /// et seulement là : une fermeture par crash laisse la précédente, ce qui est le comportement
    /// utile (spec espaces §3.2).
    /// </summary>
    private void CaptureLastSession()
    {
        var entries = new List<LastSessionEntry>();
        for (int i = 0; i < _sessions.Tabs.Count; i++)
        {
            var tab = _sessions.Tabs[i];
            var window = _sessions.DetachedWindowOf(tab);
            entries.Add(new LastSessionEntry
            {
                ConnectionId = tab.Session.Connection.Id,
                Ordinal = i,
                Detached = window is not null,
                Placement = window is null ? null : PlacementOf(window),
            });
        }

        _settings.LastSession = entries;
    }
```

L'appeler **avant** que les sessions ne soient fermées et avant l'écriture de `settings.json`, sinon le ruban est déjà vide.

- [ ] **Step 3: Reprendre au démarrage**

```csharp
    /// <summary>
    /// Rouvre la dernière session, si l'utilisateur l'a demandé. Passe par le même
    /// <see cref="WorkspacePlan"/> que les espaces nommés : c'est la même décision, sur une autre
    /// source, et elle n'a pas à être écrite deux fois.
    /// </summary>
    private async Task RestoreLastSessionIfAsked()
    {
        if (!_settings.RestoreLastSession || _settings.LastSession.Count == 0 || _connections is null)
        {
            return;
        }

        // Un espace éphémère, jamais écrit en base : c'est l'adaptateur entre l'état de fenêtrage
        // de settings.json et la décision de montage.
        var asWorkspace = new Workspace
        {
            Name = string.Empty,
            AutoConnect = true,
            Items = [.. _settings.LastSession.Select(e => new WorkspaceItem
            {
                ConnectionId = e.ConnectionId,
                Ordinal = e.Ordinal,
                Detached = e.Detached,
                Placement = e.Placement,
            })],
        };

        // announceEmpty: false — au démarrage, une reprise qui ne donne rien (connexions supprimées
        // depuis) est le cas normal, pas un incident dont il faut avertir.
        await MountWorkspaceAsync(asWorkspace, announceEmpty: false);
    }
```

L'appeler là où la fenêtre principale a fini de se charger et où le conteneur de sessions est prêt — au même endroit que la restauration de la disposition du pane, et **après** que `_connections` et `_version` sont affectés (`ShellWindow.xaml.cs:170`), sans quoi `OpenConnectionAsync` sort sur son garde `_version is null`. Depuis un gestionnaire `Loaded` non attendable, appeler `_ = RestoreLastSessionIfAsked();`.

`RestoreLastSessionIfAsked` construit un `Workspace` qui n'est jamais écrit en base : c'est l'adaptateur entre `settings.json` et la décision de montage, et c'est ce qui évite d'écrire deux fois la même logique.

- [ ] **Step 4: Offrir le réglage dans la palette**

Ajouter une entrée `cmd:restore-toggle` au groupe `Palette_GroupCommands`, dont le sous-titre affiche l'état courant via `Text.Of(Strings.Palette_ToggleRestoreSubtitle, _settings.RestoreLastSession ? Strings.Palette_On : Strings.Palette_Off)`, et une branche du `switch` qui bascule `_settings.RestoreLastSession` puis sauve les réglages.

- [ ] **Step 5: Compiler et tester**

Run: `dotnet build RemoteDeck.sln` puis `dotnet test RemoteDeck.sln`
Expected: build à `0 Avertissement(s)`, 199 tests verts.

- [ ] **Step 6: Vérification manuelle**

Activer le réglage, ouvrir deux sessions dont une détachée, fermer l'application, la rouvrir : les deux reviennent. Désactiver le réglage, fermer, rouvrir : rien ne s'ouvre.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteDeck.App/Views/ShellWindow.xaml.cs src/RemoteDeck.App/Resources/Strings.resx src/RemoteDeck.App/Resources/Strings.fr.resx
git commit -m "feat(app): optional restore of the last session at startup"
```

---

### Task 8: Documentation et vérification manuelle

**Files:**
- Modify: `README.md` (section *Sessions*)
- Modify: `CHANGELOG.md` (section *Unreleased*)
- Modify: `docs/manual-checklist.md`

- [ ] **Step 1: README**

Ajouter une sous-section **Espaces de travail** après *Full screen*. Elle doit dire, sans jargon : comment on capture un espace, comment on l'ouvre, que **rien n'est jamais fermé** par l'ouverture d'un espace, que les connexions partent en série et non toutes à la fois, qu'une connexion supprimée disparaît des espaces, et que la reprise de la dernière session existe et est **désactivée par défaut**. Mentionner que les espaces vivent dans `connections.db` et la reprise dans `settings.json`, en conservant la phrase existante « supprimer `settings.json` ne coûte que la disposition », qui reste vraie.

- [ ] **Step 2: CHANGELOG**

Une entrée sous `## Unreleased`, au-dessus des entrées existantes, dans le ton des autres : ce que la fonctionnalité fait, et les deux décisions qui surprendraient si elles n'étaient pas écrites — un espace n'écrase jamais ce qui tourne, et la reprise est désactivée par défaut.

- [ ] **Step 3: Checklist manuelle**

Ajouter une section `## Espaces de travail`, avant `## Build prerequisites`, avec au minimum :

```markdown
- [ ] Capturer un espace de trois sessions dont deux détachées sur deux écrans, le rouvrir
      après avoir tout fermé : les trois reviennent, chacune à sa place.
- [ ] Ouvrir un espace alors que deux de ses sessions tournent déjà : **rien n'est fermé ni
      reconnecté**, et les deux sessions vivantes sont seulement déplacées.
- [ ] Session ancrée que l'espace veut détachée : elle est détachée. L'inverse la rattache.
- [ ] Enregistrer sous un nom existant : la confirmation apparaît, et refuser laisse
      l'espace inchangé.
- [ ] Supprimer une connexion membre d'un espace : elle disparaît de l'espace, l'espace
      reste, et les autres connexions sont intactes.
- [ ] Un espace dont toutes les connexions ont été supprimées : il reste listé, l'ouvrir
      affiche « Rien à ouvrir » et n'ouvre rien.
- [ ] Débrancher l'écran d'une fenêtre de l'espace, puis l'ouvrir : la fenêtre est sur un
      écran connecté et atteignable — approximatif sur des échelles mixtes, c'est attendu.
- [ ] `AutoConnect` décoché : les onglets apparaissent sans qu'aucune session ne démarre ;
      chacune se connecte à la sélection.
- [ ] Les connexions partent **en série** : sur un espace de quatre, les pastilles passent au
      vert l'une après l'autre, pas toutes ensemble.
- [ ] Une connexion de l'espace dont le mot de passe est refusé : elle seule échoue, avec sa
      raison, et **n'est pas rejouée**. Les autres sont montées.
- [ ] Reprise de dernière session **désactivée par défaut** sur une installation neuve.
- [ ] Activée : fermer avec deux sessions, rouvrir, les deux reviennent. Tuer le processus au
      lieu de fermer proprement : la reprise précédente est conservée.
- [ ] La fenêtre de nommage : `Entrée` valide, `Échap` annule, le bouton reste grisé sur un
      nom vide ou fait d'espaces, et l'apparence suit le thème clair et le thème sombre.
- [ ] Interface en anglais puis en français (`REMOTEDECK_UI_CULTURE`) : aucune chaîne
      d'espace de travail n'est restée en dur.
```

- [ ] **Step 4: Vérification finale**

Run: `dotnet build RemoteDeck.sln` puis `dotnet test RemoteDeck.sln`
Expected: `0 Avertissement(s)`, 199 tests verts.

- [ ] **Step 5: Commit**

```bash
git add README.md CHANGELOG.md docs/manual-checklist.md
git commit -m "docs: workspaces"
```

---

## Récapitulatif des tests

| Tâche | Tests ajoutés | Total attendu |
|---|---|---|
| Baseline | — | 171 |
| 1 — Schéma V2 | 3 | 174 |
| 2 — Repository | 8 | 182 |
| 3 — `WorkspacePlan` | 14 | 196 |
| 4 — Settings | 3 | 199 |
| 5 à 8 — App et docs | 0 (vues WPF, non testables ici) | 199 |

Les tâches 1 à 4 sont entièrement pilotées par les tests. Les tâches 5 à 7 ne le sont pas, et c'est structurel : `RemoteDeck.App` n'a pas de projet de test. C'est précisément pourquoi la décision de montage vit dans `WorkspacePlan` — la seule partie où un bug serait silencieux y est couverte par 14 tests.
