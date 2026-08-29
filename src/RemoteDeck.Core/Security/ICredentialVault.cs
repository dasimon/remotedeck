using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Security;

/// <summary>
/// Encrypts and lends secrets. By design no member accepts or returns a <see cref="string"/>:
/// secrets travel as native BSTRs owned by the caller (spec §5.2).
/// </summary>
public interface ICredentialVault
{
    /// <summary>Encrypts the BSTR's content into <paramref name="credential"/> (new entropy + blob). The caller keeps ownership of the BSTR.</summary>
    void Seal(Credential credential, nint secretBstr);

    /// <summary>Decrypts the secret into a native BSTR lent to <paramref name="useBstr"/>, then zeroes and frees it.</summary>
    void UseSecret(Credential credential, Action<nint> useBstr);
}
