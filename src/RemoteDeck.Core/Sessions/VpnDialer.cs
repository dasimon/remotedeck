namespace RemoteDeck.Core.Sessions;

/// <summary>The RAS error codes this sequence has to tell apart. Everything else is just a code.</summary>
public static class RasError
{
    /// <summary><c>ERROR_SUCCESS</c>.</summary>
    public const uint Success = 0;

    /// <summary>
    /// <c>ERROR_CANNOT_OPEN_PHONEBOOK</c> (621). Not fatal: it is what RAS's own default phone book
    /// answers on the reference client, which is why an explicit path is tried first.
    /// </summary>
    public const uint CannotOpenPhonebook = 621;

    /// <summary><c>ERROR_CANNOT_FIND_PHONEBOOK_ENTRY</c> (623). Look in the next phone book.</summary>
    public const uint EntryNotFound = 623;

    /// <summary>
    /// <c>ERROR_INVALID_SIZE</c> (632). RAS refusing the <c>dwSize</c> of a versioned structure; the
    /// gateway answers it by trying the other known size, and it never reaches this sequence.
    /// </summary>
    public const uint InvalidSize = 632;
}

/// <summary>What RemoteDeck may hand to <c>RasDial</c> on the user's behalf.</summary>
/// <param name="UserName">The user name saved with the profile.</param>
/// <param name="PasswordHandle">
/// Not a password. RAS returns a <em>handle</em> to the saved one — sixteen asterisks — which
/// <c>RasDial</c> exchanges for the real secret. RemoteDeck never sees, asks for or stores a VPN
/// password, and this is what makes that possible.
/// </param>
/// <param name="Domain">The domain saved with the profile, usually empty.</param>
public sealed record RasCredential(string UserName, string PasswordHandle, string Domain)
{
    /// <summary>
    /// Whether this is enough to dial with. Both halves are needed: <c>RASDIALPARAMS</c> documents
    /// that an empty user name <em>and</em> password make RAS dial as the current Windows logon
    /// context, which is exactly the failure this whole path exists to avoid — a handle with no user
    /// name would walk into it quietly.
    /// </summary>
    public bool IsUsable => UserName.Length > 0 && PasswordHandle.Length > 0;
}

/// <summary>What RAS answered when asked about an entry: a code, and a credential when it had one.</summary>
public readonly record struct RasRead(uint Code, RasCredential? Credential);

/// <summary>How the attempt to raise a profile ended.</summary>
public enum VpnDialOutcome
{
    /// <summary>The tunnel is up and the rest of RemoteDeck can see it. The session may open.</summary>
    Connected = 0,

    /// <summary>
    /// <c>RasDial</c> returned success but no interface carries the profile's name. Say so rather
    /// than opening a session that would fail a second later.
    /// </summary>
    RaisedButNotVisible = 1,

    /// <summary>
    /// The profile has no saved credential. Nothing was dialled, and RemoteDeck will not ask for one.
    /// </summary>
    NoStoredCredential = 2,

    /// <summary>No phone book knows this profile.</summary>
    EntryNotFound = 3,

    /// <summary>RAS refused, in its own words.</summary>
    Failed = 4,

    /// <summary>
    /// Nobody is waiting any more. <see cref="VpnDialer"/> never returns this: a synchronous
    /// <c>RasDial</c> has no timeout of its own, so the cap belongs to whoever called it, and past
    /// that cap the attempt is still running inside Windows rather than over.
    /// </summary>
    StillDialing = 5,
}

/// <summary>The outcome, with the RAS code and message where there is one.</summary>
public readonly record struct VpnDialResult(VpnDialOutcome Outcome, uint Code, string Detail);

/// <summary>The RAS calls this sequence needs, as something a test can stand in for.</summary>
public interface IRasGateway
{
    /// <summary>
    /// The phone books to try, in order. The reference client answers 621 for RAS's own default, so
    /// the explicit paths come first and the default is a last resort rather than the first guess.
    /// A <c>null</c> entry means "let RAS choose".
    /// </summary>
    IReadOnlyList<string?> Phonebooks { get; }

    /// <summary>Reads an entry's saved dial parameters — <c>RasGetEntryDialParams</c>.</summary>
    RasRead ReadEntry(string? phonebook, string entry);

    /// <summary>Reads an entry's saved credentials — <c>RasGetCredentials</c>.</summary>
    RasRead ReadCredentials(string? phonebook, string entry);

    /// <summary>Dials, and returns the RAS code. Zero is success.</summary>
    uint Dial(string? phonebook, string entry, RasCredential credential);

    /// <summary>The VPN profiles that are up right now.</summary>
    IReadOnlySet<string> ConnectedProfiles();

    /// <summary>Windows's own text for a RAS code, in the user's language.</summary>
    string Describe(uint code);
}

/// <summary>
/// Raises a Windows VPN profile with the credential the user already stored in it.
///
/// Pure, and in <c>Core</c>, for the same reason as <see cref="VpnRequirement"/>: the P/Invoke can
/// only be tried by hand on a machine that has a real profile, but the order of the steps — which
/// phone book, what to do when it does not answer, whether there is anything to dial with — is
/// exactly what a test can hold.
/// </summary>
/// <remarks>
/// <para>
/// RemoteDeck stores no VPN secret, and this does not change that. <c>RasGetCredentials</c> never
/// returns a password: it returns a handle to the saved one, and <c>RasDial</c> exchanges that
/// handle for the real secret inside Windows. What passes through here is sixteen asterisks.
/// </para>
/// <para>
/// The predecessor ran <c>rasdial "&lt;profile&gt;"</c> with no credential at all. <c>RASDIALPARAMS</c>
/// documents what that means: an empty user name and password make RAS offer the current Windows
/// logon context to the VPN server. On the reference client the server dropped the call — RAS 628 —
/// while the network flyout, which dials with the profile's own credential, connected silently.
/// </para>
/// </remarks>
public sealed class VpnDialer
{
    /// <summary><c>RAS_MaxEntryName</c> from <c>ras.h</c>.</summary>
    private const int MaxEntryName = 256;

    private readonly IRasGateway _ras;

    /// <param name="ras">Never null: without it there is nothing to dial through.</param>
    public VpnDialer(IRasGateway ras)
    {
        ArgumentNullException.ThrowIfNull(ras);
        _ras = ras;
    }

    /// <summary>
    /// Raises <paramref name="profile"/>, using whatever the user saved in it.
    /// </summary>
    /// <param name="profile">The phone-book entry name. Trimmed, as everywhere else the user types
    /// one; blank is a caller's mistake rather than an answer, and throws.</param>
    public VpnDialResult Dial(string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        var entry = profile.Trim();
        if (entry.Length > MaxEntryName)
        {
            // A name RAS cannot hold names no entry. Round-tripping it through the API to be told so
            // buys nothing.
            return new VpnDialResult(VpnDialOutcome.EntryNotFound, RasError.EntryNotFound, string.Empty);
        }

        foreach (var phonebook in _ras.Phonebooks)
        {
            var read = _ras.ReadEntry(phonebook, entry);

            if (read.Code is RasError.EntryNotFound or RasError.CannotOpenPhonebook)
            {
                continue;
            }

            if (read.Code != RasError.Success)
            {
                // A real failure, not a phone book that does not hold this entry. Walking on would
                // turn it into "no such profile", which is a different and wrong thing to say.
                return Failure(read.Code);
            }

            return DialFrom(phonebook, entry, read.Credential);
        }

        return new VpnDialResult(VpnDialOutcome.EntryNotFound, RasError.EntryNotFound, string.Empty);
    }

    private VpnDialResult DialFrom(string? phonebook, string entry, RasCredential? fromEntry)
    {
        var credential = fromEntry;

        if (credential is not { IsUsable: true })
        {
            // The dial parameters are the documented source, but they are not the only one: a
            // credential set through RasSetCredentials lives beside them.
            var stored = _ras.ReadCredentials(phonebook, entry);
            if (stored.Code == RasError.Success && stored.Credential is { IsUsable: true })
            {
                credential = stored.Credential;
            }
        }

        if (credential is not { IsUsable: true })
        {
            // The invariant. RemoteDeck asks for no VPN secret and stores none, so a profile with
            // nothing saved cannot be raised from here — and dialling anyway is precisely what
            // produced the failure this replaces.
            return new VpnDialResult(VpnDialOutcome.NoStoredCredential, RasError.Success, string.Empty);
        }

        var code = _ras.Dial(phonebook, entry, credential);
        if (code != RasError.Success)
        {
            return Failure(code);
        }

        // Success from RasDial is not the same as a tunnel the rest of RemoteDeck can see, and the
        // session is about to be opened on the strength of it.
        return VpnRequirement.Check(entry, _ras.ConnectedProfiles()) == VpnState.Connected
            ? new VpnDialResult(VpnDialOutcome.Connected, RasError.Success, string.Empty)
            : new VpnDialResult(VpnDialOutcome.RaisedButNotVisible, RasError.Success, string.Empty);
    }

    private VpnDialResult Failure(uint code) =>
        new(VpnDialOutcome.Failed, code, _ras.Describe(code));
}
