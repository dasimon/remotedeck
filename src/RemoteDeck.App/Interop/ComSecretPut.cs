using System.Runtime.InteropServices;

namespace RemoteDeck.App.Interop;

/// <summary>
/// Sets <c>IMsTscNonScriptable::ClearTextPassword</c> from a native BSTR by calling the
/// interface's vtable slot directly, so the secret is never materialised as a managed string
/// (spec D5, R1). The generated interop only offers <c>set_ClearTextPassword(string)</c>,
/// which would force exactly that materialisation.
/// The caller owns the BSTR and must <see cref="Marshal.ZeroFreeBSTR"/> it afterwards.
/// </summary>
internal static unsafe class ComSecretPut
{
    // IID and interface shape from
    // https://learn.microsoft.com/windows/win32/termserv/imstscnonscriptable-interface
    private static readonly Guid IidIMsTscNonScriptable = new("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e");

    /// <summary>
    /// Vtable index of <c>put_ClearTextPassword</c>. IMsTscNonScriptable derives from IUnknown
    /// only - it is NOT a dual interface, so there is no IDispatch block to skip: slots 0-2 are
    /// QueryInterface/AddRef/Release and the first interface member follows at slot 3. Verified
    /// against the generated Interop.MSTSCLib.dll, whose MSTSCLib.IMsTscNonScriptable carries
    /// [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] with no base interface and declares
    /// its members in this order: set_ClearTextPassword, set_PortablePassword,
    /// get_PortablePassword, set_PortableSalt, get_PortableSalt, set_BinaryPassword,
    /// get_BinaryPassword, set_BinarySalt, get_BinarySalt, ResetPassword.
    /// </summary>
    private const int PutClearTextPasswordSlot = 3;

    public static void PutClearTextPassword(object ocx, nint bstr)
    {
        ArgumentNullException.ThrowIfNull(ocx);
        if (bstr == 0)
        {
            throw new ArgumentException("BSTR must not be null.", nameof(bstr));
        }

        nint unknown = Marshal.GetIUnknownForObject(ocx);
        try
        {
            Guid iid = IidIMsTscNonScriptable;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint nonScriptable));
            try
            {
                // HRESULT put_ClearTextPassword([in] BSTR). The callee copies the string
                // ([in] semantics); nothing to free here, the caller zeroes and frees its own.
                nint* vtable = *(nint**)nonScriptable;
                var put = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[PutClearTextPasswordSlot];
                Marshal.ThrowExceptionForHR(put(nonScriptable, bstr));
            }
            finally
            {
                Marshal.Release(nonScriptable);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
