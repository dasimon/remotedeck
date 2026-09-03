namespace RemoteDeck.Core.Tests.Conventions;

/// <summary>
/// Guards the artwork the build ships.
///
/// The icon's source is a PowerShell script, and the convention is "run it and commit what it
/// writes" — the release workflow publishes what is versioned and never generates artwork. That
/// convention has exactly one failure mode: editing the script and forgetting to commit its output,
/// which produces a release wearing the previous icon, or none. Nothing else in the repository
/// notices.
/// </summary>
public sealed class ShippedAssetTests
{
    /// <summary>The nine sizes the icon is drawn at, each rendered at its own size rather than
    /// downscaled — at 16 px a downscaled 256 px render turns the steps between the cards into a
    /// smear.</summary>
    private static readonly int[] ExpectedSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static string IconPath => Path.Combine(RepoFiles.AppResources, "RemoteDeck.ico");

    private static string TitleBarIconPath => Path.Combine(RepoFiles.AppResources, "RemoteDeck-32.png");

    [Fact]
    public void The_icon_files_are_committed()
    {
        Assert.True(File.Exists(IconPath), $"{IconPath} is missing. Run tools/icon/New-RemoteDeckIcon.ps1 and commit what it writes.");
        Assert.True(File.Exists(TitleBarIconPath), $"{TitleBarIconPath} is missing. Same script, same rule.");
    }

    [Fact]
    public void The_icon_carries_every_size_it_claims()
    {
        // Read the ICONDIR header directly rather than through System.Drawing: the point is to
        // assert what the shipped file contains, and a decoder that silently picks one frame would
        // defeat that.
        var bytes = File.ReadAllBytes(IconPath);

        Assert.True(bytes.Length > 6, "The icon file is truncated.");
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));  // reserved
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));  // 1 = icon, 2 would be a cursor

        int count = BitConverter.ToUInt16(bytes, 4);
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            // Width and height are single bytes, and 0 means 256 — the only way the format can
            // express its largest size.
            int width = bytes[6 + (i * 16)];
            sizes.Add(width == 0 ? 256 : width);
        }

        // Compared as sequences rather than arrays: xunit's array and ReadOnlySpan overloads are
        // ambiguous for a collection expression here.
        Assert.Equal(ExpectedSizes.AsEnumerable(), sizes.Order());
    }
}
