using RemoteDeck.Core.Import;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Tests.Import;

public sealed class RdpFileImporterTests
{
    [Fact]
    public void Complete_file_maps_every_verified_key()
    {
        string[] lines =
        [
            "full address:s:srv-app01.corp.local:3390",
            "username:s:jdoe",
            "domain:s:CORP",
            "screen mode id:i:2",
            "desktopwidth:i:1920",
            "desktopheight:i:1080",
            "dynamic resolution:i:0",
            "audiomode:i:0",
            "redirectclipboard:i:1",
            "redirectprinters:i:1",
            "drivestoredirect:s:*",
            "authentication level:i:2",
            "enablerdsaadauth:i:1",
        ];

        var c = RdpFileImporter.Parse(@"C:\rdp\Prod app.rdp", lines);

        Assert.NotNull(c);
        Assert.Equal("Prod app", c.Name);
        Assert.Equal("srv-app01.corp.local", c.Host);
        Assert.Equal(3390, c.Port);
        Assert.Equal("jdoe", c.UserName);
        Assert.Equal("CORP", c.Domain);
        Assert.Equal(DisplayMode.Scaled, c.DisplayMode);
        Assert.Equal(1920, c.FixedWidth);
        Assert.Equal(1080, c.FixedHeight);
        Assert.True(c.RedirectClipboard);
        Assert.True(c.RedirectPrinters);
        Assert.True(c.RedirectDrives);
        Assert.True(c.RedirectAudio);
        Assert.True(c.UseWebAccount);
        Assert.Equal(2, c.AuthenticationLevel);
        Assert.Equal(@"C:\rdp\Prod app.rdp", c.Source);
        // Full screen is a window preference, not a resolution: noted, never mapped.
        Assert.Contains("screen mode id", Assert.Single(c.Warnings));
    }

    [Fact]
    public void Host_and_port_are_split_on_a_single_colon()
    {
        var c = RdpFileImporter.Parse("srv.rdp", ["full address:s:srv:3390"]);

        Assert.NotNull(c);
        Assert.Equal("srv", c.Host);
        Assert.Equal(3390, c.Port);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void An_unusable_port_falls_back_to_3389_and_warns()
    {
        var c = RdpFileImporter.Parse("srv.rdp", ["full address:s:host:abc"]);

        Assert.NotNull(c);
        Assert.Equal("host", c.Host);
        Assert.Equal(3389, c.Port);
        Assert.Contains("3389", Assert.Single(c.Warnings));
    }

    [Fact]
    public void A_file_without_a_usable_full_address_returns_null()
    {
        Assert.Null(RdpFileImporter.Parse("empty.rdp", ["", "; a comment", "screen mode id:i:2"]));
        Assert.Null(RdpFileImporter.Parse("blank.rdp", ["full address:s:   "]));
    }

    [Fact]
    public void Unsupported_keys_and_malformed_lines_are_counted_never_guessed()
    {
        string[] lines =
        [
            "full address:s:srv",
            "",
            "; a comment",
            "# another comment",
            "server port:i:3390",
            "administrative session:i:1",
            "connect to console:i:1",
            "gatewayhostname:s:gw.corp.local",
            "this line is malformed",
        ];

        var c = RdpFileImporter.Parse("srv.rdp", lines);

        Assert.NotNull(c);
        Assert.Equal(3389, c.Port);
        Assert.Equal("5 unsupported entries ignored", Assert.Single(c.Warnings));
    }

    [Fact]
    public void The_password_blob_is_never_read_nor_reported()
    {
        string[] lines = ["full address:s:srv", "password 51:b:01000000D08C9DDF0115D1118C7A00C04FC297EB"];

        var c = RdpFileImporter.Parse("srv.rdp", lines);

        Assert.NotNull(c);
        Assert.Empty(c.Warnings);
        Assert.DoesNotContain("password", string.Join('|', c.Warnings), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("01000000", string.Join('|', c.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_level_3_means_unspecified()
    {
        var c = RdpFileImporter.Parse("srv.rdp", ["full address:s:srv", "authentication level:i:3"]);

        Assert.NotNull(c);
        Assert.Null(c.AuthenticationLevel);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void Desktop_size_id_maps_to_a_documented_resolution()
    {
        var c = RdpFileImporter.Parse("srv.rdp", ["full address:s:srv", "desktop size id:i:2"]);

        Assert.NotNull(c);
        Assert.Equal(DisplayMode.Scaled, c.DisplayMode);
        Assert.Equal(1024, c.FixedWidth);
        Assert.Equal(768, c.FixedHeight);
    }

    [Fact]
    public void Dynamic_resolution_wins_over_a_fixed_desktop_size()
    {
        string[] lines = ["full address:s:srv", "dynamic resolution:i:1", "desktopwidth:i:1920", "desktopheight:i:1080"];

        var c = RdpFileImporter.Parse("srv.rdp", lines);

        Assert.NotNull(c);
        Assert.Equal(DisplayMode.Dynamic, c.DisplayMode);
        Assert.Null(c.FixedWidth);
        Assert.Null(c.FixedHeight);
    }

    [Fact]
    public void Key_matching_ignores_case_and_surrounding_space()
    {
        var c = RdpFileImporter.Parse("srv.rdp", ["  Full Address:S:srv  ", "UserName:S:jdoe", "RedirectClipboard:I:0"]);

        Assert.NotNull(c);
        Assert.Equal("srv", c.Host);
        Assert.Equal("jdoe", c.UserName);
        Assert.False(c.RedirectClipboard);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void ParseFolder_skips_the_files_it_cannot_read()
    {
        var failures = 0;
        IEnumerable<string> Read(string path)
        {
            if (path.Contains("locked", StringComparison.Ordinal))
            {
                failures++;
                throw new IOException("locked");
            }
            return [$"full address:s:{Path.GetFileNameWithoutExtension(path)}"];
        }

        var candidates = RdpFileImporter.ParseFolder(@"C:\rdp", Read, ["a.rdp", "locked.rdp", "b.rdp"]);

        Assert.Equal(new[] { "a", "b" }, candidates.Select(c => c.Host).ToArray());
        Assert.Equal(@"C:\rdp\a.rdp", candidates[0].Source);
        Assert.Equal(1, failures);
    }
}
