using CommunityToolkit.Mvvm.ComponentModel;
using RemoteDeck.Core.Model;

// The view-model exposes a DisplayMode property whose type is also named DisplayMode; the alias keeps
// every enum-member reference below unambiguous instead of leaning on the "Color Color" rule.
using CoreDisplayMode = RemoteDeck.Core.Model.DisplayMode;

namespace RemoteDeck.App.ViewModels;

/// <summary>One entry of the authentication-level combo. <see cref="Value"/> is what lands in
/// <see cref="Connection.AuthenticationLevel"/>; <c>null</c> means "leave the client default".</summary>
/// <param name="Value">The RDP client authentication level, or <c>null</c> for the default.</param>
/// <param name="Label">What the combo shows.</param>
public sealed record AuthenticationLevelOption(int? Value, string Label);

/// <summary>
/// Form state for the connection editor: one observable property per <see cref="Connection"/> column
/// the user can edit, plus the three lists the combos bind to.
///
/// The numeric fields are <c>double?</c> and not <c>int</c> because <c>ui:NumberBox.Value</c> is a
/// <c>double?</c>: binding it straight through keeps an empty box empty (instead of silently reading
/// back as zero) and leaves the rounding in one place, <see cref="ApplyTo"/>.
/// </summary>
public sealed partial class ConnectionEditorViewModel : ObservableObject
{
    private const int DefaultPort = 3389;

    /// <summary>Placeholder row of the credential combo. Its <c>Id</c> stays 0, which is how
    /// <see cref="CredentialId"/> recognises it and stores <c>null</c>.</summary>
    public static Credential NoCredential { get; } = new() { Label = "(none)", UserName = "", SecretBlob = [], Entropy = [] };

    // Values verified against the Microsoft RDP client documentation in lot 0; never renumber.
    private static readonly IReadOnlyList<AuthenticationLevelOption> AllAuthenticationLevels =
    [
        new(null, "Default"),
        new(0, "No server auth"),
        new(1, "Required"),
        new(2, "Prompt if failed"),
    ];

    private static readonly IReadOnlyList<CoreDisplayMode> AllDisplayModes = Enum.GetValues<CoreDisplayMode>();

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private double? _port = DefaultPort;
    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private Credential? _selectedCredential = NoCredential;
    [ObservableProperty] private bool _isFavorite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixedSizeEnabled))]
    private CoreDisplayMode _displayMode = CoreDisplayMode.Dynamic;

    [ObservableProperty] private double? _fixedWidth;
    [ObservableProperty] private double? _fixedHeight;
    [ObservableProperty] private bool _redirectClipboard = true;
    [ObservableProperty] private bool _redirectDrives;
    [ObservableProperty] private bool _redirectPrinters;
    [ObservableProperty] private bool _redirectAudio;
    [ObservableProperty] private bool _adminSession;
    [ObservableProperty] private bool _useWebAccount;
    [ObservableProperty] private AuthenticationLevelOption? _selectedAuthenticationLevel = AllAuthenticationLevels[0];
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _errors = "";

    /// <summary>The credential combo: <see cref="NoCredential"/> first, then the saved accounts.</summary>
    public IReadOnlyList<Credential> Credentials { get; init; } = [NoCredential];

    /// <summary>Groups already in use, offered by the editable group combo. Free text is still allowed.</summary>
    public IReadOnlyList<string> KnownGroups { get; init; } = [];

    /// <summary>Instance views on the fixed option lists: the binding engine only walks instance properties.</summary>
    public IReadOnlyList<CoreDisplayMode> DisplayModes => AllDisplayModes;

    /// <inheritdoc cref="DisplayModes" />
    public IReadOnlyList<AuthenticationLevelOption> AuthenticationLevels => AllAuthenticationLevels;

    /// <summary>The stored foreign key: the placeholder row means "no credential".</summary>
    public long? CredentialId => SelectedCredential is { Id: > 0 } credential ? credential.Id : null;

    /// <summary>Dynamic follows the window, so the fixed size is meaningless — and its boxes are disabled.</summary>
    public bool IsFixedSizeEnabled => DisplayMode != CoreDisplayMode.Dynamic;

    // An empty box stays null all the way to the rules, which report it as missing rather than out of range.
    private int? PortNumber => Port is { } port ? (int)Math.Round(port) : null;
    private int? FixedWidthNumber => FixedWidth is { } width ? (int)Math.Round(width) : null;
    private int? FixedHeightNumber => FixedHeight is { } height ? (int)Math.Round(height) : null;

    /// <summary>Runs <see cref="ConnectionRules"/> and publishes the messages in <see cref="Errors"/>.</summary>
    public bool Validate()
    {
        var errors = ConnectionRules.Validate(Name, Host, PortNumber, DisplayMode, FixedWidthNumber, FixedHeightNumber);
        Errors = string.Join("\n", errors);
        return errors.Count == 0;
    }

    /// <summary>Copies the form onto the model. Call <see cref="Validate"/> first: this one trusts its state.</summary>
    public void ApplyTo(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.Name = Name.Trim();
        connection.Host = Host.Trim();
        connection.Port = PortNumber ?? DefaultPort;
        connection.GroupName = GroupName?.Trim() ?? "";
        connection.CredentialId = CredentialId;
        connection.IsFavorite = IsFavorite;
        connection.DisplayMode = DisplayMode;
        // Kept even under Dynamic: the user gets their numbers back when switching to Scaled or Fixed.
        connection.FixedWidth = FixedWidthNumber;
        connection.FixedHeight = FixedHeightNumber;
        connection.RedirectClipboard = RedirectClipboard;
        connection.RedirectDrives = RedirectDrives;
        connection.RedirectPrinters = RedirectPrinters;
        connection.RedirectAudio = RedirectAudio;
        connection.AdminSession = AdminSession;
        connection.UseWebAccount = UseWebAccount;
        connection.AuthenticationLevel = SelectedAuthenticationLevel?.Value;
        connection.Notes = Notes ?? "";
    }

    /// <summary>Builds the form for an existing connection, or a blank one when <paramref name="connection"/> is null.</summary>
    public static ConnectionEditorViewModel From(Connection? connection, IEnumerable<Credential> credentials, IEnumerable<string> groups)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(groups);

        List<Credential> choices = [NoCredential, .. credentials];
        var viewModel = new ConnectionEditorViewModel
        {
            Credentials = choices,
            KnownGroups = [.. groups],
            Name = connection?.Name ?? "",
            Host = connection?.Host ?? "",
            Port = connection?.Port ?? DefaultPort,
            GroupName = connection?.GroupName ?? "",
            IsFavorite = connection?.IsFavorite ?? false,
            DisplayMode = connection?.DisplayMode ?? CoreDisplayMode.Dynamic,
            FixedWidth = connection?.FixedWidth,
            FixedHeight = connection?.FixedHeight,
            RedirectClipboard = connection?.RedirectClipboard ?? true,
            RedirectDrives = connection?.RedirectDrives ?? false,
            RedirectPrinters = connection?.RedirectPrinters ?? false,
            RedirectAudio = connection?.RedirectAudio ?? false,
            AdminSession = connection?.AdminSession ?? false,
            UseWebAccount = connection?.UseWebAccount ?? false,
            Notes = connection?.Notes ?? "",
        };

        // A credential deleted since the connection was saved simply falls back to "(none)".
        viewModel.SelectedCredential = choices.FirstOrDefault(c => c.Id == connection?.CredentialId) ?? NoCredential;
        viewModel.SelectedAuthenticationLevel =
            AllAuthenticationLevels.FirstOrDefault(o => o.Value == connection?.AuthenticationLevel) ?? AllAuthenticationLevels[0];
        return viewModel;
    }
}
