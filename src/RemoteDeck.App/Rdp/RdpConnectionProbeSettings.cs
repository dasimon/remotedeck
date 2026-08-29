namespace RemoteDeck.App.Rdp;

/// <summary>Everything needed to open one session except the secret, which never travels as a string.</summary>
internal sealed record RdpConnectionProbeSettings(
    string Host,
    int Port,
    string UserName,
    string? Domain,
    bool UseWebAccount);
