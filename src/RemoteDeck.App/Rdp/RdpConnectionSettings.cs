using RemoteDeck.Core.Model;

namespace RemoteDeck.App.Rdp;

/// <summary>
/// Everything needed to open one session except the secret, which never travels as a string.
///
/// A flattened, UI-free projection of <see cref="Connection"/>: the shell builds it from the saved
/// row — plus the user name and domain of the attached credential, which the connection row itself
/// does not carry — and <see cref="RdpSessionHost.Configure"/> is the only consumer.
/// <list type="bullet">
///   <item><c>AuthenticationLevel</c>: 0/1/2 as in <c>IMsRdpClientAdvancedSettings5::AuthenticationLevel</c>;
///   <c>null</c> leaves whatever default the control came with.</item>
///   <item><c>FixedWidth</c>/<c>FixedHeight</c>: the remote resolution to request when <c>DisplayMode</c>
///   is <see cref="DisplayMode.Fixed"/> or <see cref="DisplayMode.Scaled"/> — Scaled asks for the same
///   pinned resolution and lets SmartSizing fit it to the window; ignored under
///   <see cref="DisplayMode.Dynamic"/>, which follows the window instead.</item>
/// </list>
/// </summary>
internal sealed record RdpConnectionSettings(
    string Host,
    int Port,
    string UserName,
    string? Domain,
    bool UseWebAccount,
    bool AdminSession,
    bool RedirectClipboard,
    bool RedirectDrives,
    bool RedirectPrinters,
    bool RedirectAudio,
    int? AuthenticationLevel,
    DisplayMode DisplayMode,
    int? FixedWidth,
    int? FixedHeight);
