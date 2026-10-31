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
/// Opening it contacts nothing. The one call to a server is the "Check for updates" button, which asks
/// the GitHub API for the latest release of this build's repository (see <see cref="UpdateCoordinator"/>).
/// RemoteDeck makes the same call by itself at startup and once a day, unless the user turned that off
/// in the palette; the button works either way, because it is the user asking.
/// </remarks>
// Wpf.Ui.Controls.* is qualified on purpose: UseWindowsForms brings System.Windows.Forms into scope
// through implicit usings, and a bare `using Wpf.Ui.Controls;` would make Button and TextBox ambiguous.
internal sealed partial class AboutWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string RepositoryUrl = "https://github.com/dasimon/remotedeck";

    private readonly UpdateCoordinator? _updates;
    private readonly Action? _updateFound;

    /// <param name="updates">The main window's coordinator; null when there is none to ask, which reads
    /// as a development build.</param>
    /// <param name="updateFound">Called when a manual check finds a release, so that the main window
    /// shows its banner even if the user hid that same version.</param>
    public AboutWindow(UpdateCoordinator? updates = null, Action? updateFound = null)
    {
        _updates = updates;
        _updateFound = updateFound;
        InitializeComponent();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);

        VersionText.Text = Text.Of(Strings.About_Version, Version);
        EnvironmentText.Text = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";
        CopyrightText.Text = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;
        SourceLink.NavigateUri = new Uri(RepositoryUrl);
        ReleasesLink.NavigateUri = new Uri(RepositoryUrl + "/releases");

        if (_updates is null || !BuildInfo.CanUpdate)
        {
            CheckUpdatesButton.IsEnabled = false;
            UpdateStatus.Text = Strings.About_DevBuild;
        }
    }

    /// <summary>
    /// The version the release tag stamped, as the executable reports it. A development build reads
    /// 1.0.0; the part after "+" is the commit it was built from, which is worth keeping in a report.
    /// </summary>
    private static string Version => BuildInfo.InformationalVersion;

    /// <summary>What a support conversation needs first, in English whatever the interface language:
    /// it is meant to be pasted into an issue, not read in the window.</summary>
    private static string Details() => string.Join(Environment.NewLine,
        $"RemoteDeck {Version}",
        RuntimeInformation.FrameworkDescription,
        $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
        $"UI culture: {CultureInfo.CurrentUICulture.Name}",
        $"Log: {ProbeLog.Path}");

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_updates is null)
        {
            return;
        }

        // One check at a time from here; the coordinator refuses a second one anyway (Busy).
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatus.Text = Strings.About_Checking;
        CheckOutcome outcome;
        try
        {
            outcome = await _updates.CheckAsync(manual: true);
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = BuildInfo.CanUpdate;
        }

        UpdateStatus.Text = outcome.Result switch
        {
            CheckResult.UpToDate => Strings.About_UpToDate,
            CheckResult.Available when _updates.Available is { } release => Text.Of(Strings.About_UpdateAvailable, release.Version),
            CheckResult.Failed => Text.Of(Strings.About_CheckFailed, outcome.Reason),
            CheckResult.DevBuild => Strings.About_DevBuild,
            CheckResult.Busy => Strings.About_UpdateBusy,
            _ => string.Empty,
        };
        if (outcome.Result == CheckResult.Available)
        {
            _updateFound?.Invoke();
        }
    }

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
