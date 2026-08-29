namespace RemoteDeck.Core.Rdp;

/// <summary>One registered flavour of the Remote Desktop ActiveX control (mstscax.dll).</summary>
/// <param name="Clsid">CLSID of the nonscriptable coclass. Its number trails the registry label by one: label "13" is <c>MsRdpClient12NotSafeForScripting</c>.</param>
/// <param name="Label">Registry label suffix ("Microsoft RDP Client Control - version {Label}").</param>
public sealed record RdpControlVersion(Guid Clsid, string Label);

/// <summary>
/// Ordered list of known control CLSIDs, newest first, and selection of the first one
/// registered on the host. CLSIDs verified against the local registry and
/// https://learn.microsoft.com/windows/win32/termserv/using-remote-desktop-web-connection
/// </summary>
public static class RdpControlCatalog
{
    public static IReadOnlyList<RdpControlVersion> Candidates { get; } =
    [
        new(new Guid("3F859AA3-C2D4-4FAA-B0E4-FD0C9C4E5E3A"), "13"), // Windows 11 / Server 2022+
        new(new Guid("1DF7C823-B2D4-4B54-975A-F2AC5D7CF8B8"), "12"),
        new(new Guid("A0C63C30-F08D-4AB4-907C-34905D770C7D"), "11"),
        new(new Guid("8B918B82-7985-4C24-89DF-C33AD2BBFBCD"), "10"), // Windows 8.1 / Server 2012 R2
    ];

    /// <summary>Returns the newest candidate for which <paramref name="isRegistered"/> is true, or null.</summary>
    public static RdpControlVersion? Select(Func<Guid, bool> isRegistered)
    {
        ArgumentNullException.ThrowIfNull(isRegistered);
        foreach (var candidate in Candidates)
        {
            if (isRegistered(candidate.Clsid))
            {
                return candidate;
            }
        }
        return null;
    }
}
