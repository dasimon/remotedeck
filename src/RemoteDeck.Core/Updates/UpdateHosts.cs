namespace RemoteDeck.Core.Updates;

/// <summary>
/// The only addresses an update may be fetched from: GitHub's API, its web host (where the asset
/// links point) and the hosts it redirects downloads to. Applied to the first address and to every
/// redirection hop.
/// </summary>
public static class UpdateHosts
{
    private static readonly string[] Allowed =
    [
        "api.github.com",
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
    ];

    /// <summary>True for an absolute https address on one of the allowed hosts, with the default port
    /// and no credentials. The host is compared whole, so "github.com.evil.example" does not pass.</summary>
    public static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.UserInfo.Length == 0
        && Allowed.Contains(uri.Host, StringComparer.Ordinal);
}
