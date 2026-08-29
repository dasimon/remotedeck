using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDeck.Core.Security;

/// <summary>
/// Conversions between a native BSTR and UTF-8 bytes that never create a managed string.
/// Every intermediate buffer is zeroed in a finally block.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SecretBytes
{
    /// <summary>Copies the BSTR's UTF-16 payload into UTF-8 bytes. Caller zeroes the result.</summary>
    public static byte[] Utf8FromBstr(nint bstr)
    {
        if (bstr == 0) throw new ArgumentException("BSTR must not be null.", nameof(bstr));
        int chars = Marshal.ReadInt32(bstr, -4) / 2;   // BSTR length prefix is in bytes
        var buffer = new char[chars];
        Marshal.Copy(bstr, buffer, 0, chars);
        try
        {
            return Encoding.UTF8.GetBytes(buffer);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    /// <summary>Allocates a BSTR from UTF-8 bytes. Caller frees it with <see cref="Marshal.ZeroFreeBSTR"/>.</summary>
    public static nint BstrFromUtf8(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[Encoding.UTF8.GetCharCount(utf8)];
        var handle = GCHandle.Alloc(chars, GCHandleType.Pinned);
        try
        {
            Encoding.UTF8.GetChars(utf8, chars);
            nint bstr = SysAllocStringLen(handle.AddrOfPinnedObject(), (uint)chars.Length);
            if (bstr == 0) throw new OutOfMemoryException("SysAllocStringLen failed.");
            return bstr;
        }
        finally
        {
            Array.Clear(chars);
            handle.Free();
        }
    }

    public static void Zero(byte[] bytes) => CryptographicOperations.ZeroMemory(bytes);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern nint SysAllocStringLen(nint source, uint length);
}
