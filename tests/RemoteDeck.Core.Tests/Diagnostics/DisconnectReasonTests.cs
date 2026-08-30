using RemoteDeck.Core.Diagnostics;

namespace RemoteDeck.Core.Tests.Diagnostics;

public sealed class DisconnectReasonTests
{
    /// <summary>
    /// The 47 codes documented for <c>IMsTscAxEvents::OnDisconnected</c>, restated here on
    /// purpose: the table under test must cover every one of them, so the expectation cannot
    /// be sourced from the implementation it verifies.
    /// </summary>
    public static TheoryData<int> DocumentedCodes =>
    [
        0, 1, 2, 3,
        260, 262, 264,
        516, 518, 520,
        772, 774, 776,
        1028, 1030, 1032,
        1286, 1288,
        1540, 1542, 1544,
        1796, 1798,
        2052, 2055, 2056,
        2308, 2310, 2312,
        2566, 2567,
        2822, 2823,
        3078, 3079, 3080,
        3335, 3591, 3847,
        4615, 5639, 5895,
        6151, 6919, 7175,
        8455, 8711,
    ];

    [Fact]
    public void Codes_zero_to_three_are_not_errors()
    {
        foreach (var reason in new[] { 0, 1, 2, 3 })
        {
            var described = DisconnectReason.Describe(reason);

            Assert.Equal(DisconnectCategory.NotAnError, described.Category);
            Assert.False(described.IsError, $"code {reason} must not be an error");
        }
    }

    [Fact]
    public void Connection_timed_out_is_a_network_error()
    {
        var described = DisconnectReason.Describe(264);

        Assert.Equal(264, described.Reason);
        Assert.Equal(DisconnectCategory.Network, described.Category);
        Assert.Equal("Connection timed out", described.Title);
        Assert.True(described.IsError);
    }

    [Fact]
    public void Logon_failure_is_an_authentication_error()
    {
        var described = DisconnectReason.Describe(2055);

        Assert.Equal(DisconnectCategory.Authentication, described.Category);
        Assert.Equal("Logon failed", described.Title);
        Assert.True(described.IsError);
    }

    [Fact]
    public void Decryption_error_is_a_security_error()
    {
        Assert.Equal(DisconnectCategory.Security, DisconnectReason.Describe(3078).Category);
    }

    [Fact]
    public void Licensing_timeout_is_a_licensing_error()
    {
        Assert.Equal(DisconnectCategory.Licensing, DisconnectReason.Describe(2312).Category);
    }

    [Fact]
    public void Out_of_memory_is_a_resources_error()
    {
        Assert.Equal(DisconnectCategory.Resources, DisconnectReason.Describe(518).Category);
    }

    [Fact]
    public void Internal_error_is_an_internal_error()
    {
        Assert.Equal(DisconnectCategory.Internal, DisconnectReason.Describe(1032).Category);
    }

    [Fact]
    public void Undocumented_code_falls_back_to_a_generic_description()
    {
        var described = DisconnectReason.Describe(424242);

        Assert.Equal(424242, described.Reason);
        Assert.Equal(DisconnectCategory.Unknown, described.Category);
        Assert.Contains("424242", described.Title, StringComparison.Ordinal);
        Assert.True(described.IsError);
    }

    [Theory]
    [MemberData(nameof(DocumentedCodes))]
    public void Every_documented_code_has_a_category_and_a_title(int reason)
    {
        var described = DisconnectReason.Describe(reason);

        Assert.Equal(reason, described.Reason);
        Assert.NotEqual(DisconnectCategory.Unknown, described.Category);
        Assert.False(string.IsNullOrWhiteSpace(described.Title), $"code {reason} has no title");
    }
}
