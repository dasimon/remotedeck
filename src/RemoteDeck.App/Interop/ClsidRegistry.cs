using Microsoft.Win32;

namespace RemoteDeck.App.Interop;

/// <summary>Registry-backed implementation of the "is this CLSID usable here?" predicate.</summary>
internal static class ClsidRegistry
{
    public static bool IsRegistered(Guid clsid)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\InprocServer32");
        return key?.GetValue(null) is string path && path.Length > 0;
    }
}
