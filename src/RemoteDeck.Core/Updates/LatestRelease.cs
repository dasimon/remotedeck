using System.Text.Json;

namespace RemoteDeck.Core.Updates;

/// <summary>
/// What the update check needs from GitHub's "latest release" answer. <see cref="Parse"/> accepts
/// only a complete, published, plainly versioned release, and answers null to anything else
/// (a web page from a captive portal or a proxy, an error object, a half-built release): the caller
/// then simply has nothing to offer.
/// </summary>
/// <param name="Version">The release tag, read as three numbers.</param>
/// <param name="PageUrl">The release page, for "what's new".</param>
/// <param name="ExeUrl">Where <c>RemoteDeck.exe</c> is downloaded from (redirected by GitHub).</param>
/// <param name="ExeSize">The size in bytes GitHub reports for the executable; always above zero.</param>
/// <param name="ExeDigest">The SHA-256 GitHub reports for the executable, 64 lower-case hexadecimal
/// characters without the "sha256:" prefix, or null when absent or unreadable.</param>
/// <param name="SumsUrl">Where the <c>SHA256SUMS</c> file is downloaded from.</param>
public sealed record LatestRelease(
    ReleaseVersion Version,
    Uri PageUrl,
    Uri ExeUrl,
    long ExeSize,
    string? ExeDigest,
    Uri SumsUrl)
{
    private const string ExeName = "RemoteDeck.exe";
    private const string SumsName = "SHA256SUMS";
    private const string DigestPrefix = "sha256:";
    private const int DigestLength = 64;

    /// <summary>Reads the answer of <c>GET /repos/{owner}/{repo}/releases/latest</c>; never throws.</summary>
    public static LatestRelease? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static LatestRelease? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Both flags must be present and false: a missing one is not taken for a published release.
        if (!IsFalse(root, "prerelease") || !IsFalse(root, "draft")) return null;
        if (GetString(root, "tag_name") is not { } tag || !ReleaseVersion.TryParseTag(tag, out var version)) return null;
        if (GetHttpsUrl(root, "html_url") is not { } pageUrl) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        JsonElement? exe = null, sums = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            var name = GetString(asset, "name");
            if (name == ExeName) exe ??= asset;
            else if (name == SumsName) sums ??= asset;
        }

        if (exe is not { } exeAsset || sums is not { } sumsAsset) return null;
        if (GetHttpsUrl(exeAsset, "browser_download_url") is not { } exeUrl) return null;
        if (GetHttpsUrl(sumsAsset, "browser_download_url") is not { } sumsUrl) return null;
        // TryGetInt64 throws on anything but a number, so the kind is checked first.
        if (!exeAsset.TryGetProperty("size", out var size)
            || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out var exeSize)
            || exeSize <= 0)
            return null;

        return new LatestRelease(version, pageUrl, exeUrl, exeSize, ReadDigest(exeAsset), sumsUrl);
    }

    /// <summary>"sha256:" and 64 hexadecimal characters, else null (an unknown algorithm or a damaged
    /// value is not used; the executable is still checked against <c>SHA256SUMS</c>).</summary>
    private static string? ReadDigest(JsonElement asset)
    {
        if (GetString(asset, "digest") is not { } digest
            || !digest.StartsWith(DigestPrefix, StringComparison.Ordinal))
            return null;

        var hex = digest.AsSpan(DigestPrefix.Length);
        if (hex.Length != DigestLength) return null;
        foreach (var c in hex)
            if (!char.IsAsciiHexDigit(c)) return null;
        return hex.ToString().ToLowerInvariant();
    }

    private static bool IsFalse(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.False;

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? GetHttpsUrl(JsonElement element, string property) =>
        GetString(element, property) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;
}
