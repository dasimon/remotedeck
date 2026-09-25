using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Navigation;
using RemoteDeck.App.Resources;
using RemoteDeck.App.Services;

namespace RemoteDeck.App.Views;

/// <summary>
/// What RemoteDeck is, in which version, and where to go next: the source, the releases, the logs.
/// Reached from the palette only, like every other command.
/// </summary>
/// <remarks>
/// It contacts nothing. Whether a newer release exists is one click away, on the releases page, and
/// RemoteDeck itself never calls a server to find out.
/// </remarks>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms brings System.Windows.Forms into scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button and TextBox ambiguous.
internal sealed partial class AboutWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string RepositoryUrl = "https://github.com/dasimon/remotedeck";

    public AboutWindow()
    {
        InitializeComponent();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        VersionText.Text = Text.Of(Strings.About_Version, Version);
        EnvironmentText.Text = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";
        CopyrightText.Text = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
        SourceLink.NavigateUri = new Uri(RepositoryUrl);
        ReleasesLink.NavigateUri = new Uri(RepositoryUrl + "/releases");
    }

    /// <summary>
    /// The version the release tag stamped, as the executable reports it. A development build reads
    /// 1.0.0; the part after "+" is the commit it was built from, which is worth keeping in a report.
    /// </summary>
    private static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "?";

    /// <summary>What a support conversation needs first, in English whatever the interface language:
    /// it is meant to be pasted into an issue, not read in the window.</summary>
    private static string Details() => string.Join(Environment.NewLine,
        $"RemoteDeck {Version}",
        RuntimeInformation.FrameworkDescription,
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
        $"UI culture: {CultureInfo.CurrentUICulture.Name}",
        $"Log: {ProbeLog.Path}");

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(ProbeLog.Path)!;
        Directory.CreateDirectory(folder);
        Open(folder);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(Details());
            CopyButton.Content = Strings.About_DetailsCopied;
        }
        catch (COMException ex)
        {
            // The clipboard can be held by another process for a moment; nothing to lose by saying so
            // in the log and leaving the button as it was.
            ProbeLog.Write("about", $"Clipboard busy: 0x{ex.HResult:X8}");
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Hands a URL or a folder to Windows, which opens it with whatever the user chose.</summary>
    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ProbeLog.Write("about", $"Could not open {target}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
