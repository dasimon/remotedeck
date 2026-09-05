using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Tests.Conventions;

/// <summary>
/// Guards the sheet's first sentence — colours are never literal in a view — and the one piece of
/// accessibility that costs nothing: a button with a picture and no words has to say what it is.
///
/// Both were review findings on 2026-09-05: two <c>#FF000000</c> in views for the ground behind a
/// remote desktop, and eleven icon-only buttons with a tooltip a sighted user can read and no
/// name a screen reader can.
/// </summary>
public sealed class ThemeTests
{
    private static readonly Regex HexColour = new(@"=\s*""#[0-9A-Fa-f]{6,8}""", RegexOptions.Compiled);

    private static readonly Regex Button = new(@"<(ui:Button|ToggleButton|Button)\b([^>]*?)/?>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static IEnumerable<string> Views() =>
        RepoFiles.AppXamlFiles().Where(path => !path.EndsWith("Theme.xaml", StringComparison.Ordinal));

    [Fact]
    public void No_view_writes_a_colour_as_a_literal()
    {
        // The sheet derives every brush from the theme so that accent and light/dark follow
        // Windows live. A literal in a view is a colour that follows nothing.
        var offenders = Views()
            .SelectMany(path => HexColour.Matches(File.ReadAllText(path))
                .Select(m => $"{Path.GetRelativePath(RepoFiles.Root, path)}: {m.Value.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Literal colour in a view: {string.Join("; ", offenders)}. Name a token from Theme.xaml.");
    }

    [Fact]
    public void No_view_writes_a_font_size_as_a_literal()
    {
        // Three sizes in the sheet; a view names one. Fourteen literals said the same three
        // numbers fourteen times before the tokens existed, and the fifteenth would have been 13.
        var literal = new Regex(@"FontSize\s*=\s*""[0-9.]+""", RegexOptions.Compiled);
        var offenders = Views()
            .SelectMany(path => literal.Matches(File.ReadAllText(path))
                .Select(m => $"{Path.GetRelativePath(RepoFiles.Root, path)}: {m.Value}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Literal font size in a view: {string.Join("; ", offenders)}. Name RdFontSm, RdFontMd or RdFont.");
    }

    [Fact]
    public void The_sheet_defines_exactly_the_three_type_sizes_and_the_hero_glyph()
    {
        var sheet = File.ReadAllText(Path.Combine(RepoFiles.AppResources, "Theme.xaml"));
        var sizes = Regex.Matches(sheet, @"<sys:Double x:Key=""(Rd(?:Font\w*|GlyphHero))"">([0-9.]+)</sys:Double>")
            .ToDictionary(m => m.Groups[1].Value, m => double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(["RdFont", "RdFontMd", "RdFontSm", "RdGlyphHero"], sizes.Keys.Order(StringComparer.Ordinal));
        Assert.True(sizes["RdFontSm"] < sizes["RdFontMd"] && sizes["RdFontMd"] < sizes["RdFont"], "Sm < Md < default, or the names lie.");
    }

    [Fact]
    public void Every_button_that_shows_only_an_icon_has_a_name_for_a_screen_reader()
    {
        // WPF does not fall back to the tooltip for the accessible name; without
        // AutomationProperties.Name, an icon-only button is announced as "button" and nothing else.
        var offenders = new List<string>();
        foreach (var path in Views())
        {
            foreach (Match m in Button.Matches(File.ReadAllText(path)))
            {
                var attributes = m.Groups[2].Value;
                var iconOnly = attributes.Contains("Icon=", StringComparison.Ordinal) && !attributes.Contains("Content=", StringComparison.Ordinal);
                if (iconOnly && !attributes.Contains("AutomationProperties.Name=", StringComparison.Ordinal))
                {
                    var name = Regex.Match(attributes, @"x:Name=""([^""]+)""");
                    offenders.Add($"{Path.GetRelativePath(RepoFiles.Root, path)}: {(name.Success ? name.Groups[1].Value : "(unnamed)")}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"Icon-only button with no AutomationProperties.Name: {string.Join("; ", offenders)}. Give it the same resource as its ToolTip.");
    }
}
