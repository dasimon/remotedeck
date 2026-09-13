using Microsoft.Data.Sqlite;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Data;

/// <summary>
/// Reads and writes workspaces (workspaces spec §3.1). A workspace is always written whole: there
/// is no editor, so there is no partial update to represent.
/// </summary>
public sealed class WorkspaceRepository(SqliteDatabase db)
{
    /// <summary>
    /// Inserts the workspace, or fully replaces the one that already has this name. Replacing is the
    /// normal way to evolve a workspace (spec §5), and it keeps the existing <c>Id</c> so that
    /// nothing is left pointing at nothing.
    /// </summary>
    /// <returns>The id of the workspace written.</returns>
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

            // Items are rewritten as a block: that is what "replace" means here, and it avoids
            // computing a diff for a list of two to six rows.
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

    /// <summary>Idempotent, like <see cref="ConnectionRepository.Delete"/>: an id already gone is a
    /// non-event. Items go with it by cascade.</summary>
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
            // A placement exists only if all four coordinates are there. A detached item whose
            // placement was never saved is legitimate: the per-connection fallback handles it.
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
