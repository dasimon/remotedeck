using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RemoteDeck.Core.Tests.Conventions;

/// <summary>
/// Guards the motion rules the theme sheet states: three durations and no others, none above the
/// ceiling, and no animation written anywhere but the one class that reads them.
///
/// The failure this catches is the ordinary one — a view that grows its own
/// <c>Duration="0:0:0.4"</c> because 150 ms felt short that afternoon. Nothing else would notice:
/// it compiles, it runs, and the application slowly acquires four speeds.
/// </summary>
public sealed class MotionTests
{
    private static readonly string ThemeSheet = Path.Combine(RepoFiles.AppResources, "Theme.xaml");

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The ceiling the sheet names: past this, the user is waiting for the interface.</summary>
    private const int CeilingMilliseconds = 220;

    private static IReadOnlyDictionary<string, TimeSpan> Durations()
    {
        var root = XDocument.Load(ThemeSheet).Root!;
        return root.Elements()
            .Where(e => e.Name.LocalName == "Duration")
            .ToDictionary(
                e => e.Attribute(X + "Key")!.Value,
                e => TimeSpan.Parse(e.Value.Trim(), System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
    }

    [Fact]
    public void The_sheet_defines_exactly_the_three_motion_tokens()
    {
        var keys = Durations().Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["RdMotion", "RdMotionFast", "RdMotionSlow"], keys);
    }

    [Fact]
    public void Every_motion_token_is_positive_and_under_the_ceiling()
    {
        foreach (var (key, duration) in Durations())
        {
            Assert.True(duration > TimeSpan.Zero, $"{key} is zero: a token that animates nothing is a token nobody should reach for.");
            Assert.True(duration.TotalMilliseconds <= CeilingMilliseconds,
                $"{key} is {duration.TotalMilliseconds} ms, above the {CeilingMilliseconds} ms ceiling the sheet states.");
        }
    }

    [Fact]
    public void Fast_is_shorter_than_default_which_is_shorter_than_slow()
    {
        var d = Durations();

        Assert.True(d["RdMotionFast"] < d["RdMotion"], "Fast must be the shortest: it is what leaving takes.");
        Assert.True(d["RdMotion"] < d["RdMotionSlow"], "Slow must be the longest: it is the ceiling.");
    }

    [Fact]
    public void Every_duration_in_a_view_is_one_of_the_sheets_tokens()
    {
        // A Storyboard in a view is fine — the row hover in both lists is one, declarative and
        // argued in place. What is not fine is the number: a duration is written once, in the
        // sheet, and a view names it.
        var literal = new Regex(@"Duration\s*=\s*""(?!\{StaticResource RdMotion(Fast|Slow)?\})[^""]*""", RegexOptions.Compiled);
        var offenders = RepoFiles.AppXamlFiles()
            .Where(path => !path.EndsWith("Theme.xaml", StringComparison.Ordinal))
            .SelectMany(path => literal.Matches(File.ReadAllText(path)).Select(m => $"{Path.GetRelativePath(RepoFiles.Root, path)}: {m.Value}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Duration written as a literal in a view: {string.Join("; ", offenders)}. Name a token — RdMotionFast, RdMotion or RdMotionSlow.");
    }

    [Fact]
    public void No_code_but_Motion_builds_an_animation()
    {
        var pattern = new Regex(@"new\s+(Double|Color|Thickness|Point)Animation\b|new\s+Storyboard\b|BeginAnimation\(", RegexOptions.Compiled);
        var appSources = Directory.EnumerateFiles(Path.Combine(RepoFiles.Root, "src", "RemoteDeck.App"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.EndsWith("Motion.cs", StringComparison.Ordinal));

        var offenders = appSources
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepoFiles.Root, path))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Animation built outside Controls/Motion.cs: {string.Join(", ", offenders)}.");
    }
}
