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
    public void New_connection_defaults_match_the_model()
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
    public void Sql_defaults_match_the_spec()
    {
        // Deliberately bypasses the repository: this pins the DEFAULT clauses of the V1 script,
        // which every row not written by Insert relies on.
        using var raw = _tmp.Db.Open();
        var cmd = raw.Cmd(
            "INSERT INTO Connection (Name, Host, CreatedUtc) VALUES ('raw', 'raw', '2026-01-01T00:00:00.0000000Z');"
            + " SELECT last_insert_rowid();");
        var id = (long)cmd.ExecuteScalar()!;

        var b = _repo.Get(id)!;
        Assert.Equal(3389, b.Port);
        Assert.Equal("", b.GroupName);
        Assert.Equal(DisplayMode.Dynamic, b.DisplayMode);
        Assert.True(b.RedirectClipboard);
        Assert.False(b.RedirectDrives);
        Assert.False(b.RedirectPrinters);
        Assert.False(b.RedirectAudio);
        Assert.False(b.AdminSession);
        Assert.False(b.UseWebAccount);
        Assert.False(b.IsFavorite);
        Assert.Equal("", b.Notes);
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
        Assert.Equal(cred.Id, _repo.Get(x.Id)!.CredentialId);

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
    public void Insert_rejected_by_the_database_leaves_CreatedUtc_unset()
    {
        var x = Make("Bad ref");
        x.CredentialId = 4242;

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _repo.Insert(x));

        Assert.Equal(default, x.CreatedUtc);
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

    [Fact]
    public void SetFavorite_flips_the_flag_both_ways()
    {
        var x = Make("Star");
        _repo.Insert(x);

        _repo.SetFavorite(x.Id, true);
        Assert.True(_repo.Get(x.Id)!.IsFavorite);

        _repo.SetFavorite(x.Id, false);
        Assert.False(_repo.Get(x.Id)!.IsFavorite);
    }

    [Fact]
    public void SetFavorite_touches_nothing_else()
    {
        // The whole point of a one-column update: the context menu has no form behind it, so it must
        // not be able to write back a stale copy of the rest of the row.
        var x = new Connection
        {
            Name = "Prod DC", Host = "dc01", Port = 3390, GroupName = "Prod",
            DisplayMode = DisplayMode.Fixed, FixedWidth = 1920, FixedHeight = 1080,
            RedirectClipboard = false, Notes = "keep me",
        };
        _repo.Insert(x);
        var before = _repo.Get(x.Id)!;

        _repo.SetFavorite(x.Id, true);

        var after = _repo.Get(x.Id)!;
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Host, after.Host);
        Assert.Equal(before.Port, after.Port);
        Assert.Equal(before.GroupName, after.GroupName);
        Assert.Equal(before.DisplayMode, after.DisplayMode);
        Assert.Equal(before.FixedWidth, after.FixedWidth);
        Assert.Equal(before.FixedHeight, after.FixedHeight);
        Assert.Equal(before.RedirectClipboard, after.RedirectClipboard);
        Assert.Equal(before.Notes, after.Notes);
        Assert.Equal(before.CreatedUtc, after.CreatedUtc);
    }

    [Fact]
    public void SetFavorite_of_an_unknown_id_is_a_no_op()
    {
        // Idempotent like Delete, and for the same reason: the row can be gone between the click and
        // the write, and that is a race, not a mistake the user made.
        _repo.SetFavorite(4242, true);

        Assert.Empty(_repo.GetAll());
    }

    [Fact]
    public void GetAll_puts_a_freshly_favorited_connection_first()
    {
        // The ordering the pane relies on, exercised through the new write rather than through Insert.
        _repo.Insert(Make("Alpha"));
        var zulu = Make("Zulu");
        _repo.Insert(zulu);

        _repo.SetFavorite(zulu.Id, true);

        Assert.Equal("Zulu", _repo.GetAll()[0].Name);
    }

    [Fact]
    public void Update_does_not_rewrite_CreatedUtc()
    {
        var x = Make("Immutable");
        _repo.Insert(x);
        var created = _repo.Get(x.Id)!.CreatedUtc;

        // A caller that rebuilds the object from a form has no CreatedUtc to give back.
        var edited = new Connection { Id = x.Id, Name = x.Name, Host = x.Host };
        Assert.Equal(default, edited.CreatedUtc);
        _repo.Update(edited);

        Assert.Equal(created, _repo.Get(x.Id)!.CreatedUtc);
    }
}
