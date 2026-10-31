using System.Globalization;

namespace RemoteDeck.Core.Updates;

/// <summary>
/// The three numbers of a release. Only plain <c>major.minor.patch</c> is understood: a tag or a
/// build with anything else in it (a pre-release suffix, a fourth number) is refused rather than
/// guessed at, so it is never offered as an update nor compared with the running version.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>Reads a release tag: exactly "v", then three dot-separated numbers ("v0.9.0").</summary>
    public static bool TryParseTag(string? tag, out ReleaseVersion version)
    {
        version = default;
        return tag is not null && tag.StartsWith('v') && TryParseNumbers(tag.AsSpan(1), out version);
    }

    /// <summary>Reads the version of the running build ("0.8.0+7859f064"): the build metadata after
    /// the first '+' is dropped, what remains must be plain numbers.</summary>
    public static bool TryParseInformational(string? informationalVersion, out ReleaseVersion version)
    {
        version = default;
        if (informationalVersion is null) return false;

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        var numbers = plus < 0 ? informationalVersion.AsSpan() : informationalVersion.AsSpan(0, plus);
        return TryParseNumbers(numbers, out version);
    }

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>Exactly three numbers separated by dots. <see cref="NumberStyles.None"/> takes ASCII
    /// digits only: no sign, no space, no other script, and a number too big for an int fails.</summary>
    private static bool TryParseNumbers(ReadOnlySpan<char> text, out ReleaseVersion version)
    {
        version = default;

        var firstDot = text.IndexOf('.');
        if (firstDot < 0) return false;
        var rest = text[(firstDot + 1)..];
        var secondDot = rest.IndexOf('.');
        if (secondDot < 0) return false;
        var patchText = rest[(secondDot + 1)..];

        if (!int.TryParse(text[..firstDot], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(rest[..secondDot], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(patchText, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;

        version = new ReleaseVersion(major, minor, patch);
        return true;
    }
}
