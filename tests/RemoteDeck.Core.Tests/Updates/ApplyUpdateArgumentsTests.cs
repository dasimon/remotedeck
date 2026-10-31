using System.Runtime.InteropServices;
using RemoteDeck.Core.Updates;

namespace RemoteDeck.Core.Tests.Updates;

/// <summary>The command line handed to the elevated instance that copies the verified executable.</summary>
public sealed class ApplyUpdateArgumentsTests
{
    private const string Hash = "669f7ab5dbbae1efbac700f88cdb166bd7395a806d7d6f498073a119f1e6e330";

    [Theory]
    // Spaces and an accent, the case the review asks about.
    [InlineData(@"C:\Programmes FDC\RemoteDeck update\RemoteDeck.exe")]
    [InlineData(@"C:\Users\héloïse\AppData\Local\RemoteDeck\update\0.9.0\RemoteDeck.exe")]
    [InlineData(@"C:\NoSpaces\RemoteDeck.exe")]
    // Backslashes before a quote and at the end follow CommandLineToArgvW's own rules.
    [InlineData(@"C:\odd ""name""\RemoteDeck.exe")]
    [InlineData(@"\\server\share with space\")]
    public void What_Format_writes_Windows_reads_back(string source)
    {
        var args = SplitLikeWindows(ApplyUpdateArguments.Format(source, Hash));

        Assert.Equal([ApplyUpdateArguments.Verb, source, Hash], args);
        Assert.True(ApplyUpdateArguments.TryParse(args, out var parsedSource, out var parsedHash));
        Assert.Equal(source, parsedSource);
        Assert.Equal(Hash, parsedHash);
    }

    [Fact]
    public void An_upper_case_hash_is_read_in_lower_case()
    {
        string[] args = [ApplyUpdateArguments.Verb, @"C:\RemoteDeck.exe", Hash.ToUpperInvariant()];

        Assert.True(ApplyUpdateArguments.TryParse(args, out _, out var sha256));
        Assert.Equal(Hash, sha256);
    }

    public static TheoryData<string[]> Refused => new()
    {
        // No verb, or another one.
        new[] { @"C:\RemoteDeck.exe", Hash },
        new[] { "--apply", @"C:\RemoteDeck.exe", Hash },
        new[] { "--APPLY-UPDATE", @"C:\RemoteDeck.exe", Hash },
        // Missing arguments.
        Array.Empty<string>(),
        new[] { ApplyUpdateArguments.Verb },
        new[] { ApplyUpdateArguments.Verb, @"C:\RemoteDeck.exe" },
        // Not a 64-character hexadecimal hash.
        new[] { ApplyUpdateArguments.Verb, @"C:\RemoteDeck.exe", Hash[1..] },
        new[] { ApplyUpdateArguments.Verb, @"C:\RemoteDeck.exe", Hash + "0" },
        new[] { ApplyUpdateArguments.Verb, @"C:\RemoteDeck.exe", "g" + Hash[1..] },
        new[] { ApplyUpdateArguments.Verb, @"C:\RemoteDeck.exe", "sha256:" + Hash },
        // One argument too many.
        new[] { ApplyUpdateArguments.Verb, @"C:\RemoteDeck.exe", Hash, "extra" },
        // A source that is not a full path: the elevated instance starts in System32.
        new[] { ApplyUpdateArguments.Verb, "", Hash },
        new[] { ApplyUpdateArguments.Verb, "RemoteDeck.exe", Hash },
        new[] { ApplyUpdateArguments.Verb, @"update\RemoteDeck.exe", Hash },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Anything_else_is_refused(string[] args)
    {
        Assert.False(ApplyUpdateArguments.TryParse(args, out var source, out var sha256));
        Assert.Equal("", source);
        Assert.Equal("", sha256);
    }

    /// <summary>Splits a command line exactly as the started process will: with Windows' own parser.
    /// A program name is put in front because the first token follows different rules.</summary>
    private static string[] SplitLikeWindows(string arguments)
    {
        var argv = CommandLineToArgvW("RemoteDeck.exe " + arguments, out var count);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var result = new string[count - 1];
            for (var i = 1; i < count; i++)
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
