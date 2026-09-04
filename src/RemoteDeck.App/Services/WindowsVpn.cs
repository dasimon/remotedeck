using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

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
/// RemoteDeck holds no VPN credential. <see cref="Dial"/> hands the profile name to Windows, which
/// uses whatever the user stored in the profile itself.
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

    private static IEnumerable<string> Phonebooks()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk");
    }

    /// <summary>
    /// Asks Windows to dial <paramref name="profile"/>, through <c>rasdial</c>.
    /// </summary>
    /// <remarks>
    /// Only ever from a button the user pressed. RemoteDeck never raises a tunnel on its own: a
    /// connection attempt is not consent to change the machine's network state, and a VPN that goes
    /// up by itself is a VPN nobody knows is up.
    /// <para>
    /// The window is shown rather than hidden. <c>rasdial</c> is where Windows prompts when the
    /// profile has no stored credential, and swallowing that console would turn a prompt into a
    /// hang.
    /// </para>
    /// </remarks>
    /// <returns>
    /// What <c>rasdial</c> said. <c>null</c> when it succeeded; otherwise the message to show the
    /// user — its own words where it produced any, and its exit code where it did not.
    /// </returns>
    /// <remarks>
    /// The output is captured rather than left to a console window. The first version showed the
    /// window, on the reasoning that <c>rasdial</c> is where Windows would prompt for a missing
    /// password — but a console that closes the instant the command ends takes any error message
    /// with it, which is exactly what happened: a black flash and no connection, with nothing to
    /// read. An error nobody can see is worse than no attempt at all.
    /// </remarks>
    public static string? Dial(string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var entry = profile.Trim();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("rasdial", $"\"{entry}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                ProbeLog.Write("vpn", $"rasdial \"{entry}\": the process did not start");
                return "rasdial did not start.";
            }

            // Standard input is deliberately NOT redirected: rasdial prompts on it when the profile
            // has no stored credential, and a redirected, never-written stdin would hang the wait
            // below instead of failing.
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(TimeSpan.FromSeconds(45)))
            {
                ProbeLog.Write("vpn", $"rasdial \"{entry}\": still running after 45 s, left alone");
                return "rasdial is still running. Watch the Windows VPN indicator, then connect again.";
            }

            var said = string.Join(" ", (output + " " + error)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Length > 0));

            ProbeLog.Write("vpn", $"rasdial \"{entry}\" exited {process.ExitCode}: {said}");

            return process.ExitCode == 0
                ? null
                : said.Length > 0 ? said : $"rasdial returned {process.ExitCode}.";
        }
        catch (Exception ex)
        {
            ProbeLog.Write("vpn", $"rasdial \"{entry}\" failed to start: {ex.GetType().Name} {ex.Message}");
            return ex.Message;
        }
    }
}
