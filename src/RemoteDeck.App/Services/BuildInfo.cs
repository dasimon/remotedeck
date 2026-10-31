using System.Reflection;
using RemoteDeck.Core.Updates;

namespace RemoteDeck.App.Services;

/// <summary>
/// What this executable knows about itself and may do about updates, read once from the assembly.
/// </summary>
/// <remarks>
/// The update channel is assembly metadata stamped by the build (see <c>RemoteDeck.App.csproj</c>),
/// never a setting: there is nothing on the user's machine to edit. A build without the stamp has
/// no repository to read from, so <see cref="CanUpdate"/> is false and the app stays silent.
/// </remarks>
internal static class BuildInfo
{
    private const string UpdateChannelKey = "UpdateChannel";

    private static readonly Assembly? Entry = Assembly.GetEntryAssembly();

    /// <summary>The informational version ("0.8.0+7859f064"); "?" when the assembly carries none.</summary>
    public static string InformationalVersion { get; } =
        Entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>The channel the build was stamped with; null for a plain build.</summary>
    public static string? UpdateChannel { get; } = Entry?.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => string.Equals(a.Key, UpdateChannelKey, StringComparison.Ordinal))?.Value;

    /// <summary>The "owner/name" of the repository to read releases from; null for an unknown channel.</summary>
    public static string? UpdateRepository { get; } = RemoteDeck.Core.Updates.UpdateChannel.RepositoryFor(UpdateChannel);

    /// <summary>The running version, or null when the informational version is not a plain
    /// MAJOR.MINOR.PATCH (a pre-release build never compares itself with a release).</summary>
    public static ReleaseVersion? Version { get; } =
        ReleaseVersion.TryParseInformational(InformationalVersion, out var version) ? version : null;

    /// <summary>True when there is a repository to ask and a running version to compare with.</summary>
    public static bool CanUpdate => UpdateRepository is not null && Version is not null;
}
