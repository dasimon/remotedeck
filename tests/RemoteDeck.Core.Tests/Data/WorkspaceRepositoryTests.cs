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
