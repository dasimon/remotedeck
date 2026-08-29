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
