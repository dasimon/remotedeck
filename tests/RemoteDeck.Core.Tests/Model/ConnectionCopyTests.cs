using System.Reflection;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Tests.Model;

/// <summary>
/// Duplicating a connection: which fields a copy carries, and the name it is offered under.
/// </summary>
public sealed class ConnectionCopyTests
{
    private const string First = "{0} (copy)";
    private const string Nth = "{0} (copy {1})";

    /// <summary>What a copy deliberately does not take from its source.</summary>
    private static readonly HashSet<string> NotCarried =
    [
        nameof(Connection.Id),               // a new row
        nameof(Connection.Name),             // given by the caller, never the source's
        nameof(Connection.IsFavorite),       // a star is about one machine, not its settings
        nameof(Connection.LastConnectedUtc), // it has never been connected
        nameof(Connection.CreatedUtc),       // set by the insert
    ];

    private static Connection Source() => new()
    {
        Id = 42, Name = "WIN02", Host = "contoso-win02", Port = 3390, GroupName = "Prod", CredentialId = 7,
        IsFavorite = true, DisplayMode = DisplayMode.Fixed, FixedWidth = 1920, FixedHeight = 1080,
        RedirectClipboard = false, RedirectDrives = true, RedirectPrinters = true, RedirectAudio = true,
        AdminSession = true, UseWebAccount = true, WebAccountUpn = "user@contoso.com", AuthenticationLevel = 1,
        VpnProfile = "VPN Contoso", AutoRaiseVpn = true, Notes = "notes",
        LastConnectedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Every_setting_is_carried_except_the_ones_that_belong_to_the_original()
    {
        var source = Source();
        var copy = ConnectionCopy.Of(source, "WIN03");

        foreach (var property in typeof(Connection).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (NotCarried.Contains(property.Name))
            {
                continue;
            }

            Assert.True(Equals(property.GetValue(source), property.GetValue(copy)), $"{property.Name} was not carried");
        }

        Assert.Equal("WIN03", copy.Name);
        Assert.Equal(0, copy.Id);
        Assert.False(copy.IsFavorite);
        Assert.Null(copy.LastConnectedUtc);
    }

    [Fact]
    public void The_source_fixture_sets_every_property_so_a_new_one_cannot_be_skipped_silently()
    {
        // The test above compares property by property, which proves nothing for a property the
        // fixture leaves at its default: a copy that forgot it would still "match". A column added
        // later has to be set in Source(), or listed in NotCarried, before this passes again.
        var fresh = new Connection { Name = "", Host = "" };
        var source = Source();

        foreach (var property in typeof(Connection).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.False(Equals(property.GetValue(fresh), property.GetValue(source)),
                $"{property.Name} is left at its default in the fixture");
        }
    }

    [Fact]
    public void A_copy_shares_no_mutable_state_with_its_source()
    {
        var source = Source();
        var copy = ConnectionCopy.Of(source, "WIN03");

        copy.Host = "elsewhere";

        Assert.Equal("contoso-win02", source.Host);
    }

    [Fact]
    public void The_first_copy_is_named_after_its_source()
    {
        Assert.Equal("WIN02 (copy)", ConnectionCopy.NameFor("WIN02", ["WIN02"], First, Nth));
    }

    [Fact]
    public void Later_copies_are_numbered_from_two()
    {
        Assert.Equal("WIN02 (copy 2)", ConnectionCopy.NameFor("WIN02", ["WIN02", "WIN02 (copy)"], First, Nth));
        Assert.Equal("WIN02 (copy 3)", ConnectionCopy.NameFor("WIN02", ["WIN02", "WIN02 (copy)", "WIN02 (copy 2)"], First, Nth));
    }

    [Fact]
    public void Names_are_compared_the_way_a_user_reads_them()
    {
        Assert.Equal("WIN02 (copy 2)", ConnectionCopy.NameFor("WIN02", ["win02 (COPY)"], First, Nth));
    }

    [Fact]
    public void The_name_always_fits_the_limit()
    {
        var longName = new string('x', ConnectionRules.MaxNameLength);

        var name = ConnectionCopy.NameFor(longName, [longName], First, Nth);

        Assert.True(name.Length <= ConnectionRules.MaxNameLength, $"{name.Length} characters");
        Assert.EndsWith(" (copy)", name);
    }
}
