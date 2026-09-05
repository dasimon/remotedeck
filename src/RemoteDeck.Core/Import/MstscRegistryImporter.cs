namespace RemoteDeck.Core.Import;

/// <summary>
/// Turns the servers Remote Desktop Connection remembers into <see cref="ImportCandidate"/>s. Pure: the
/// entries are read by the caller — the registry access itself lives in the app layer, under
/// <c>HKCU\Software\Microsoft\Terminal Server Client\Servers</c> (one subkey per host, optional
/// <c>UsernameHint</c> value).
/// See <see href="https://learn.microsoft.com/troubleshoot/windows-server/remote/remote-desktop-protocol-settings"/>.
/// </summary>
public static class MstscRegistryImporter
{
    private const string SourceName = "mstsc registry";

    /// <summary>
    /// One candidate per host, in the order given, keeping the first of any hosts that differ only by
    /// case. Blank hosts are dropped. The registry holds no port and no password, so the port stays the
    /// default and no credential is ever proposed.
    /// </summary>
    public static IReadOnlyList<ImportCandidate> FromServers(IEnumerable<(string Host, string? UserName)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ImportCandidate>();

        foreach (var (rawHost, rawUserName) in entries)
        {
            var host = rawHost is null ? "" : rawHost.Trim();
            if (host.Length == 0 || !seen.Add(host)) continue;

            var userName = rawUserName is null ? "" : rawUserName.Trim();
            candidates.Add(new ImportCandidate
            {
                Name = host,
                Host = host,
                UserName = userName.Length == 0 ? null : userName,
                Source = SourceName,
            });
        }

        return candidates.AsReadOnly();
    }

    /// <summary>
    /// Fills the user name of every web-account candidate that has none with the <c>UsernameHint</c>
    /// Remote Desktop Connection remembers for the same host (compared without case). The
    /// <c>.rdp</c> mstsc exports carries no <c>username</c> line for such a connection; the UPN lives
    /// in that hint, and it is what the control needs to find the account without a prompt. Other
    /// candidates are returned untouched: their hint would be a credential's name.
    /// </summary>
    public static IReadOnlyList<ImportCandidate> WithUserNameHints(
        IEnumerable<ImportCandidate> candidates, IEnumerable<(string Host, string? UserName)> entries)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(entries);

        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, userName) in entries)
        {
            if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(userName))
                hints.TryAdd(host.Trim(), userName.Trim());
        }

        return candidates
            .Select(c => c.UseWebAccount && c.UserName is null && hints.TryGetValue(c.Host, out var hint)
                ? c with { UserName = hint }
                : c)
            .ToList()
            .AsReadOnly();
    }
}
