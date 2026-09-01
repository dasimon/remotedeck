using RemoteDeck.Core.Sessions;
using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Tests.Sessions;

public sealed class ScreenFitTests
{
    private static readonly ScreenBounds Primary = new(0, 0, 1920, 1080);
    private static readonly ScreenBounds Secondary = new(1920, 0, 1920, 1080);
    private static readonly IReadOnlyList<ScreenBounds> Both = [Primary, Secondary];

    [Fact]
    public void No_saved_placement_means_no_suggestion()
        => Assert.Null(ScreenFit.Choose(null, Both));

    [Fact]
    public void A_placement_fully_on_a_screen_is_kept_as_is()
    {
        var saved = new DetachedWindowPlacement(2000, 100, 1280, 800, false);

        Assert.Equal(saved, ScreenFit.Choose(saved, Both));
    }

    [Fact]
    public void A_placement_on_a_screen_that_is_gone_is_dropped()
    {
        var saved = new DetachedWindowPlacement(2000, 100, 1280, 800, false);

        Assert.Null(ScreenFit.Choose(saved, [Primary]));
    }

    [Fact]
    public void A_barely_visible_placement_is_pulled_back_into_its_screen()
    {
        var saved = new DetachedWindowPlacement(1850, 100, 1280, 800, false);

        var fitted = ScreenFit.Choose(saved, [Primary]);

        Assert.NotNull(fitted);
        Assert.True(fitted.Left + fitted.Width <= Primary.Width);
        Assert.Equal(800, fitted.Height);
    }

    [Fact]
    public void A_window_larger_than_its_screen_is_shrunk_to_fit()
    {
        var saved = new DetachedWindowPlacement(0, 0, 3000, 2000, false);

        var fitted = ScreenFit.Choose(saved, [Primary]);

        Assert.NotNull(fitted);
        Assert.Equal(1920, fitted.Width);
        Assert.Equal(1080, fitted.Height);
    }

    [Fact]
    public void A_size_below_the_minimum_is_raised()
    {
        var saved = new DetachedWindowPlacement(10, 10, 100, 50, false);

        var fitted = ScreenFit.Choose(saved, [Primary]);

        Assert.NotNull(fitted);
        Assert.Equal(640, fitted.Width);
        Assert.Equal(480, fitted.Height);
    }

    [Fact]
    public void The_full_screen_flag_survives_fitting()
    {
        var saved = new DetachedWindowPlacement(1850, 100, 1280, 800, true);

        Assert.True(ScreenFit.Choose(saved, [Primary])!.FullScreen);
    }

    [Fact]
    public void No_screen_at_all_means_no_suggestion()
        => Assert.Null(ScreenFit.Choose(new DetachedWindowPlacement(0, 0, 800, 600, false), []));
}
