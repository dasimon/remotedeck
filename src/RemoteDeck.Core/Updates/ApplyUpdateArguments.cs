using System.Text;

namespace RemoteDeck.Core.Updates;

/// <summary>
/// The command line of the elevated instance that only copies an already verified executable into a
/// folder the user cannot write to: <c>--apply-update "&lt;source&gt;" &lt;sha256&gt;</c>, nothing else.
/// The elevated instance re-hashes the source itself, so the hash on the command line is what it
/// compares with, not a proof.
/// </summary>
public static class ApplyUpdateArguments
{
    public const string Verb = "--apply-update";

    private const int HashLength = 64;

    /// <summary>A line for <c>ProcessStartInfo.Arguments</c> (or a log), quoted so that Windows'
    /// <c>CommandLineToArgvW</c> gives back exactly the verb, <paramref name="source"/> and
    /// <paramref name="sha256"/>.</summary>
    public static string Format(string source, string sha256)
    {
        var line = new StringBuilder();
        AppendArgument(line, Verb);
        line.Append(' ');
        AppendArgument(line, source);
        line.Append(' ');
        AppendArgument(line, sha256);
        return line.ToString();
    }

    /// <summary>Reads exactly the verb, a fully qualified source path and a 64-character hexadecimal
    /// hash (returned in lower case). Anything missing, extra or malformed is refused, and both outputs
    /// are then empty.</summary>
    public static bool TryParse(string[] args, out string source, out string sha256)
    {
        source = "";
        sha256 = "";
        if (args is not [Verb, var path, var hash]) return false;
        // The elevated instance starts in System32: a relative path would point somewhere else.
        if (!Path.IsPathFullyQualified(path)) return false;
        if (hash.Length != HashLength) return false;
        foreach (var c in hash)
            if (!char.IsAsciiHexDigit(c)) return false;

        source = path;
        sha256 = hash.ToLowerInvariant();
        return true;
    }

    /// <summary>The quoting rules of <c>CommandLineToArgvW</c>: inside quotes, backslashes are literal
    /// unless they precede a quote, where 2n backslashes give n and 2n+1 give n and a literal quote.</summary>
    private static void AppendArgument(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes before a quote are doubled, and the quote itself is escaped.
            line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            line.Append(c);
            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled so that it stays a closing quote.
        line.Append('\\', backslashes * 2);
        line.Append('"');
    }
}
