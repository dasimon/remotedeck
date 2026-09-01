using RemoteDeck.Core.Sessions;

namespace RemoteDeck.Core.Tests.Sessions;

public sealed class ClosePlanTests
{
    [Fact]
    public void First_session_gets_the_per_session_budget()
        => Assert.Equal(TimeSpan.FromSeconds(5), ClosePlan.For(4, TimeSpan.Zero));

    [Fact]
    public void The_overall_budget_caps_the_last_sessions()
        => Assert.Equal(TimeSpan.FromSeconds(2), ClosePlan.For(3, TimeSpan.FromSeconds(28)));

    [Fact]
    public void An_exhausted_budget_gives_zero_not_a_negative_wait()
        => Assert.Equal(TimeSpan.Zero, ClosePlan.For(2, TimeSpan.FromSeconds(31)));

    [Fact]
    public void A_single_session_still_gets_five_seconds()
        => Assert.Equal(TimeSpan.FromSeconds(5), ClosePlan.For(1, TimeSpan.Zero));

    [Fact]
    public void Nothing_left_to_close_asks_for_nothing()
        => Assert.Equal(TimeSpan.Zero, ClosePlan.For(0, TimeSpan.Zero));
}
