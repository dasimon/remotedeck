using System.Globalization;

namespace RemoteDeck.Core.Model;

/// <summary>
/// Duplicating a connection: a new, unsaved connection with the settings of an existing one, and a
/// name that does not collide with any other.
/// </summary>
/// <remarks>
/// Pure, and in <c>Core</c>: what can go wrong is a field forgotten on the way, and a test that walks
/// every property of <see cref="Connection"/> is what keeps a column added later from being dropped
/// from copies without anyone noticing.
/// </remarks>
public static class ConnectionCopy
{
    /// <summary>
    /// A new connection with <paramref name="source"/>'s settings, named <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// Not carried: the id (it is a new row), the favourite star (it marks one machine, not a set of
    /// settings), the last connection time (it has never been connected) and the creation time (the
    /// insert sets it). The credential is carried as a reference — the copy points at the same saved
    /// credential, and no secret is duplicated.
    /// </remarks>
    public static Connection Of(Connection source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);

        return new Connection
        {
            Name = name,
            Host = source.Host,
            Port = source.Port,
            GroupName = source.GroupName,
            CredentialId = source.CredentialId,
            DisplayMode = source.DisplayMode,
            FixedWidth = source.FixedWidth,
            FixedHeight = source.FixedHeight,
            RedirectClipboard = source.RedirectClipboard,
            RedirectDrives = source.RedirectDrives,
            RedirectPrinters = source.RedirectPrinters,
            RedirectAudio = source.RedirectAudio,
            AdminSession = source.AdminSession,
            UseWebAccount = source.UseWebAccount,
            WebAccountUpn = source.WebAccountUpn,
            AuthenticationLevel = source.AuthenticationLevel,
            VpnProfile = source.VpnProfile,
            AutoRaiseVpn = source.AutoRaiseVpn,
            Notes = source.Notes,
        };
    }

    /// <summary>
    /// The name a copy of <paramref name="sourceName"/> is offered under: the first format while it is
    /// free, then the numbered one from 2 up. Always within <see cref="ConnectionRules.MaxNameLength"/>.
    /// </summary>
    /// <param name="takenNames">Every connection name in use. Compared ignoring case, as a user reads
    /// them: "WIN02 (copy)" and "win02 (COPY)" are the same name to anyone looking at the list.</param>
    /// <param name="firstFormat">The first copy's name, <c>{0}</c> being the source's — localised by the caller.</param>
    /// <param name="nthFormat">Later copies, <c>{0}</c> the source's name and <c>{1}</c> the number.</param>
    public static string NameFor(string sourceName, IEnumerable<string> takenNames, string firstFormat, string nthFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(takenNames);
        ArgumentNullException.ThrowIfNull(firstFormat);
        ArgumentNullException.ThrowIfNull(nthFormat);

        var taken = new HashSet<string>(takenNames, StringComparer.CurrentCultureIgnoreCase);
        var baseName = sourceName.Trim();

        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? Fit(firstFormat, baseName, n) : Fit(nthFormat, baseName, n);
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Formats the name, shortening the source's part — never the suffix — until it fits.</summary>
    private static string Fit(string format, string baseName, int n)
    {
        var name = string.Format(CultureInfo.CurrentCulture, format, baseName, n);
        var overflow = name.Length - ConnectionRules.MaxNameLength;
        if (overflow <= 0)
        {
            return name;
        }

        var shortened = baseName[..Math.Max(0, baseName.Length - overflow)].TrimEnd();
        return string.Format(CultureInfo.CurrentCulture, format, shortened, n);
    }
}
