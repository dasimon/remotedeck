using System.Runtime.CompilerServices;

namespace RemoteDeck.Core.Tests.Conventions;

/// <summary>
/// Locates the repository's own files from a test run.
///
/// The tests in this folder are unlike every other test in the solution: they assert nothing about
/// <c>RemoteDeck.Core</c>'s behaviour, they assert things about the <em>repository</em> — that the
/// two resource files agree, that no user-visible string was left in the markup, that the icon the
/// build ships is really there. They live in this test project because it is the only one the
/// solution has, and creating a second one to hold four files would cost more than it explains.
/// </summary>
/// <remarks>
/// The root is derived from this file's own compile-time path, not from the output directory.
/// Walking up from <c>AppContext.BaseDirectory</c> is the obvious approach and it is wrong: the
/// output directory is only inside the repository by convention, and it leaves it the moment anyone
/// passes <c>-p:BaseOutputPath=…</c> or adopts .NET's <c>artifacts</c> layout — measured, not
/// assumed, by running the suite that way and watching every test here fail. The walk survives as a
/// fallback for the case this file has been moved.
/// </remarks>
internal static class RepoFiles
{
    /// <summary>The repository root — the directory holding <c>RemoteDeck.sln</c>.</summary>
    public static string Root { get; } = FindRoot();

    public static string AppResources => Path.Combine(Root, "src", "RemoteDeck.App", "Resources");

    public static string EnglishResx => Path.Combine(AppResources, "Strings.resx");

    public static string FrenchResx => Path.Combine(AppResources, "Strings.fr.resx");

    public static string DesignerFile => Path.Combine(AppResources, "Strings.Designer.cs");

    /// <summary>Every XAML file of the WPF project, which is where user-visible text can hide.</summary>
    public static IReadOnlyList<string> AppXamlFiles() =>
        [.. Directory.EnumerateFiles(Path.Combine(Root, "src", "RemoteDeck.App"), "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)];

    private static string FindRoot([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(thisFile), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(start)) continue;

            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RemoteDeck.sln")))
                {
                    return dir.FullName;
                }
            }
        }

        throw new InvalidOperationException(
            $"RemoteDeck.sln was found neither above '{thisFile}' nor above '{AppContext.BaseDirectory}'. "
            + "These tests read the repository's own files and cannot run outside a checkout — if the "
            + "build machine and the test machine are not the same one, they never will.");
    }
}
