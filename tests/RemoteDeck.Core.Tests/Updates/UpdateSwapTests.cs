using System.Security.Cryptography;
using System.Text;
using RemoteDeck.Core.Updates;

namespace RemoteDeck.Core.Tests.Updates;

/// <summary>Replacing the executable by the downloaded one, never leaving the user without a valid file.
/// Plain text files in temporary folders stand in for the executables.</summary>
public sealed class UpdateSwapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RemoteDeckSwap-" + Guid.NewGuid().ToString("N"));
    private readonly string _target;
    private readonly string _source;

    public UpdateSwapTests()
    {
        Directory.CreateDirectory(_directory);
        _target = Path.Combine(_directory, "RemoteDeck.exe");
        _source = Path.Combine(_directory, "download.bin");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static string HashOf(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void Apply_replaces_the_target_and_keeps_the_old_one_aside()
    {
        File.WriteAllText(_target, "v1");
        File.WriteAllText(_source, "v2");

        var result = UpdateSwap.Apply(_source, HashOf("v2"), _target);

        Assert.Equal(SwapResult.Done, result);
        Assert.Equal("v2", File.ReadAllText(_target));
        Assert.Equal("v1", File.ReadAllText(_target + ".old"));
        Assert.False(File.Exists(_target + ".new"));
    }

    [Fact]
    public void Apply_with_a_wrong_hash_touches_nothing()
    {
        File.WriteAllText(_target, "v1");
        File.WriteAllText(_source, "v2");

        var result = UpdateSwap.Apply(_source, HashOf("something else"), _target);

        Assert.Equal(SwapResult.HashMismatch, result);
        Assert.Equal("v1", File.ReadAllText(_target));
        Assert.False(File.Exists(_target + ".new"));
        Assert.False(File.Exists(_target + ".old"));
    }

    [Fact]
    public void Apply_with_a_truncated_source_touches_nothing()
    {
        File.WriteAllText(_target, "v1");
        File.WriteAllText(_source, "version 2 of the executable");
        var expected = HashOf("version 2 of the executable");
        File.WriteAllText(_source, "version 2 of");

        var result = UpdateSwap.Apply(_source, expected, _target);

        Assert.Equal(SwapResult.HashMismatch, result);
        Assert.Equal("v1", File.ReadAllText(_target));
        Assert.False(File.Exists(_target + ".new"));
        Assert.False(File.Exists(_target + ".old"));
    }

    [Fact]
    public void Apply_accepts_an_upper_case_expected_hash()
    {
        File.WriteAllText(_target, "v1");
        File.WriteAllText(_source, "v2");

        var result = UpdateSwap.Apply(_source, HashOf("v2").ToUpperInvariant(), _target);

        Assert.Equal(SwapResult.Done, result);
        Assert.Equal("v2", File.ReadAllText(_target));
    }

    [Fact]
    public void Apply_overwrites_a_stale_old()
    {
        File.WriteAllText(_target, "v1");
        File.WriteAllText(_target + ".old", "v0");
        File.WriteAllText(_source, "v2");

        var result = UpdateSwap.Apply(_source, HashOf("v2"), _target);

        Assert.Equal(SwapResult.Done, result);
        Assert.Equal("v2", File.ReadAllText(_target));
        Assert.Equal("v1", File.ReadAllText(_target + ".old"));
    }

    [Fact]
    public void Apply_restores_when_the_old_cannot_be_replaced()
    {
        File.WriteAllText(_target, "v1");
        File.WriteAllText(_target + ".old", "v0");
        File.WriteAllText(_source, "v2");

        SwapResult result;
        using (new FileStream(_target + ".old", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = UpdateSwap.Apply(_source, HashOf("v2"), _target);
        }

        Assert.Equal(SwapResult.Failed, result);
        Assert.Equal("v1", File.ReadAllText(_target));
        Assert.False(File.Exists(_target + ".new"));
    }

    [Fact]
    public void Apply_with_a_missing_source_fails_cleanly()
    {
        File.WriteAllText(_target, "v1");

        var result = UpdateSwap.Apply(_source, HashOf("v2"), _target);

        Assert.Equal(SwapResult.Failed, result);
        Assert.Equal("v1", File.ReadAllText(_target));
        Assert.False(File.Exists(_target + ".new"));
        Assert.False(File.Exists(_target + ".old"));
    }

    [Fact]
    public void Sha256Of_returns_the_lower_case_hex_hash()
    {
        File.WriteAllText(_source, "v2");

        Assert.Equal(HashOf("v2"), UpdateSwap.Sha256Of(_source));
    }

    [Fact]
    public void CleanUp_removes_the_leftovers_and_the_work_directory()
    {
        var work = Path.Combine(_directory, "update");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "RemoteDeck.exe"), "v2");
        File.WriteAllText(_target, "v2");
        File.WriteAllText(_target + ".old", "v1");
        File.WriteAllText(_target + ".new", "partial");

        UpdateSwap.CleanUp(_target, work);

        Assert.True(File.Exists(_target));
        Assert.False(File.Exists(_target + ".old"));
        Assert.False(File.Exists(_target + ".new"));
        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public void CleanUp_ignores_a_locked_old()
    {
        var work = Path.Combine(_directory, "update");
        Directory.CreateDirectory(work);
        File.WriteAllText(_target, "v2");
        File.WriteAllText(_target + ".old", "v1");

        using (new FileStream(_target + ".old", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = Record.Exception(() => UpdateSwap.CleanUp(_target, work));

            Assert.Null(exception);
        }

        Assert.False(Directory.Exists(work));
        Assert.Equal("v2", File.ReadAllText(_target));
    }
}
