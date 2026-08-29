using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using RemoteDeck.Core.Model;
using RemoteDeck.Core.Security;

namespace RemoteDeck.Core.Tests.Security;

// The vault is [SupportedOSPlatform("windows")]; this mirrors it so CA1416 accepts the call sites.
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialVaultTests
{
    private readonly DpapiCredentialVault _vault = new();

    private static Credential Make() => new()
    {
        Label = "L", UserName = "u", SecretBlob = [], Entropy = [],
    };

    /// <summary>Test-only helper: builds a native BSTR from a literal. Production code never does this.</summary>
    private static void WithBstr(string literal, Action<nint> use)
    {
        nint bstr = Marshal.StringToBSTR(literal);
        try { use(bstr); } finally { Marshal.ZeroFreeBSTR(bstr); }
    }

    [Fact]
    public void Seal_then_UseSecret_round_trips_unicode()
    {
        var c = Make();
        WithBstr("p@ss wörd — 密码", b => _vault.Seal(c, b));

        string? seen = null;
        _vault.UseSecret(c, b => seen = Marshal.PtrToStringBSTR(b));

        Assert.Equal("p@ss wörd — 密码", seen);
    }

    [Fact]
    public void Seal_sets_32_byte_entropy_and_a_non_empty_blob()
    {
        var c = Make();

        WithBstr("x", b => _vault.Seal(c, b));

        Assert.Equal(32, c.Entropy.Length);
        Assert.NotEmpty(c.SecretBlob);
    }

    [Fact]
    public void Same_secret_sealed_twice_gives_different_blobs_and_entropy()
    {
        var a = Make();
        var b = Make();
        WithBstr("same", x => _vault.Seal(a, x));
        WithBstr("same", x => _vault.Seal(b, x));

        Assert.NotEqual(a.Entropy, b.Entropy);
        Assert.NotEqual(a.SecretBlob, b.SecretBlob);
    }

    [Fact]
    public void Wrong_entropy_fails_to_unprotect()
    {
        var c = Make();
        WithBstr("secret", b => _vault.Seal(c, b));
        c.Entropy = new byte[32];

        Assert.Throws<CryptographicException>(() => _vault.UseSecret(c, _ => { }));
    }

    [Fact]
    public void Empty_secret_round_trips()
    {
        var c = Make();
        WithBstr("", b => _vault.Seal(c, b));

        int? length = null;
        _vault.UseSecret(c, b => length = Marshal.ReadInt32(b, -4));

        Assert.Equal(0, length);
    }

    [Fact]
    public void Vault_surface_has_no_string_parameter_or_return()
    {
        var methods = typeof(ICredentialVault).GetMethods(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(methods);
        Assert.All(methods, m =>
        {
            Assert.NotEqual(typeof(string), m.ReturnType);
            Assert.All(m.GetParameters(), p => Assert.NotEqual(typeof(string), p.ParameterType));
        });
    }
}
