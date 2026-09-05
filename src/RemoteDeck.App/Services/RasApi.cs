using System.Runtime.InteropServices;
using System.Text;
using RemoteDeck.Core.Sessions;

namespace RemoteDeck.App.Services;

/// <summary>
/// The RAS calls behind <see cref="VpnDialer"/>: read what the user saved in a VPN profile, and dial
/// with it. Nothing here decides anything — the sequence lives in <c>Core</c>, where a test can hold
/// it; this is the part that can only be tried by hand, on a machine with a real profile.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No versioned RAS structure is declared.</strong> <c>RASDIALPARAMSW</c> grows with the
/// Windows version — <c>dwSubEntry</c>, then <c>dwIfIndex</c>, then <c>szEncPassword</c> — and a
/// struct guessed wrong reads garbage or crashes, which is the same reason
/// <see cref="WindowsVpn.ConnectedProfiles"/> refused <c>RASCONN</c>. So this writes a flat buffer at
/// constant offsets instead, and those offsets are not assumed either: they were confirmed on
/// 2026-09-05 by reading the reference profile's user name and password handle back out of one.
/// </para>
/// <para>
/// <strong>RemoteDeck still holds no VPN secret.</strong> <c>RasGetCredentials</c> and
/// <c>RasGetEntryDialParams</c> never return a password: they return a handle to the saved one —
/// sixteen asterisks — and <c>RasDial</c> exchanges that handle for the real secret inside Windows.
/// Sixteen asterisks is all that ever passes through this file.
/// </para>
/// <para>
/// <strong><c>RasHangUp</c> is deliberately never called.</strong> The documentation asks for it on
/// any non-null connection handle, and obeying it here would hang up the tunnel just raised.
/// <c>rasdial.exe</c> sets the precedent: it dials, returns, and leaves the connection standing.
/// </para>
/// </remarks>
internal sealed class RasApi : IRasGateway
{
    // RASDIALPARAMSW, x64 and Unicode, from ras.h (RAS_MaxEntryName 256, RAS_MaxPhoneNumber 128) and
    // lmcons.h (UNLEN 256, PWLEN 256, DNLEN 15). Every field is a WCHAR array plus its terminator.
    private const int OffsetEntryName = 4;                                  // dwSize
    private const int OffsetPhoneNumber = OffsetEntryName + (257 * 2);      // 518
    private const int OffsetCallbackNumber = OffsetPhoneNumber + (129 * 2); // 776
    private const int OffsetUserName = OffsetCallbackNumber + (129 * 2);    // 1034
    private const int OffsetPassword = OffsetUserName + (257 * 2);          // 1548
    private const int OffsetDomain = OffsetPassword + (257 * 2);            // 2062

    /// <summary>
    /// The <c>dwSize</c> values <c>rasapi32</c> accepts for <c>RASDIALPARAMSW</c>, most recent first.
    /// 2120 is the Windows 7 layout (through <c>dwIfIndex</c>, padded to its 8-byte alignment); 2112
    /// is the one before it. Measured on 2026-09-05: those two are accepted and 2100, 2116, 2124 and
    /// 2128 are all refused with <c>ERROR_INVALID_SIZE</c> — so the Windows 8 <c>szEncPassword</c>
    /// layout is not what this runtime wants, and guessing "the newest must be right" would have
    /// been wrong.
    /// </summary>
    private static readonly int[] DialParamsSizes = [2120, 2112];

    // RASCREDENTIALSW: no conditional members, one size, nothing to negotiate.
    private const int CredentialsSize = 1068;
    private const int OffsetCredentialsMask = 4;
    private const int OffsetCredentialsUserName = 8;
    private const int OffsetCredentialsPassword = OffsetCredentialsUserName + (257 * 2); // 522
    private const int OffsetCredentialsDomain = OffsetCredentialsPassword + (257 * 2);   // 1036

    /// <summary>
    /// <c>RASCM_UserName | RASCM_Password | RASCM_Domain</c>. <c>RASCM_DefaultCreds</c> is
    /// deliberately absent: asking for it on a per-user entry makes RAS return success with
    /// <em>everything empty</em> — measured on the reference profile, where mask 0x7 returns the user
    /// name and the handle and mask 0xF returns nothing at all.
    /// </summary>
    private const int CredentialsMask = 0x1 | 0x2 | 0x4;

    /// <summary>The size that worked, once one has. Zero until the first call settles it.</summary>
    private static int _knownDialParamsSize;

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RasGetEntryDialParamsW")]
    private static extern uint RasGetEntryDialParams(string? phonebook, IntPtr dialParams, out int passwordSaved);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RasGetCredentialsW")]
    private static extern uint RasGetCredentials(string? phonebook, string entry, IntPtr credentials);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RasDialW")]
    private static extern uint RasDial(
        IntPtr extensions, string? phonebook, IntPtr dialParams, uint notifierType, IntPtr notifier, out IntPtr connection);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RasGetErrorStringW")]
    private static extern uint RasGetErrorString(uint error, StringBuilder message, int size);

    /// <inheritdoc />
    /// <remarks>
    /// The explicit paths first and RAS's own default last, which is the opposite of what the
    /// documentation's "if this parameter is NULL, the function uses the current default phone book"
    /// suggests: on the reference client that default answers 621, cannot open the phone book, for a
    /// profile the explicit user path resolves immediately.
    /// </remarks>
    public IReadOnlyList<string?> Phonebooks { get; } = [.. WindowsVpn.Phonebooks(), null];

    /// <inheritdoc />
    public RasRead ReadEntry(string? phonebook, string entry)
    {
        return WithDialParams(entry, buffer =>
        {
            var code = RasGetEntryDialParams(phonebook, buffer, out _);
            return code == RasError.Success
                ? new RasRead(code, ReadCredential(buffer, OffsetUserName, OffsetPassword, OffsetDomain))
                : new RasRead(code, null);
        });
    }

    /// <inheritdoc />
    public RasRead ReadCredentials(string? phonebook, string entry)
    {
        var buffer = Marshal.AllocHGlobal(CredentialsSize);
        try
        {
            Zero(buffer, CredentialsSize);
            Marshal.WriteInt32(buffer, 0, CredentialsSize);
            Marshal.WriteInt32(buffer, OffsetCredentialsMask, CredentialsMask);

            var code = RasGetCredentials(phonebook, entry, buffer);

            // The returned dwMask is not consulted. On the reference client it echoes the request —
            // it claims Password even when the field comes back empty — so the string is the only
            // honest answer to "is there a saved password".
            return code == RasError.Success
                ? new RasRead(code, ReadCredential(
                    buffer, OffsetCredentialsUserName, OffsetCredentialsPassword, OffsetCredentialsDomain))
                : new RasRead(code, null);
        }
        finally
        {
            Zero(buffer, CredentialsSize);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <inheritdoc />
    public uint Dial(string? phonebook, string entry, RasCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var read = WithDialParams(entry, buffer =>
        {
            Write(buffer, OffsetUserName, credential.UserName, 256);
            Write(buffer, OffsetPassword, credential.PasswordHandle, 256);
            Write(buffer, OffsetDomain, credential.Domain, 15);

            // No extensions, no notifier: with a null notifier RasDial is synchronous, so when it
            // returns the tunnel is either up or it is not. There is no window and no message pump.
            var code = RasDial(IntPtr.Zero, phonebook, buffer, 0, IntPtr.Zero, out var connection);

            ProbeLog.Write("vpn", $"RasDial \"{entry}\" returned {code}, handle {(connection == IntPtr.Zero ? "none" : "held")}"
                + " (never hung up on purpose: that would drop the tunnel just raised)");

            return new RasRead(code, null);
        });

        return read.Code;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> ConnectedProfiles() => WindowsVpn.ConnectedProfiles();

    /// <inheritdoc />
    public string Describe(uint code)
    {
        var message = new StringBuilder(512);
        if (RasGetErrorString(code, message, message.Capacity) == RasError.Success && message.Length > 0)
        {
            return message.ToString().Trim();
        }

        // Not every code RAS can return is a RAS code; the rest are ordinary Win32 ones.
        return new System.ComponentModel.Win32Exception((int)code).Message;
    }

    /// <summary>
    /// Runs <paramref name="call"/> against a zeroed <c>RASDIALPARAMSW</c> holding
    /// <paramref name="entry"/>, trying each accepted <c>dwSize</c> until one is not refused.
    /// </summary>
    private static RasRead WithDialParams(string entry, Func<IntPtr, RasRead> call)
    {
        int[] sizes = _knownDialParamsSize != 0 ? [_knownDialParamsSize] : DialParamsSizes;
        var result = new RasRead(RasError.InvalidSize, null);

        foreach (var size in sizes)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Zero(buffer, size);
                Marshal.WriteInt32(buffer, 0, size);
                Write(buffer, OffsetEntryName, entry, 256);

                result = call(buffer);
            }
            finally
            {
                Zero(buffer, size);
                Marshal.FreeHGlobal(buffer);
            }

            if (result.Code != RasError.InvalidSize)
            {
                if (_knownDialParamsSize != size)
                {
                    ProbeLog.Write("vpn", $"RASDIALPARAMS dwSize {size} accepted");
                    _knownDialParamsSize = size;
                }

                return result;
            }

            ProbeLog.Write("vpn", $"RASDIALPARAMS dwSize {size} refused with 632, trying the next");
        }

        return result;
    }

    private static RasCredential? ReadCredential(IntPtr buffer, int user, int password, int domain)
    {
        var credential = new RasCredential(
            Marshal.PtrToStringUni(buffer + user) ?? string.Empty,
            Marshal.PtrToStringUni(buffer + password) ?? string.Empty,
            Marshal.PtrToStringUni(buffer + domain) ?? string.Empty);

        return credential.IsUsable ? credential : null;
    }

    /// <summary>Writes a null-terminated string, truncated to what the field can hold.</summary>
    private static void Write(IntPtr buffer, int offset, string value, int maxCharacters)
    {
        if (value.Length == 0)
        {
            return;
        }

        var text = value.Length > maxCharacters ? value[..maxCharacters] : value;
        var bytes = Encoding.Unicode.GetBytes(text);
        Marshal.Copy(bytes, 0, buffer + offset, bytes.Length);
    }

    /// <summary>
    /// Wipes the buffer, before use so every field it does not fill reads as an empty string, and
    /// after use because it held a credential — even one that is only ever sixteen asterisks.
    /// </summary>
    private static void Zero(IntPtr buffer, int size) => Marshal.Copy(new byte[size], 0, buffer, size);
}
