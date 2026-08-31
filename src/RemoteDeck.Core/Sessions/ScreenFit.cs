using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Sessions;

/// <summary>One screen's working area, in virtual-screen coordinates.</summary>
public readonly record struct ScreenBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Turns a remembered placement into one that is usable on the screens present right now. A window
/// remembered on a monitor that has since been unplugged must not open off-screen where the user
/// cannot reach its title bar.
/// </summary>
public static class ScreenFit
{
    /// <summary>Height of the title-bar strip whose visibility decides whether a placement is still reachable.</summary>
    private const double TitleBarHeight = 40;

    /// <summary>
    /// The saved placement adjusted to the screens available now, or null when it belongs to a
    /// screen that is gone. The result always sits entirely inside one screen and respects the
    /// minimum size.
    /// </summary>
    public static DetachedWindowPlacement? Choose(
        DetachedWindowPlacement? saved,
        IReadOnlyList<ScreenBounds> screens,
        double minWidth = 640,
        double minHeight = 480)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (saved is null || screens.Count == 0) return null;

        var screen = MostOverlapping(saved, screens);
        if (screen is null) return null;

        var bounds = screen.Value;
        double width = Math.Clamp(saved.Width, minWidth, bounds.Width);
        double height = Math.Clamp(saved.Height, minHeight, bounds.Height);
        double left = Math.Clamp(saved.Left, bounds.Left, bounds.Right - width);
        double top = Math.Clamp(saved.Top, bounds.Top, bounds.Bottom - height);

        return saved with { Left = left, Top = top, Width = width, Height = height };
    }

    /// <summary>
    /// The screen showing the most of the window's title-bar strip, or null when none does. Any
    /// sliver of that strip is enough to adopt a screen — the placement is then pulled fully back
    /// inside it, so a barely visible window is rescued rather than dropped; only a window whose
    /// title bar misses every screen is forgotten.
    /// </summary>
    private static ScreenBounds? MostOverlapping(DetachedWindowPlacement saved, IReadOnlyList<ScreenBounds> screens)
    {
        ScreenBounds? best = null;
        double bestArea = 0;

        foreach (var screen in screens)
        {
            double overlapWidth = Math.Min(saved.Left + saved.Width, screen.Right) - Math.Max(saved.Left, screen.Left);
            double overlapHeight = Math.Min(saved.Top + TitleBarHeight, screen.Bottom) - Math.Max(saved.Top, screen.Top);
            if (overlapWidth <= 0 || overlapHeight <= 0) continue;

            double area = overlapWidth * overlapHeight;
            if (area <= bestArea) continue;

            bestArea = area;
            best = screen;
        }

        return best;
    }
}
