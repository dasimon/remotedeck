using System.Runtime.InteropServices;
using Microsoft.Win32;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Interop;

/// <summary>
/// Implementation of the "is this CLSID usable here?" predicate consumed by
/// <see cref="Core.Rdp.RdpControlCatalog.Select"/>.
/// </summary>
/// <remarks>
/// Registry presence alone is not enough. mstscax.dll registers control versions that its
/// own class factory then refuses: on Windows 11 26200 the version-13 CLSID has a complete
/// <c>InprocServer32</c> entry, yet <c>CoGetClassObject</c> answers
/// <c>CLASS_E_CLASSNOTAVAILABLE</c> (0x80040111) and the <c>AxHost</c> throws at handle
/// creation. So the registry lookup is kept only as a cheap pre-filter and the real answer
/// comes from asking COM for the class factory.
/// </remarks>
internal static class ClsidRegistry
{
    private const int ClsctxInprocServer = 0x1;

    /// <summary>IID_IClassFactory.</summary>
    private static readonly Guid ClassFactoryIid = new("00000001-0000-0000-C000-000000000046");

    /// <summary>
    /// True when <paramref name="clsid"/> is registered <em>and</em> its class factory can
    /// actually be obtained in-process. Candidates that fail the second test are reported to
    /// <see cref="ProbeLog"/> with their HRESULT: that is probe R4 evidence.
    /// </summary>
    public static bool IsUsable(Guid clsid)
    {
        if (!IsRegistered(clsid))
        {
            return false;
        }

        var iid = ClassFactoryIid;
        var hr = CoGetClassObject(ref clsid, ClsctxInprocServer, 0, ref iid, out var factory);
        if (hr < 0)
        {
            ProbeLog.Write("R4", $"CLSID {clsid:D} is registered but not creatable: CoGetClassObject returned 0x{hr:X8}");
            return false;
        }

        if (factory != 0)
        {
            Marshal.Release(factory);
        }

        return true;
    }

    /// <summary>Cheap pre-filter: does the CLSID have an in-process server registered?</summary>
    private static bool IsRegistered(Guid clsid)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\InprocServer32");
        return key?.GetValue(null) is string path && path.Length > 0;
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoGetClassObject(ref Guid clsid, int context, nint reserved, ref Guid iid, out nint classFactory);
}
