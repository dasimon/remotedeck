namespace RemoteDeck.Core.Updates;

/// <summary>Reads a <c>SHA256SUMS</c> listing, the format of <c>sha256sum</c>: one
/// "&lt;64 hex characters&gt; *&lt;file name&gt;" line per file (a space instead of the '*' in text mode).</summary>
public static class Sha256Sums
{
    private const int HashLength = 64;

    /// <summary>The hash listed for exactly <paramref name="fileName"/>, in lower case, or null when
    /// the file is not listed or its line is not a well-formed 64-character hexadecimal hash.</summary>
    public static string? Find(string content, string fileName)
    {
        foreach (var line in content.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            // hash, one space, the text/binary marker, then the name.
            if (text.Length != HashLength + 2 + fileName.Length) continue;
            if (text[HashLength] != ' ' || text[HashLength + 1] is not (' ' or '*')) continue;
            if (!text.AsSpan(HashLength + 2).SequenceEqual(fileName)) continue;

            var hash = text.AsSpan(0, HashLength);
            foreach (var c in hash)
                if (!char.IsAsciiHexDigit(c)) return null;
            return hash.ToString().ToLowerInvariant();
        }

        return null;
    }
}
