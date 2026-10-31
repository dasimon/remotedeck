using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Tests.Conventions;

/// <summary>
/// Guards the two lines of <c>release.yml</c> the built-in update depends on. Neither fails a build
/// when it is wrong: a missing channel stamp ships an executable that never looks for an update,
/// and a missing <c>prerelease</c> flag lets a release candidate be offered to everyone.
/// </summary>
public sealed class ReleaseWorkflowTests
{
    private static string Workflow() =>
        File.ReadAllText(Path.Combine(RepoFiles.Root, ".github", "workflows", "release.yml"));

    [Fact]
    public void The_publish_command_stamps_the_release_channel()
    {
        // The channel is fixed at compile time (see RemoteDeck.App.csproj), so this is the only place
        // that decides which repository a shipped executable updates from.
        var publish = Regex.Match(Workflow(), @"dotnet publish src/[^\r\n]*(?:\r?\n[ \t]+--?[^\r\n]*)*");

        Assert.True(publish.Success, "release.yml has no 'dotnet publish' command.");
        Assert.Contains("-p:UpdateChannel=release", publish.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_workflow_never_stamps_the_test_channel()
    {
        // The test channel reads a throw-away repository; an executable built here with it would
        // update from nothing, or from whatever that repository holds.
        Assert.DoesNotContain("UpdateChannel=test", Workflow(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_with_a_hyphen_is_published_as_a_pre_release()
    {
        // Pre-releases are never offered by the update check ("latest" skips them), but only if
        // GitHub knows they are one. The tag decides: v1.2.0-rc.1 is a pre-release, v1.2.0 is not.
        Assert.Contains("prerelease: ${{ contains(github.ref_name, '-') }}", Workflow(), StringComparison.Ordinal);
    }
}
