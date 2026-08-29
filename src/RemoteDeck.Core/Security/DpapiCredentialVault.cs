using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RemoteDeck.Core.Model;

namespace RemoteDeck.Core.Security;

/// <summary>
/// Windows DPAPI, CurrentUser scope, plus 32 bytes of per-credential entropy (spec §5.1).
/// The database file alone is useless without the Windows profile; two identical secrets
/// produce different blobs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialVault : ICredentialVault
{
    private const int EntropyLength = 32;

    public void Seal(Credential credential, nint secretBstr)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var entropy = RandomNumberGenerator.GetBytes(EntropyLength);
        var utf8 = SecretBytes.Utf8FromBstr(secretBstr);
        try
        {
            credential.SecretBlob = ProtectedData.Protect(utf8, entropy, DataProtectionScope.CurrentUser);
            credential.Entropy = entropy;
        }
        finally
        {
            SecretBytes.Zero(utf8);
        }
    }

    public void UseSecret(Credential credential, Action<nint> useBstr)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(useBstr);
        var utf8 = ProtectedData.Unprotect(credential.SecretBlob, credential.Entropy, DataProtectionScope.CurrentUser);
        try
        {
            nint bstr = SecretBytes.BstrFromUtf8(utf8);
            try
            {
                useBstr(bstr);
            }
            finally
            {
                Marshal.ZeroFreeBSTR(bstr);
            }
        }
        finally
        {
            SecretBytes.Zero(utf8);
        }
    }
}
