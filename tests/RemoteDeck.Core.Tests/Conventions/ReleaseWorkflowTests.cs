using System.Text.RegularExpressions;

namespace RemoteDeck.Core.Tests.Conventions;

/// <summary>
/// Guards the parts of <c>release.yml</c> the built-in update depends on. None fails a build when it
/// is wrong: a missing channel stamp ships an executable that never looks for an update, which the
/// step checking the published executable exists to catch, and a missing <c>prerelease</c> flag lets
/// a release candidate be offered to everyone.
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
    public void The_published_executable_is_checked_for_the_release_channel_stamp()
    {
        // The stamp can be lost without any build failing (a property set later, a condition that no
        // longer matches); the step after the publish is what turns that into a failed release. It
        // looks for the attribute's serialised bytes: length-prefixed "UpdateChannel" (13) then
        // "release" (7).
        var workflow = Workflow();
        var step = Regex.Match(workflow, @"- name: Refuse to ship a binary that is not on the release channel\r?\n(?:[ \t]+[^\r\n]*\r?\n?)*");

        Assert.True(step.Success, "release.yml has no step checking the UpdateChannel stamp of the published executable.");
        Assert.Contains("publish/RemoteDeck.exe", step.Value, StringComparison.Ordinal);
        Assert.Contains("@(1, 0, 13)", step.Value, StringComparison.Ordinal);
        Assert.Contains("GetBytes('UpdateChannel')", step.Value, StringComparison.Ordinal);
        Assert.Contains("@(7) + [Text.Encoding]::UTF8.GetBytes('release')", step.Value, StringComparison.Ordinal);
        Assert.Contains("exit 1", step.Value, StringComparison.Ordinal);
        Assert.True(workflow.IndexOf(step.Value, StringComparison.Ordinal) > workflow.IndexOf("dotnet publish", StringComparison.Ordinal),
            "The UpdateChannel check must run after the publish.");
        Assert.True(workflow.IndexOf(step.Value, StringComparison.Ordinal) < workflow.IndexOf("action-gh-release", StringComparison.Ordinal),
            "The UpdateChannel check must run before the release is created.");
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
