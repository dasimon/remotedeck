namespace RemoteDeck.Core.Sessions;

/// <summary>
/// How long the next session may take to close when the application is shutting down. Each session
/// gets five seconds, but the whole shutdown is capped: with detached windows the number of live
/// sessions is no longer bounded by what fits in a tab strip (design §6).
/// </summary>
public static class ClosePlan
{
    public const int PerSessionSeconds = 5;
    public const int OverallSeconds = 30;

    /// <summary>Time granted to the next close: the per-session slice, or whatever budget is left.</summary>
    public static TimeSpan For(int remainingSessions, TimeSpan elapsed)
    {
        if (remainingSessions <= 0) return TimeSpan.Zero;

        var left = TimeSpan.FromSeconds(OverallSeconds) - elapsed;
        if (left <= TimeSpan.Zero) return TimeSpan.Zero;

        var perSession = TimeSpan.FromSeconds(PerSessionSeconds);
        return left < perSession ? left : perSession;
    }
}
