using System.Drawing;

namespace RemoteDeck.Core.Rdp;

/// <summary>
/// The rectangle the remote monitors cover, in local virtual-screen pixels, as reported by
/// <c>IMsRdpClientNonScriptable5::GetRemoteMonitorsBoundingBox</c>.
/// https://learn.microsoft.com/windows/win32/termserv/imsrdpclientnonscriptable5-getremotemonitorsboundingbox
/// </summary>
public static class RemoteMonitorBox
{
    /// <summary>
    /// Converts the control's edges into a rectangle. Its right and bottom edges are inclusive
    /// (measured 2026-10-29: (-1920,0)-(5119,1439) for 7040×1440 pixels), hence the +1. An empty or
    /// inverted box is <c>null</c>, so the caller falls back to the local virtual screen.
    /// </summary>
    public static Rectangle? FromInclusive(int left, int top, int right, int bottom)
    {
        int width = right - left + 1;
        int height = bottom - top + 1;
        return width > 0 && height > 0 ? new Rectangle(left, top, width, height) : null;
    }
}
