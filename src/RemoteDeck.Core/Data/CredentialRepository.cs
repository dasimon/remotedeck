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
        cmd.Add("$modified", credential.ModifiedUtc.ToDb());
        credential.Id = (long)cmd.ExecuteScalar()!;
        return credential.Id;
    }

    public void Update(Credential credential)
    {
        var now = DateTime.UtcNow;
        using var c = db.Open();
        var cmd = c.Cmd("""
            UPDATE Credential SET Label = $label, Domain = $domain, UserName = $user,
                SecretBlob = $blob, Entropy = $entropy, ModifiedUtc = $modified
            WHERE Id = $id
            """);
        Bind(cmd, credential);
        cmd.Add("$modified", now.ToDb());
        cmd.Add("$id", credential.Id);
        if (cmd.ExecuteNonQuery() == 0) throw new KeyNotFoundException($"Credential {credential.Id} does not exist.");
        credential.ModifiedUtc = now; // only once the row is known to exist: a failed update leaves the object untouched
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

    /// <summary>Everything but <c>$modified</c>, which each caller stamps itself.</summary>
    private static void Bind(SqliteCommand cmd, Credential x)
    {
        cmd.Add("$label", x.Label);
        cmd.Add("$domain", x.Domain);
        cmd.Add("$user", x.UserName);
        cmd.Add("$blob", x.SecretBlob);
        cmd.Add("$entropy", x.Entropy);
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
