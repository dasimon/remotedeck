namespace RemoteDeck.Core.Model;

/// <summary>
/// The hue a group is drawn in, picked from the group's name: no setting, nothing stored, and the
/// same group gets the same hue on every machine. Two groups may share a hue; the name still tells
/// them apart.
/// </summary>
public static class GroupHue
{
    /// <summary>The palette index of <paramref name="groupName"/>, or <c>null</c> for no group.</summary>
    /// <param name="count">How many hues the palette has.</param>
    public static int? Of(string? groupName, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var key = groupName?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(key)) return null;

        // FNV-1a: stable across processes, unlike string.GetHashCode.
        var hash = 2166136261u;
        foreach (var c in key)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % (uint)count);
    }
}
