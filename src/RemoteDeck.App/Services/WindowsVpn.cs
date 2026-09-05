using System.IO;
using System.Net.NetworkInformation;
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.App.Services;

/// <summary>
/// The Windows VPN, as much of it as RemoteDeck touches: which profiles are up, and a way to ask
/// Windows to raise one.
/// </summary>
/// <remarks>
/// <para>
/// Read through <see cref="NetworkInterface"/> rather than through <c>RasEnumConnections</c>. The
/// RAS API would be the direct answer, but it means declaring <c>RASCONN</c>, whose layout is
/// versioned by a <c>dwSize</c> field — a struct guessed wrong reads garbage or crashes, and this
/// project's rule is not to assume the shape of someone else's type. Windows names the dial-up
/// interface after the phonebook entry, so the managed enumeration answers the same question with
/// nothing to get wrong.
/// </para>
/// <para>
/// RemoteDeck holds no VPN credential. <see cref="DialAsync"/> hands Windows the profile name and
/// the handle RAS itself returns in place of the saved password — sixteen asterisks, exchanged for
/// the real secret inside Windows and never seen here.
/// </para>
/// </remarks>
internal static class WindowsVpn
{
    /// <summary>
    /// The names under which a VPN tunnel that is currently up can be recognised — the interface's
    /// name and its description, since which of the two carries the profile name has never been
    /// promised anywhere. Both are matched case-insensitively by <c>VpnRequirement</c>.
    /// </summary>
    /// <returns>Never null. An empty set means nothing is up, which is a real answer; a failure to
    /// enumerate throws rather than pretending the tunnel is down.</returns>
    public static IReadOnlySet<string> ConnectedProfiles()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            // Ppp covers L2TP, PPTP and SSTP; Tunnel covers IKEv2 and the rest. Everything else is
            // an ordinary adapter and cannot be a VPN profile.
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel))
            {
                continue;
            }

            names.Add(nic.Name);
            names.Add(nic.Description);
        }

        ProbeLog.Write("vpn", names.Count == 0
            ? "no VPN interface is up"
            : $"VPN interfaces up: {string.Join(", ", names.Order(StringComparer.OrdinalIgnoreCase))}");

        return names;
    }

    /// <summary>
    /// Every VPN profile worth offering in the editor: the ones defined in the RAS phonebooks,
    /// whether up or not, plus any tunnel that is up.
    /// </summary>
    /// <remarks>
    /// The phonebook is an INI file whose section names are the entry names, which is the only part
    /// of it this reads. Its location is not something this project has verified on a machine that
    /// has one, so nothing depends on finding it: the editor's field stays typeable, and a phonebook
    /// that is missing, moved or unreadable simply means the list is shorter. That is why the
    /// currently-up tunnels are unioned in — on the machine where it matters, at least the profile
    /// in use will be offered.
    /// </remarks>
    public static IReadOnlyList<string> KnownProfiles()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var phonebook in Phonebooks())
        {
            try
            {
                if (!File.Exists(phonebook))
                {
                    continue;
                }

                foreach (var line in File.ReadLines(phonebook))
                {
                    var text = line.Trim();
                    if (text.Length > 2 && text[0] == '[' && text[^1] == ']')
                    {
                        names.Add(text[1..^1]);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                ProbeLog.Write("vpn", $"Could not read '{phonebook}': {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var up in ConnectedProfiles())
        {
            names.Add(up);
        }

        return [.. names.Order(StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Where Windows keeps the RAS phone books: the user's, then the machine's. Also the paths
    /// <see cref="RasApi"/> dials through — RAS's own default answers 621 on the reference client,
    /// so an explicit path is not a nicety here.
    /// </summary>
    internal static IEnumerable<string> Phonebooks()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk");
    }

    /// <summary>
    /// Asks Windows to raise <paramref name="profile"/>, with the credential the user already saved
    /// in it, and without a window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever from a button the user pressed. RemoteDeck never raises a tunnel on its own: a
    /// connection attempt is not consent to change the machine's network state, and a VPN that goes
    /// up by itself is a VPN nobody knows is up.
    /// </para>
    /// <para>
    /// This used to run <c>rasdial "&lt;profile&gt;"</c>. That fails with RAS 628 on the reference
    /// profile, and the reason is documented rather than mysterious: <c>RASDIALPARAMS</c> says an
    /// empty user name and password make RAS dial as the current Windows logon context, so
    /// <c>rasdial</c> with no argument offers the wrong account to the VPN server, which drops the
    /// call. The network flyout does not, because it dials with the profile's own saved credential.
    /// <see cref="RasApi"/> now does the same, and RemoteDeck still stores no VPN secret: what it
    /// passes on is the sixteen-asterisk handle RAS hands back in place of the password.
    /// </para>
    /// <para>
    /// The dial runs on a background thread — a synchronous <c>RasDial</c> blocks until the tunnel is
    /// up or refused — and the wait is capped. Past the cap the attempt is not cancelled: it carries
    /// on inside Windows, and the shell says so instead of pretending it failed.
    /// </para>
    /// </remarks>
    public static async Task<VpnDialResult> DialAsync(string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var entry = profile.Trim();
        var dial = Task.Run(() =>
        {
            try
            {
                return new VpnDialer(new RasApi()).Dial(entry);
            }
            catch (Exception ex)
            {
                // A missing rasapi32, a refused entry point: worth reporting as itself rather than
                // letting it tear down the shell from a background thread.
                ProbeLog.Write("vpn", $"RasDial \"{entry}\" threw: {ex.GetType().Name}: {ex.Message}");
                return new VpnDialResult(VpnDialOutcome.Failed, RasError.Success, ex.Message);
            }
        });

        if (await Task.WhenAny(dial, Task.Delay(DialWait)).ConfigureAwait(true) != dial)
        {
            ProbeLog.Write("vpn", $"RasDial \"{entry}\": still going after {DialWait.TotalSeconds:0} s, left to Windows");
            return new VpnDialResult(VpnDialOutcome.StillDialing, RasError.Success, string.Empty);
        }

        var result = await dial.ConfigureAwait(true);
        ProbeLog.Write("vpn", $"Dial \"{entry}\": {result.Outcome}"
            + (result.Code == RasError.Success ? string.Empty : $" ({result.Code}: {result.Detail})"));

        return result;
    }

    /// <summary>
    /// How long the shell waits for a tunnel before saying it is still coming up. Ours, not
    /// Windows's: a synchronous <c>RasDial</c> takes no timeout, and an L2TP handshake that is going
    /// to fail can take most of a minute to say so.
    /// </summary>
    private static readonly TimeSpan DialWait = TimeSpan.FromSeconds(60);
}
