namespace RemoteDeck.Core.Updates;

/// <summary>
/// The update channel setting and the GitHub repository each value allows. Anything else, or no
/// setting at all, means the app neither checks nor updates: the only way to receive an update is
/// to name a channel on purpose.
/// </summary>
public static class UpdateChannel
{
    public const string Release = "release";

    /// <summary>The throw-away repository used to try the update mechanism end to end.</summary>
    public const string Test = "test";

    /// <summary>The "owner/name" of the repository to read releases from; null when the channel is
    /// not exactly one of the known names (the comparison is ordinal, case included).</summary>
    public static string? RepositoryFor(string? channel) => channel switch
    {
        Release => "dasimon/remotedeck",
        Test => "dasimon/remotedeck-update-test",
        _ => null,
    };
}
