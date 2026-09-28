using RemoteDeck.Core.Data;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Settings;
using RemoteDeck.Core.Tests.Data;
using RemoteDeck.Core.Transfer;

namespace RemoteDeck.Core.Tests.Transfer;

public sealed class ConfigurationTransferTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    private static Credential Credential(long id, string label) => new()
    {
        Id = id, Label = label, UserName = "admin", Domain = "CORP",
        SecretBlob = [1, 2, 3, 4], Entropy = [9, 9, 9],
    };

    private static Connection Sql(long id = 1, long? credential = 10) => new()
    {
        Id = id, Name = "SQL", Host = "sql01.corp.local", Port = 3390, GroupName = "Prod",
        CredentialId = credential, IsFavorite = true, DisplayMode = DisplayMode.Fixed,
        FixedWidth = 1920, FixedHeight = 1080, VpnProfile = "VPN Contoso", AutoRaiseVpn = true,
        Notes = "Primary", AuthenticationLevel = 1,
    };

    private static Workspace Prod(long connectionId) => new()
    {
        Id = 5, Name = "PROD", AutoConnect = false,
        Items = [new WorkspaceItem { ConnectionId = connectionId, Ordinal = 0, Detached = true,
            Placement = new DetachedWindowPlacement(10, 20, 1280, 800, false) }],
    };

    private static ConfigurationDocument RoundTrip(ConfigurationDocument document) =>
        ConfigurationTransfer.Read(ConfigurationTransfer.Serialize(document));

    [Fact]
    public void An_export_carries_no_secret()
    {
        var json = ConfigurationTransfer.Serialize(
            ConfigurationTransfer.Build([Sql()], [Credential(10, "Domain admin")], [], Now));

        Assert.Contains("Domain admin", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entropy", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_connection_survives_the_round_trip_with_its_credential_named_by_label()
    {
        var document = RoundTrip(ConfigurationTransfer.Build([Sql()], [Credential(10, "Domain admin")], [], Now));

        var c = Assert.Single(document.Connections);
        Assert.Equal("SQL", c.Name);
        Assert.Equal(3390, c.Port);
        Assert.Equal(DisplayMode.Fixed, c.DisplayMode);
        Assert.Equal("Domain admin", c.Credential);
        Assert.Equal("VPN Contoso", c.VpnProfile);
        Assert.True(c.AutoRaiseVpn);
        Assert.Equal(Now, document.ExportedUtc);
    }

    [Fact]
    public void Import_links_a_credential_with_the_same_label_and_names_the_missing_ones()
    {
        var document = RoundTrip(ConfigurationTransfer.Build(
            [Sql(1, 10), Sql(2, 11).Also(c => c.Name = "APP")],
            [Credential(10, "Domain admin"), Credential(11, "Local admin")], [], Now));

        var plan = ConfigurationTransfer.Plan(document, [], [Credential(77, "domain ADMIN")], []);

        Assert.Equal(2, plan.Connections.Count);
        Assert.Equal(77, plan.Connections.Single(p => p.Connection.Name == "SQL").Connection.CredentialId);
        Assert.Null(plan.Connections.Single(p => p.Connection.Name == "APP").Connection.CredentialId);
        Assert.Equal(["Local admin"], plan.MissingCredentials);
    }

    [Fact]
    public void A_connection_already_present_is_not_added_again()
    {
        var document = RoundTrip(ConfigurationTransfer.Build([Sql()], [], [], Now));
        var local = Sql(42).Also(c => c.Name = "sql");

        var plan = ConfigurationTransfer.Plan(document, [local], [], []);

        Assert.Empty(plan.Connections);
        Assert.Equal(1, plan.ConnectionsAlreadyPresent);
        Assert.False(plan.HasSomethingToAdd);
    }

    [Fact]
    public void A_connection_the_editor_would_refuse_is_not_planned()
    {
        var document = new ConfigurationDocument
        {
            Connections = [new ExportedConnection { Key = 1, Name = "Bad", Host = "has space" }],
        };

        var plan = ConfigurationTransfer.Plan(document, [], [], []);

        Assert.Empty(plan.Connections);
        Assert.Equal(1, plan.ConnectionsInvalid);
    }

    [Fact]
    public void Weakened_security_is_counted_for_the_confirmation()
    {
        var document = new ConfigurationDocument
        {
            Connections =
            [
                new ExportedConnection { Key = 1, Name = "A", Host = "a", AuthenticationLevel = 0 },
                new ExportedConnection { Key = 2, Name = "B", Host = "b", RedirectDrives = true },
                new ExportedConnection { Key = 3, Name = "C", Host = "c", AuthenticationLevel = 2 },
            ],
        };

        Assert.Equal(2, ConfigurationTransfer.Plan(document, [], [], []).WeakenedSecurity);
    }

    [Fact]
    public void A_workspace_whose_name_is_taken_is_not_replaced()
    {
        var document = RoundTrip(ConfigurationTransfer.Build([Sql()], [], [Prod(1)], Now));

        var plan = ConfigurationTransfer.Plan(document, [], [], [new Workspace { Name = "prod" }]);

        Assert.Empty(plan.Workspaces);
        Assert.Equal(1, plan.WorkspacesAlreadyPresent);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"format":"something-else","version":1}""")]
    [InlineData("""{"format":"remotedeck-configuration","version":99}""")]
    public void Anything_but_a_readable_configuration_is_refused(string json)
        => Assert.Throws<InvalidDataException>(() => ConfigurationTransfer.Read(json));

    [Fact]
    public void Apply_writes_connections_then_workspaces_mapped_to_the_new_ids()
    {
        using var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();
        var connections = new ConnectionRepository(tmp.Db);
        var workspaces = new WorkspaceRepository(tmp.Db);
        long present = connections.Insert(new Connection { Name = "APP", Host = "app01" });

        var app = new Connection { Id = 2, Name = "APP", Host = "app01" };
        var document = RoundTrip(ConfigurationTransfer.Build(
            [Sql(1, null), app], [],
            [new Workspace
            {
                Name = "PROD",
                Items = [new WorkspaceItem { ConnectionId = 1, Ordinal = 0 }, new WorkspaceItem { ConnectionId = 2, Ordinal = 1 }],
            }], Now));

        var plan = ConfigurationTransfer.Plan(document, connections.GetAll(), [], workspaces.GetAll());
        var result = new ImportResult();
        ConfigurationTransfer.Apply(plan, connections, workspaces, result);

        Assert.Equal(1, result.ConnectionsAdded);
        Assert.Equal(1, result.WorkspacesAdded);
        var sql = connections.GetAll().Single(c => c.Name == "SQL");
        var prod = Assert.Single(workspaces.GetAll());
        Assert.Equal([sql.Id, present], prod.Items.OrderBy(i => i.Ordinal).Select(i => i.ConnectionId));

        // The same file a second time adds nothing.
        var again = ConfigurationTransfer.Plan(document, connections.GetAll(), [], workspaces.GetAll());
        Assert.False(again.HasSomethingToAdd);
    }

    [Fact]
    public void A_row_repeated_in_the_file_is_added_once_and_both_keys_open_it()
    {
        using var tmp = new TempDatabase();
        tmp.Db.EnsureCreated();
        var connections = new ConnectionRepository(tmp.Db);
        var workspaces = new WorkspaceRepository(tmp.Db);
        var document = new ConfigurationDocument
        {
            Connections =
            [
                new ExportedConnection { Key = 1, Name = "SQL", Host = "sql01" },
                new ExportedConnection { Key = 2, Name = "SQL", Host = "SQL01" },
            ],
            Workspaces = [new ExportedWorkspace("W", true, [new ExportedWorkspaceItem(2, 0, false, null)])],
        };

        var plan = ConfigurationTransfer.Plan(document, [], [], []);
        ConfigurationTransfer.Apply(plan, connections, workspaces, new ImportResult());

        var only = Assert.Single(connections.GetAll());
        Assert.Equal(only.Id, Assert.Single(Assert.Single(workspaces.GetAll()).Items).ConnectionId);
    }
}

internal static class ConnectionTestExtensions
{
    /// <summary>Tweaks a fixture in place and hands it back, for one-line variations.</summary>
    public static Connection Also(this Connection connection, Action<Connection> change)
    {
        change(connection);
        return connection;
    }
}
