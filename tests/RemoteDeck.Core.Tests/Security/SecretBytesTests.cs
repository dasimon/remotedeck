using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using RemoteDeck.Core.Security;

namespace RemoteDeck.Core.Tests.Security;

// SecretBytes is [SupportedOSPlatform("windows")]; this mirrors it so CA1416 accepts the call sites.
[SupportedOSPlatform("windows")]
public sealed class SecretBytesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("p@ss")]
    [InlineData("mot de passe é€𝄞")]
    public void A_BSTR_round_trips_through_UTF8(string literal)
    {
        nint bstr = Marshal.StringToBSTR(literal);
        try
        {
            var utf8 = SecretBytes.Utf8FromBstr(bstr);
            Assert.Equal(Encoding.UTF8.GetBytes(literal), utf8);

            nint back = SecretBytes.BstrFromUtf8(utf8);
            try
            {
                Assert.Equal(literal, Marshal.PtrToStringBSTR(back));
            }
            finally
            {
                Marshal.ZeroFreeBSTR(back);
            }
        }
        finally
        {
            Marshal.ZeroFreeBSTR(bstr);
        }
    }

    [Fact]
    public void Zero_clears_every_byte()
    {
        byte[] bytes = [1, 2, 3, 4];

        SecretBytes.Zero(bytes);

        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_null_BSTR_is_refused()
        => Assert.Throws<ArgumentException>(() => SecretBytes.Utf8FromBstr(0));
}
