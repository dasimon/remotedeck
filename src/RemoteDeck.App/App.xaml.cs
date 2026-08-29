using System.Windows;
using RemoteDeck.App.Services;

namespace RemoteDeck.App;

/// <summary>Application entry point. Logs a startup marker so the lot-0 probe log always
/// starts with a run boundary, then hands over to the <c>StartupUri</c> shell window.</summary>
// System.Windows.Application is qualified on purpose: UseWindowsForms puts
// System.Windows.Forms.Application in scope too, which makes the short name ambiguous.
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProbeLog.Write("startup", $"RemoteDeck starting, log at {ProbeLog.Path}");
    }
}
