using System.Globalization;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Import;

/// <summary>
/// Reads Remote Desktop Connection <c>.rdp</c> files into <see cref="ImportCandidate"/>s. Pure: the
/// caller supplies the lines, so nothing here touches the disk.
///
/// Only the keys documented by Microsoft are mapped, and they are mapped exactly as documented; every
/// other key is counted and dropped, never guessed. The <c>password 51:b:</c> entry is discarded without
/// being read, and never appears in a warning or anywhere else.
/// See <see href="https://learn.microsoft.com/azure/virtual-desktop/rdp-properties"/> and
/// <see href="https://learn.microsoft.com/troubleshoot/windows-server/remote/remote-desktop-protocol-settings"/>.
/// </summary>
public static class RdpFileImporter
{
    private const int DefaultPort = 3389;
    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const int MinDimension = 200;
    private const int MaxDimension = 8192;

    /// <summary>Resolutions of <c>desktop size id</c>, in the documented order 0 to 4.</summary>
    private static readonly (int Width, int Height)[] DesktopSizes =
        [(640, 480), (800, 600), (1024, 768), (1280, 1024), (1600, 1200)];

    /// <summary>
    /// Parses one <c>.rdp</c> file. Returns null when it carries no usable <c>full address</c>, since a
    /// candidate without a host is not importable. <paramref name="fileName"/> gives both the proposed
    /// name (without extension) and <see cref="ImportCandidate.Source"/>.
    ///
    /// Blank lines and comments (<c>;</c> or <c>#</c>) are skipped silently — they are not entries.
    /// Malformed lines (fewer than three colon-separated segments), unknown keys and documented keys
    /// carrying an undocumented value are counted into a single warning, for example
    /// <c>4 unsupported entries ignored</c>.
    /// </summary>
    public static ImportCandidate? Parse(string fileName, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(lines);

        var warnings = new List<string>();
        var unsupported = 0;

        string? host = null;
        var port = DefaultPort;
        string? userName = null;
        string? domain = null;
        int? width = null;
        int? height = null;
        int? sizeId = null;
        int? dynamicResolution = null;
        int? authenticationLevel = null;
        var redirectClipboard = true;
        var redirectPrinters = false;
        var redirectDrives = false;
        var redirectAudio = false;
        var useWebAccount = false;

        foreach (var raw in lines)
        {
            var line = raw is null ? "" : raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

            // key:type:value — the value itself may contain colons (host:port), so split into three.
            var parts = line.Split(':', 3);
            if (parts.Length < 3) { unsupported++; continue; }

            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts[2].Trim();

            // The DPAPI blob of another user profile: dropped unread, never counted, never reported.
            if (key == "password 51") continue;

            switch (key)
            {
                case "full address":
                    var address = ParseAddress(value, ref port, warnings);
                    if (address.Length > 0) host = address;
                    break;
                case "username":
                    userName = value.Length == 0 ? null : value;
                    break;
                case "domain":
                    domain = value.Length == 0 ? null : value;
                    break;
                case "desktopwidth":
                    if (TryInt(value, out var w) && w is >= MinDimension and <= MaxDimension) width = w;
                    else unsupported++;
                    break;
                case "desktopheight":
                    if (TryInt(value, out var h) && h is >= MinDimension and <= MaxDimension) height = h;
                    else unsupported++;
                    break;
                case "desktop size id":
                    if (TryInt(value, out var id) && id >= 0 && id < DesktopSizes.Length) sizeId = id;
                    else unsupported++;
                    break;
                case "screen mode id":
                    // 1 windowed, 2 full screen. A window preference is not a resolution: noted, not mapped.
                    if (TryInt(value, out var screenMode) && screenMode is 1 or 2)
                    {
                        if (screenMode == 2)
                            warnings.Add("Full screen (screen mode id:i:2) is a window preference, not a resolution: not imported.");
                    }
                    else unsupported++;
                    break;
                case "dynamic resolution":
                    if (TryInt(value, out var dynamicMode) && dynamicMode is 0 or 1) dynamicResolution = dynamicMode;
                    else unsupported++;
                    break;
                case "audiomode":
                    // 0 play here, 1 play on the remote machine, 2 do not play.
                    if (TryInt(value, out var audio) && audio is 0 or 1 or 2) redirectAudio = audio == 0;
                    else unsupported++;
                    break;
                case "redirectclipboard":
                    if (TryBool(value, out var clipboard)) redirectClipboard = clipboard;
                    else unsupported++;
                    break;
                case "redirectprinters":
                    if (TryBool(value, out var printers)) redirectPrinters = printers;
                    else unsupported++;
                    break;
                case "drivestoredirect":
                    // Empty means no drive; "*" or a list means at least one.
                    redirectDrives = value.Length > 0;
                    break;
                case "authentication level":
                    // 0 connect without warning, 1 do not connect, 2 warn, 3 unspecified.
                    if (TryInt(value, out var level) && level is 0 or 1 or 2 or 3)
                        authenticationLevel = level == 3 ? null : level;
                    else unsupported++;
                    break;
                case "enablerdsaadauth":
                    if (TryBool(value, out var webAccount)) useWebAccount = webAccount;
                    else unsupported++;
                    break;
                default:
                    unsupported++;
                    break;
            }
        }

        if (host is null) return null;

        var displayMode = DisplayMode.Dynamic;
        int? fixedWidth = null;
        int? fixedHeight = null;
        if (dynamicResolution != 1)
        {
            if (width is not null && height is not null)
            {
                displayMode = DisplayMode.Scaled;
                fixedWidth = width;
                fixedHeight = height;
            }
            else if (sizeId is not null)
            {
                displayMode = DisplayMode.Scaled;
                fixedWidth = DesktopSizes[sizeId.Value].Width;
                fixedHeight = DesktopSizes[sizeId.Value].Height;
            }
        }

        if (unsupported > 0)
            warnings.Add(unsupported == 1 ? "1 unsupported entry ignored" : $"{unsupported} unsupported entries ignored");

        var name = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(name)) name = host;

        return new ImportCandidate
        {
            Name = name,
            Host = host,
            Port = port,
            UserName = userName,
            Domain = domain,
            DisplayMode = displayMode,
            FixedWidth = fixedWidth,
            FixedHeight = fixedHeight,
            RedirectClipboard = redirectClipboard,
            RedirectDrives = redirectDrives,
            RedirectPrinters = redirectPrinters,
            RedirectAudio = redirectAudio,
            UseWebAccount = useWebAccount,
            AuthenticationLevel = authenticationLevel,
            Source = fileName,
            Warnings = warnings.AsReadOnly(),
        };
    }

    /// <summary>
    /// Parses the <paramref name="files"/> the caller listed, resolving relative names against
    /// <paramref name="folder"/>, and keeps the candidates that carry a host. A file whose
    /// <paramref name="readLines"/> call fails (locked, unreadable, gone) is skipped instead of breaking
    /// the batch; a caller that wants to count those failures counts them in its own delegate, the only
    /// place that knows what went wrong.
    /// </summary>
    public static IReadOnlyList<ImportCandidate> ParseFolder(
        string folder, Func<string, IEnumerable<string>> readLines, IEnumerable<string> files)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(readLines);
        ArgumentNullException.ThrowIfNull(files);

        var candidates = new List<ImportCandidate>();
        foreach (var file in files)
        {
            var path = Path.IsPathRooted(file) ? file : Path.Combine(folder, file);
            try
            {
                var candidate = Parse(path, readLines(path));
                if (candidate is not null) candidates.Add(candidate);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                        or System.Security.SecurityException or NotSupportedException)
            {
                // Unreadable file: skipped, so one bad file cannot cost the user the whole folder.
            }
        }

        return candidates.AsReadOnly();
    }

    /// <summary>
    /// Splits <c>host</c> or <c>host:port</c>. A value holding several colons is an IPv6 literal and is
    /// kept whole; a single colon followed by anything but a port 1-65535 warns and keeps 3389.
    /// </summary>
    private static string ParseAddress(string value, ref int port, List<string> warnings)
    {
        var at = value.IndexOf(':', StringComparison.Ordinal);
        if (at < 0 || value.IndexOf(':', at + 1) >= 0) return value;

        var hostPart = value[..at];
        var portPart = value[(at + 1)..];
        if (TryInt(portPart, out var parsed) && parsed is >= MinPort and <= MaxPort)
        {
            port = parsed;
            return hostPart;
        }

        warnings.Add($"\"{portPart}\" is not a usable port; {DefaultPort} was used instead.");
        return hostPart;
    }

    private static bool TryInt(string value, out int result)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);

    /// <summary>The documented boolean form of an <c>:i:</c> entry: exactly <c>0</c> or <c>1</c>.</summary>
    private static bool TryBool(string value, out bool result)
    {
        result = value == "1";
        return value is "0" or "1";
    }
}
