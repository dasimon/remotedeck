using System.Windows.Forms;
using RemoteDeck.Core.Rdp;

namespace RemoteDeck.App.Interop;

/// <summary>
/// Hosts one instance of the Remote Desktop ActiveX control. The OCX is created when the
/// Win32 handle is created (AxHost semantics): call <see cref="Control.CreateControl"/> or
/// parent the host before touching <see cref="Ocx"/>.
/// </summary>
internal sealed class RdpAxHost : AxHost
{
    public RdpControlVersion Version { get; }

    public RdpAxHost(RdpControlVersion version) : base(version.Clsid.ToString("D"))
    {
        Version = version;
        Dock = DockStyle.Fill;
    }

    /// <summary>The raw COM object. Cast it to the MSTSCLib interface you need.</summary>
    public object Ocx => GetOcx()
        ?? throw new InvalidOperationException("The RDP control has not been created yet (no window handle).");
}
