using System.Windows;
using System.Windows.Media;
using RemoteDeck.App.Interop;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Rdp;
using Wpf.Ui.Appearance;

namespace RemoteDeck.App.Views;

/// <summary>
/// Lot-0 shell: a Fluent window hosting the Remote Desktop ActiveX control through a
/// <c>WindowsFormsHost</c>. Probes R4 (custom title bar + HWND host coexistence) and
/// R3 (per-monitor DPI). Connection logic lands in task 5.
/// </summary>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms puts System.Windows.Forms in
// scope through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button,
// TextBox and friends ambiguous here.
public partial class ShellWindow : Wpf.Ui.Controls.FluentWindow
{
    private RdpAxHost? _ax;

    public ShellWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        HostInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_HOST") ?? "";
        UserInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_USER") ?? "";
        DomainInput.Text = Environment.GetEnvironmentVariable("REMOTEDECK_PROBE_DOMAIN") ?? "";

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var version = RdpControlCatalog.Select(ClsidRegistry.IsRegistered);
        if (version is null)
        {
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, "No Remote Desktop control found",
                "None of the known mstscax.dll CLSIDs is registered on this machine.");
            ConnectButton.IsEnabled = false;
            return;
        }

        var ax = new RdpAxHost(version);
        try
        {
            RdpHost.Child = ax;
            ax.CreateControl();
        }
        catch (Exception ex)
        {
            // Being listed in HKCR\CLSID is not the same as being creatable: mstscax.dll
            // registers control versions its class factory then refuses
            // (CLASS_E_CLASSNOTAVAILABLE, 0x80040111). Surface it, never crash the shell.
            ProbeLog.Write("R4", $"control version {version.Label} ({version.Clsid:D}) is registered but not creatable: {ex.GetType().Name} 0x{ex.HResult:X8}");
            ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Error, $"RDP control v{version.Label} could not be created",
                $"The CLSID is registered but its class factory refused it (0x{ex.HResult:X8}). See {ProbeLog.Path}.");
            ConnectButton.IsEnabled = false;
            return;
        }

        _ax = ax;

        var dpi = VisualTreeHelper.GetDpi(this);
        ProbeLog.Write("R4", $"FluentWindow + WindowsFormsHost created; control version {version.Label} ({version.Clsid:D})");
        ProbeLog.Write("R3", $"Window DPI scale X={dpi.DpiScaleX:F2} Y={dpi.DpiScaleY:F2}");

        ShowStatus(Wpf.Ui.Controls.InfoBarSeverity.Informational, $"RDP control v{version.Label} ready", "Enter a host and press Connect.");
    }

    private void ShowStatus(Wpf.Ui.Controls.InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        // Wired in Task 5.
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        // Wired in Task 5.
    }
}
