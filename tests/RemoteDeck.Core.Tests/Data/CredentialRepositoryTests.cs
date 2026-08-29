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
    public void Insert_rejected_by_the_database_leaves_the_timestamp_unset()
    {
        _repo.Insert(Make("Same"));
        var clash = Make("Same");

        Assert.Throws<SqliteException>(() => _repo.Insert(clash));

        Assert.Equal(default, clash.ModifiedUtc);
    }

    [Fact]
    public void Update_to_an_existing_label_throws()
    {
        _repo.Insert(Make("A"));
        var b = Make("B");
        _repo.Insert(b);

        b.Label = "A";

        Assert.Throws<SqliteException>(() => _repo.Update(b));
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
    public void Update_unknown_id_leaves_the_timestamp_untouched()
    {
        var c = Make("Stamped");
        _repo.Insert(c);
        var stamped = c.ModifiedUtc;
        Thread.Sleep(5);
        c.Id = 12345;

        Assert.Throws<KeyNotFoundException>(() => _repo.Update(c));

        Assert.Equal(stamped, c.ModifiedUtc);
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
