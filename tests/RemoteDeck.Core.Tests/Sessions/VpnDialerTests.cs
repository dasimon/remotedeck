using RemoteDeck.Core.Sessions;

namespace RemoteDeck.Core.Tests.Sessions;

/// <summary>
/// The sequence behind "raise this VPN profile": which phone book to ask, what to do when it does
/// not answer, whether there is a credential to dial with at all, and what a dial that returned
/// success but shows nothing actually means.
///
/// Pure, and in Core, for the same reason as <see cref="VpnRequirement"/>: the P/Invoke can only be
/// tried by hand on a machine with a real profile, but the order of the steps and the decision at
/// each one is exactly what a test can hold.
/// </summary>
public sealed class VpnDialerTests
{
    private const string Entry = "VPN FDC";
    private const string UserPhonebook = @"C:\Users\someone\AppData\Roaming\...\rasphone.pbk";
    private const string AllUsersPhonebook = @"C:\ProgramData\...\rasphone.pbk";

    private static readonly RasCredential Saved = new("someone", "****************", "");

    /// <summary>
    /// A RAS that answers from a script rather than from Windows, and writes down what it was asked.
    /// </summary>
    private sealed class FakeRas : IRasGateway
    {
        private readonly Dictionary<string, RasRead> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RasRead> _credentials = new(StringComparer.Ordinal);

        public IReadOnlyList<string?> Phonebooks { get; set; } = [UserPhonebook, AllUsersPhonebook, null];

        public List<string> Asked { get; } = [];

        public List<(string? Phonebook, string Entry, RasCredential Credential)> Dialled { get; } = [];

        public uint DialCode { get; set; }

        public IReadOnlySet<string> Connected { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string Key(string? phonebook) => phonebook ?? "<default>";

        public FakeRas Entry(string? phonebook, RasRead read)
        {
            _entries[Key(phonebook)] = read;
            return this;
        }

        public FakeRas Credentials(string? phonebook, RasRead read)
        {
            _credentials[Key(phonebook)] = read;
            return this;
        }

        public RasRead ReadEntry(string? phonebook, string entry)
        {
            Asked.Add($"entry:{Key(phonebook)}:{entry}");
            return _entries.TryGetValue(Key(phonebook), out var read) ? read : new RasRead(RasError.EntryNotFound, null);
        }

        public RasRead ReadCredentials(string? phonebook, string entry)
        {
            Asked.Add($"credentials:{Key(phonebook)}:{entry}");
            return _credentials.TryGetValue(Key(phonebook), out var read) ? read : new RasRead(RasError.EntryNotFound, null);
        }

        public uint Dial(string? phonebook, string entry, RasCredential credential)
        {
            Dialled.Add((phonebook, entry, credential));
            return DialCode;
        }

        public IReadOnlySet<string> ConnectedProfiles() => Connected;

        public string Describe(uint code) => $"RAS says {code}";
    }

    private static FakeRas ReadyToDial() => new FakeRas()
        .Entry(UserPhonebook, new RasRead(RasError.Success, Saved));

    [Fact]
    public void A_blank_profile_is_refused_rather_than_dialled()
    {
        var ras = new FakeRas();
        var dialer = new VpnDialer(ras);

        Assert.Throws<ArgumentNullException>(() => dialer.Dial(null!));
        Assert.Throws<ArgumentException>(() => dialer.Dial(""));
        Assert.Throws<ArgumentException>(() => dialer.Dial("   "));
        Assert.Empty(ras.Dialled);
    }

    [Fact]
    public void A_name_longer_than_RAS_allows_is_unknown_without_asking_RAS()
    {
        // RAS_MaxEntryName is 256. A name RAS cannot hold names no entry, and there is nothing to
        // gain by making it round-trip through the API to be told so.
        var ras = new FakeRas();

        var result = new VpnDialer(ras).Dial(new string('x', 257));

        Assert.Equal(VpnDialOutcome.EntryNotFound, result.Outcome);
        Assert.Empty(ras.Asked);
        Assert.Empty(ras.Dialled);
    }

    [Fact]
    public void The_name_is_trimmed_before_RAS_sees_it()
    {
        var ras = ReadyToDial();
        ras.Connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Entry };

        new VpnDialer(ras).Dial("  VPN FDC  ");

        Assert.Equal(Entry, ras.Dialled.Single().Entry);
    }

    [Fact]
    public void The_first_phonebook_that_answers_is_the_only_one_asked()
    {
        var ras = ReadyToDial();
        ras.Connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Entry };

        new VpnDialer(ras).Dial(Entry);

        Assert.Equal([$"entry:{UserPhonebook}:{Entry}"], ras.Asked);
    }

    [Fact]
    public void An_entry_missing_from_one_phonebook_is_looked_for_in_the_next()
    {
        var ras = new FakeRas()
            .Entry(UserPhonebook, new RasRead(RasError.EntryNotFound, null))
            .Entry(AllUsersPhonebook, new RasRead(RasError.Success, Saved));
        ras.Connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Entry };

        var result = new VpnDialer(ras).Dial(Entry);

        Assert.Equal(VpnDialOutcome.Connected, result.Outcome);
        Assert.Equal(AllUsersPhonebook, ras.Dialled.Single().Phonebook);
    }

    [Fact]
    public void A_phonebook_that_cannot_be_opened_is_stepped_over()
    {
        // Measured on the reference client: RAS's own default phone book answers 621 there, which is
        // why the explicit paths are tried first and 621 must not be fatal.
        var ras = new FakeRas()
            .Entry(UserPhonebook, new RasRead(RasError.CannotOpenPhonebook, null))
            .Entry(AllUsersPhonebook, new RasRead(RasError.Success, Saved));
        ras.Connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Entry };

        Assert.Equal(VpnDialOutcome.Connected, new VpnDialer(ras).Dial(Entry).Outcome);
    }

    [Fact]
    public void A_profile_no_phonebook_knows_is_reported_as_unknown()
    {
        var ras = new FakeRas();

        var result = new VpnDialer(ras).Dial(Entry);

        Assert.Equal(VpnDialOutcome.EntryNotFound, result.Outcome);
        Assert.Equal(3, ras.Asked.Count);
        Assert.Empty(ras.Dialled);
    }

    [Fact]
    public void An_error_that_is_neither_of_those_stops_there_and_is_reported()
    {
        // 5 is ERROR_ACCESS_DENIED. Walking on to the next phone book would turn a real failure into
        // "no such profile", which is a different — and wrong — thing to tell the user.
        var ras = new FakeRas().Entry(UserPhonebook, new RasRead(5, null));

        var result = new VpnDialer(ras).Dial(Entry);

        Assert.Equal(VpnDialOutcome.Failed, result.Outcome);
        Assert.Equal(5u, result.Code);
        Assert.Equal("RAS says 5", result.Detail);
        Assert.Single(ras.Asked);
        Assert.Empty(ras.Dialled);
    }

    [Fact]
    public void With_no_stored_password_nothing_is_dialled_at_all()
    {
        // The invariant: RemoteDeck asks for no VPN secret and stores none, so a profile with
        // nothing saved cannot be raised. Dialling anyway is exactly what produced the 628 this
        // whole change is about.
        var ras = new FakeRas()
            .Entry(UserPhonebook, new RasRead(RasError.Success, new RasCredential("someone", "", "")))
            .Credentials(UserPhonebook, new RasRead(RasError.Success, new RasCredential("someone", "", "")));

        var result = new VpnDialer(ras).Dial(Entry);

        Assert.Equal(VpnDialOutcome.NoStoredCredential, result.Outcome);
        Assert.Empty(ras.Dialled);
    }

    [Fact]
    public void A_password_with_no_user_name_is_not_a_credential()
    {
        // Both empty means RAS dials as the current logon context — the failure being fixed. A
        // handle with no user name would do the same thing quietly.
        var ras = new FakeRas()
            .Entry(UserPhonebook, new RasRead(RasError.Success, new RasCredential("", "****************", "")))
            .Credentials(UserPhonebook, new RasRead(RasError.Success, new RasCredential("", "****************", "")));

        Assert.Equal(VpnDialOutcome.NoStoredCredential, new VpnDialer(ras).Dial(Entry).Outcome);
        Assert.Empty(ras.Dialled);
    }

    [Fact]
    public void When_the_entry_carries_nothing_the_credential_store_is_asked()
    {
        var ras = new FakeRas()
            .Entry(UserPhonebook, new RasRead(RasError.Success, null))
            .Credentials(UserPhonebook, new RasRead(RasError.Success, Saved));
        ras.Connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Entry };

        var result = new VpnDialer(ras).Dial(Entry);

        Assert.Equal(VpnDialOutcome.Connected, result.Outcome);
        Assert.Equal(
            [$"entry:{UserPhonebook}:{Entry}", $"credentials:{UserPhonebook}:{Entry}"],
            ras.Asked);
    }

    [Fact]
    public void The_handle_is_handed_to_RasDial_exactly_as_it_came_back()
    {
        // RemoteDeck never holds a password: what it passes on is the sixteen-asterisk handle RAS
        // gave it, unread and unaltered.
        var ras = ReadyToDial();
        ras.Connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Entry };

        new VpnDialer(ras).Dial(Entry);

        Assert.Same(Saved, ras.Dialled.Single().Credential);
    }

    [Fact]
    public void A_dial_that_brings_the_profile_up_is_a_success()
    {
        var ras = ReadyToDial();
        ras.Connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vpn fdc" };

        var result = new VpnDialer(ras).Dial(Entry);

        Assert.Equal(VpnDialOutcome.Connected, result.Outcome);
        Assert.Equal(0u, result.Code);
    }

    [Fact]
    public void A_dial_that_succeeds_but_shows_nothing_is_not_called_a_success()
    {
        // RasDial returning zero is not the same as a tunnel the rest of RemoteDeck can see. Opening
        // the session on that promise would fail a second later with a cryptic RDP error.
        var ras = ReadyToDial();

        var result = new VpnDialer(ras).Dial(Entry);

        Assert.Equal(VpnDialOutcome.RaisedButNotVisible, result.Outcome);
    }

    [Fact]
    public void A_refused_dial_carries_the_code_and_Windows_own_words()
    {
        var ras = ReadyToDial();
        ras.DialCode = 691;

        var result = new VpnDialer(ras).Dial(Entry);

        Assert.Equal(VpnDialOutcome.Failed, result.Outcome);
        Assert.Equal(691u, result.Code);
        Assert.Equal("RAS says 691", result.Detail);
    }

    [Fact]
    public void A_refused_dial_is_not_tried_again_in_another_phonebook()
    {
        // The entry was found and the credential was real; another phone book would not hold a
        // better answer, and a VPN server is not something to knock on twice by accident.
        var ras = ReadyToDial();
        ras.DialCode = 691;

        new VpnDialer(ras).Dial(Entry);

        Assert.Single(ras.Dialled);
    }

    [Fact]
    public void The_dialer_rejects_a_null_gateway_rather_than_failing_later()
    {
        Assert.Throws<ArgumentNullException>(() => new VpnDialer(null!));
    }
}
