namespace RemoteDeck.Core.Model;

/// <summary>
/// A reusable account. The secret is stored as an opaque DPAPI blob plus per-row entropy;
/// this type never holds the decrypted value (spec §5).
/// </summary>
public sealed class Credential
{
    public long Id { get; set; }
    public required string Label { get; set; }
    public string? Domain { get; set; }
    public required string UserName { get; set; }
    public required byte[] SecretBlob { get; set; }
    public required byte[] Entropy { get; set; }
    public DateTime ModifiedUtc { get; set; }
}
