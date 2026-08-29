using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace RemoteDeck.App.Interop;

/// <summary>
/// Sets <c>IMsTscNonScriptable::ClearTextPassword</c> from a native BSTR through raw
/// <c>IDispatch::Invoke</c>, so the secret is never materialised as a managed string (spec D5, R1).
/// The caller owns the BSTR and must <see cref="Marshal.ZeroFreeBSTR"/> it afterwards.
/// </summary>
internal static unsafe class ComSecretPut
{
    // IID from https://learn.microsoft.com/windows/win32/termserv/imstscnonscriptable-interface
    private static readonly Guid IidIMsTscNonScriptable = new("c1e6743a-41c1-4a74-832a-0dd06c1c7a0e");

    private const ushort DispatchPropertyPut = 0x4;   // DISPATCH_PROPERTYPUT
    private const int DispidPropertyPut = -3;         // DISPID_PROPERTYPUT
    private const ushort VtBstr = 8;                  // VT_BSTR
    private const int VariantSize = 24;               // sizeof(VARIANT) on x64 (16 on x86; 24 is safe for both)

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
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint dispatch));
            try
            {
                // IMsTscNonScriptable is a dual interface: vtable = IUnknown(3) + IDispatch(4) + members.
                nint* vtable = *(nint**)dispatch;
                var getIdsOfNames = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint, uint, int*, int>)vtable[5];
                var invoke = (delegate* unmanaged[Stdcall]<nint, int, Guid*, uint, ushort, DISPPARAMS*, nint, nint, nint, int>)vtable[6];

                Guid nil = Guid.Empty;
                int dispId;
                nint name = Marshal.StringToCoTaskMemUni("ClearTextPassword");
                try
                {
                    Marshal.ThrowExceptionForHR(getIdsOfNames(dispatch, &nil, &name, 1, 0, &dispId));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }

                // VARIANT layout: vt (ushort) at offset 0, union payload at offset 8.
                byte* variant = stackalloc byte[VariantSize];
                new Span<byte>(variant, VariantSize).Clear();
                *(ushort*)variant = VtBstr;
                *(nint*)(variant + 8) = bstr;

                int namedArg = DispidPropertyPut;
                var parameters = new DISPPARAMS
                {
                    rgvarg = (nint)variant,
                    rgdispidNamedArgs = (nint)(&namedArg),
                    cArgs = 1,
                    cNamedArgs = 1,
                };

                // The VARIANT does not own the BSTR: Invoke copies it on the control's side
                // ([in] semantics). Nothing to free here; the caller zeroes and frees its own.
                Marshal.ThrowExceptionForHR(invoke(dispatch, dispId, &nil, 0, DispatchPropertyPut, &parameters, 0, 0, 0));
            }
            finally
            {
                Marshal.Release(dispatch);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
