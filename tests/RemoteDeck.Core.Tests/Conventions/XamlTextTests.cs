using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Tests.Conventions;

/// <summary>
/// Catches user-visible text left as a literal in the markup instead of coming from
/// <c>Strings.resx</c>.
///
/// The interface ships in English and French and follows the Windows display language with nothing
/// to configure, so a literal is not a style problem: it is a sentence that stays English forever,
/// in a build nobody tests in French. This is the cheapest possible guard — it reads the XAML as
/// text — and it is worth having because the failure is invisible to everyone whose Windows is in
/// English.
/// </summary>
public sealed class XamlTextTests
{
    /// <summary>
    /// The attributes that put text in front of a user. Deliberately not exhaustive: this is a
    /// tripwire, not a proof. Add an attribute here the day something slips through it.
    /// </summary>
    private static readonly string[] TextAttributes =
    [
        "Header", "Content", "Text", "ToolTip", "PlaceholderText", "InputGestureText", "Title",
    ];

    private static readonly Regex AttributePattern = new(
        $@"\b(?<attribute>{string.Join("|", TextAttributes)})\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// The product name. It is the one word in the interface that is deliberately the same in every
    /// language, so it is the one literal allowed in the markup. Kept as an explicit list rather
    /// than a loose rule: an allowance nobody can enumerate is an allowance that grows.
    /// </summary>
    private static readonly string[] Untranslated = ["RemoteDeck"];

    private static readonly Regex CharacterReference = new(@"&#x[0-9A-Fa-f]+;|&#\d+;", RegexOptions.Compiled);

    /// <summary>
    /// Values that are not prose and never will be. A markup extension (<c>{Binding …}</c>,
    /// <c>{x:Static …}</c>) is the correct answer and is what most of these attributes hold; the
    /// rest are structural.
    /// </summary>
    private static bool IsAcceptable(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0) return true;

        // A markup extension — the whole point of this test is that the value should be one.
        if (trimmed.StartsWith('{')) return true;

        if (Untranslated.Contains(trimmed, StringComparer.Ordinal)) return true;

        // Icon glyphs are written as character references (Segoe MDL2 lives in the private use
        // area). They must be removed before the letter test, because the reference itself is
        // spelled with letters — "&#xE735;" would otherwise read as prose.
        var withoutGlyphs = CharacterReference.Replace(trimmed, string.Empty);

        // What is left carrying no letter at all is a symbol, a number or punctuation, not a
        // sentence someone has to read.
        return !withoutGlyphs.Any(char.IsLetter);
    }

    [Fact]
    public void No_user_visible_text_is_hard_coded_in_the_markup()
    {
        var offenders = new List<string>();

        foreach (var file in RepoFiles.AppXamlFiles())
        {
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(RepoFiles.Root, file);

            foreach (Match match in AttributePattern.Matches(text))
            {
                var value = match.Groups["value"].Value;
                if (IsAcceptable(value)) continue;

                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{relative}:{line} {match.Groups["attribute"].Value}=\"{value}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            "User-visible text must come from Strings.resx, so the French build has it too:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Every_static_string_reference_in_the_markup_names_a_key_that_exists()
    {
        // `{x:Static res:Strings.Foo}` compiles only if the property exists, so this cannot catch a
        // typo — but it does catch a property left behind in Strings.Designer.cs after its key was
        // removed from the resx, which compiles and returns null at runtime.
        var english = System.Xml.Linq.XDocument.Load(RepoFiles.EnglishResx).Root!
            .Elements("data")
            .Select(d => d.Attribute("name")?.Value)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        var referenced = new Regex(@"res:Strings\.(?<key>\w+)", RegexOptions.Compiled);
        var dangling = new List<string>();

        foreach (var file in RepoFiles.AppXamlFiles())
        {
            var relative = Path.GetRelativePath(RepoFiles.Root, file);
            foreach (Match match in referenced.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups["key"].Value;
                if (!english.Contains(key)) dangling.Add($"{relative}: {key}");
            }
        }

        Assert.True(dangling.Count == 0,
            $"Markup references resource keys that Strings.resx does not define: {string.Join(", ", dangling.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}.");
    }
}
