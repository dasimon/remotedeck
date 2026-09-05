using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Tests.Model;

public sealed class ConnectionRulesTests
{
    [Fact]
    public void A_connection_that_says_nothing_about_server_authentication_gets_a_warning_not_silence()
    {
        // Measured 2026-09-05: left to itself the control's AuthenticationLevel is 0 — "no
        // authentication of the server" — so a connection created with "Default" would accept a
        // spoofed host without a word. mstsc's own default is 2: attempt, and prompt on failure.
        Assert.Equal(2, ConnectionRules.EffectiveAuthenticationLevel(null));
        Assert.Equal(ConnectionRules.DefaultAuthenticationLevel, ConnectionRules.EffectiveAuthenticationLevel(null));
    }

    [Fact]
    public void An_explicit_server_authentication_level_is_kept_as_it_is()
    {
        // Including 0: a user who chose "no server authentication" in the editor chose it.
        Assert.Equal(0, ConnectionRules.EffectiveAuthenticationLevel(0));
        Assert.Equal(1, ConnectionRules.EffectiveAuthenticationLevel(1));
        Assert.Equal(2, ConnectionRules.EffectiveAuthenticationLevel(2));
    }

    [Fact]
    public void Valid_connection_reports_no_error()
        => Assert.Empty(ConnectionRules.Validate("Web01", "web01.corp", 3389, DisplayMode.Dynamic, null, null));

    [Fact]
    public void Name_and_host_are_required()
    {
        var errors = ConnectionRules.Validate("  ", "", 3389, DisplayMode.Dynamic, null, null);

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Port_must_be_between_1_and_65535()
    {
        Assert.Single(ConnectionRules.Validate("Web01", "web01.corp", 0, DisplayMode.Dynamic, null, null));
        Assert.Single(ConnectionRules.Validate("Web01", "web01.corp", 65536, DisplayMode.Dynamic, null, null));
        Assert.Empty(ConnectionRules.Validate("Web01", "web01.corp", 65535, DisplayMode.Dynamic, null, null));
    }

    [Fact]
    public void Missing_port_is_reported_as_required()
    {
        var errors = ConnectionRules.Validate("Web01", "web01.corp", null, DisplayMode.Dynamic, null, null);

        Assert.Equal([ConnectionError.PortRequired], errors);
    }

    [Fact]
    public void Host_must_not_contain_spaces()
        => Assert.Single(ConnectionRules.Validate("Web01", "web 01.corp", 3389, DisplayMode.Dynamic, null, null));

    [Fact]
    public void Fixed_mode_requires_dimensions_in_range()
    {
        Assert.Equal(2, ConnectionRules.Validate("Web01", "web01.corp", 3389, DisplayMode.Fixed, null, null).Count);
        Assert.Equal(2, ConnectionRules.Validate("Web01", "web01.corp", 3389, DisplayMode.Scaled, 639, 479).Count);
        Assert.Empty(ConnectionRules.Validate("Web01", "web01.corp", 3389, DisplayMode.Fixed, 1920, 1080));
    }

    [Fact]
    public void Fixed_dimensions_stop_at_the_upper_bound()
    {
        Assert.Single(ConnectionRules.Validate("Web01", "web01.corp", 3389, DisplayMode.Fixed, 8193, 1080));
        Assert.Empty(ConnectionRules.Validate("Web01", "web01.corp", 3389, DisplayMode.Fixed, 8192, 1080));
    }

    [Fact]
    public void Dynamic_mode_ignores_dimensions()
        => Assert.Empty(ConnectionRules.Validate("Web01", "web01.corp", 3389, DisplayMode.Dynamic, 1, 1));
}
