using System.Runtime.InteropServices;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Interop;

/// <summary>
/// What a full-screen window spanning several monitors needs from Windows, which WPF does not give:
/// a placement in physical pixels, and a full-screen marking for the Shell.
/// </summary>
internal static class SpanningFullScreen
{
    private static readonly nint HwndTop = 0;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    /// <summary>
    /// Places the window on <paramref name="physical"/>, in physical pixels. Not through
    /// <c>Left</c>/<c>Top</c>/<c>Width</c>/<c>Height</c>: those are device-independent units, and a
    /// window across two monitors has no single scale to convert them with.
    /// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos
    /// </summary>
    public static void Cover(nint hwnd, System.Drawing.Rectangle physical)
    {
        if (!SetWindowPos(hwnd, HwndTop, physical.X, physical.Y, physical.Width, physical.Height, SwpFrameChanged | SwpShowWindow))
        {
            ProbeLog.Write("display", $"SetWindowPos({physical}) failed: error {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>
    /// Tells the Shell the window is full screen, so the taskbar goes under it while it is active.
    /// The Shell only detects that by itself for a window matching one monitor exactly; seen on
    /// 2026-10-29, a window across two monitors left the taskbar on top of the session.
    /// https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist2-markfullscreenwindow
    /// </summary>
    public static void MarkFullScreen(nint hwnd, bool on)
    {
        try
        {
            var list = (ITaskbarList2)new TaskbarList();
            try
            {
                list.HrInit();
                list.MarkFullscreenWindow(hwnd, on);
            }
            finally
            {
                Marshal.ReleaseComObject(list);
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Write("display", $"MarkFullscreenWindow({on}) failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [ComImport]
    [Guid("602D4995-B13A-429b-A66E-1935E44F4317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2
    {
        // ITaskbarList, in vtable order, then ITaskbarList2's one method.
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList;
}
