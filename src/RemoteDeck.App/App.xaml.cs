using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Security;
using RemoteDeck.Core.Updates;

namespace RemoteDeck.App;

/// <summary>Application entry point. Logs a startup marker so the lot-0 probe log always
/// starts with a run boundary, opens and migrates the local database, builds the service
/// container, then hands over to the <c>StartupUri</c> shell window.</summary>
// System.Windows.Application is qualified on purpose: UseWindowsForms puts
// System.Windows.Forms.Application in scope too, which makes the short name ambiguous.
public partial class App : System.Windows.Application
{
    /// <summary>The running application, typed. Hides the base <c>Current</c>, which is typed as <see cref="System.Windows.Application"/>.</summary>
    public static new App Current => (App)System.Windows.Application.Current;

    /// <summary>Composition root. Repositories and the vault are stateless singletons.</summary>
    public IServiceProvider Services { get; private set; } = new ServiceCollection().BuildServiceProvider();

    /// <summary>The local database, opened and migrated at startup. Null until <see cref="OnStartup"/> has run.</summary>
    public SqliteDatabase? Database { get; private set; }

    /// <summary>
    /// Where the pre-migration copy was to be written when writing it failed, so the database was
    /// left at its old version and not opened. Null otherwise. The shell names it in its notice.
    /// </summary>
    public string? DatabaseBackupFailure { get; private set; }

    /// <summary>True only once the database has been created and migrated without error; the shell must not read it otherwise.</summary>
    public bool DatabaseReady { get; private set; }

    /// <summary>The executable to start once the application has exited; see <see cref="RestartAfterExit"/>.</summary>
    private string? _restartPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LogCrashes();

        // The elevated half of an update: copy, check, swap, exit. Before everything else, so that
        // this administrator process never opens the database, a theme watcher or a window.
        if (e.Args is [ApplyUpdateArguments.Verb, ..])
        {
            ApplyUpdateAndExit(e.Args);
            return;
        }

        // What a previous update left: the old executable, a staged copy, the downloads. Only the
        // update work folder, never the RemoteDeck data folder that contains it. In a protected folder
        // the old executable is refused here; the elevated step had Windows delete it at its restart.
        if (Environment.ProcessPath is { } exe)
        {
            UpdateSwap.CleanUp(exe, UpdateClient.WorkDirectory);
        }

        // Detached session windows are deliberately owner-less — an owned window is always painted
        // above its owner and minimises with it, which is the opposite of a session torn onto a
        // second monitor. The default OnLastWindowClose would therefore keep the process alive after
        // the shell is gone, with windows the user has no way back to. The shell is the application:
        // when it closes, so does RemoteDeck. StartupUri makes it MainWindow.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        ApplyOverriddenCulture();
        ProbeLog.Write("startup", $"RemoteDeck starting, log at {ProbeLog.Path}");
        try
        {
            // Built here rather than in a field initialiser: a throwing initialiser would kill the process before OnStartup runs.
            Database = new SqliteDatabase(SqliteDatabase.DefaultPath());
            if (Database.EnsureCreated() is { } backup)
            {
                ProbeLog.Write("startup", $"Database upgraded; the previous version was copied to {backup}");
            }

            DatabaseReady = true;
            ProbeLog.Write("startup", $"Database ready at {Database.Path}, schema v{SchemaMigrator.CurrentVersion}");
        }
        catch (DatabaseBackupException ex)
        {
            // Not migrated, deliberately: without the copy the upgrade would be a one-way door, and the
            // build the user came from refuses the new schema. The shell still opens for RDP-only use.
            DatabaseBackupFailure = ex.BackupPath;
            ProbeLog.Write("startup", $"Database not upgraded: {ex.Message} ({ex.InnerException?.GetType().Name})");
        }
        catch (SchemaTooNewException ex)
        {
            // Refusing to touch a newer database is the safe outcome; the shell still opens for RDP-only use.
            ProbeLog.Write("startup", $"Database not opened: {ex.Message}");
        }
        catch (Exception ex)
        {
            ProbeLog.Write("startup", $"Database initialisation failed: {ex.GetType().Name}: {ex.Message}");
        }

        // The repositories are registered only when the database is usable: GetService<CredentialRepository>()
        // then returns null and the UI disables the matching features instead of crashing.
        var services = new ServiceCollection();
        if (Database is not null && DatabaseReady)
        {
            services.AddSingleton(Database);
            services.AddSingleton<CredentialRepository>();
            services.AddSingleton<ConnectionRepository>();
            services.AddSingleton<WorkspaceRepository>();
        }
        services.AddSingleton<ICredentialVault, DpapiCredentialVault>();
        Services = services.BuildServiceProvider();
    }

    /// <summary>
    /// Starts <paramref name="exePath"/> once this instance has exited: from <see cref="System.Windows.Application.Exit"/>,
    /// that is after the main window's close has written the settings and the last session, which the
    /// new instance then reads. Not elevated: it is started as the user running this instance.
    /// </summary>
    public void RestartAfterExit(string exePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        if (_restartPath is null)
        {
            Exit += OnExitRestart;
        }

        _restartPath = exePath;
        ProbeLog.Write("update", $"restart scheduled on exit: {exePath}");
    }

    private void OnExitRestart(object sender, ExitEventArgs e)
    {
        if (_restartPath is not { } path)
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
            ProbeLog.Write("update", $"restarted {path}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The new executable is in place; the user starts it by hand.
            ProbeLog.Write("update", $"restart of {path} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The <c>--apply-update "&lt;source&gt;" &lt;sha256&gt;</c> mode, run elevated by the installed
    /// executable: swaps <paramref name="args"/>' source in place of this very executable (never a path
    /// from the command line) and ends the process with <see cref="UpdateInstaller.ExitCodeOf"/>. A malformed
    /// command line exits too, with <see cref="UpdateInstaller.ExitBadArguments"/>, rather than
    /// opening an elevated window.
    /// </summary>
    private static void ApplyUpdateAndExit(string[] args)
    {
        // A file of its own: the RemoteDeck waiting for this process has the main log open.
        ProbeLog.UseApplyUpdateLog();
        ProbeLog.Write("startup", $"RemoteDeck starting in {ApplyUpdateArguments.Verb} mode, log at {ProbeLog.Path}");
        int code;
        if (!ApplyUpdateArguments.TryParse(args, out var source, out var sha256))
        {
            ProbeLog.Write("update", $"--apply-update: malformed command line ({args.Length} arguments)");
            code = UpdateInstaller.ExitBadArguments;
        }
        else if (Environment.ProcessPath is not { } target)
        {
            ProbeLog.Write("update", "--apply-update: the executable's own path is unknown");
            code = UpdateInstaller.ExitFailed;
        }
        else
        {
            ProbeLog.Write("update", $"--apply-update: installing {source} as {target}");
            var result = UpdateInstaller.SwapAndLog(source, sha256, target);
            if (result == SwapResult.Done)
            {
                // The user-level clean-up at the next launch cannot delete it in a protected folder.
                UpdateInstaller.DeleteOldAtReboot(target);
            }

            code = UpdateInstaller.ExitCodeOf(result);
        }

        // Environment.Exit rather than Shutdown: StartupUri cannot be cleared (its setter refuses
        // null), and nothing documents that Shutdown called from OnStartup keeps WPF from loading the
        // shell window once this method returns. Ending the process here means no window is ever built.
        Environment.Exit(code);
    }

    /// <summary>
    /// Makes sure a crash leaves a line in the probe log before the process dies. Nothing here
    /// marks an exception as handled: an application that swallows what it did not expect keeps
    /// running in a state nobody designed, and a crash with evidence is a bug that gets fixed.
    /// </summary>
    /// <remarks>
    /// Until this existed there was no handler at all, so an exception escaping any event handler
    /// — or any <c>async void</c> after its first <c>await</c>, which is what every UI event
    /// handler that connects a session is — ended the process with nothing in the log. The three
    /// sources are distinct: the WPF dispatcher for the UI thread, the AppDomain for every other
    /// thread, and the task scheduler for a faulted task nobody awaited (which no longer crashes
    /// the process, and would otherwise vanish entirely).
    /// </remarks>
    private void LogCrashes()
    {
        DispatcherUnhandledException += (_, args) => Record("UI thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Record("background thread", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => Record("unobserved task", args.Exception);

        static void Record(string where, Exception? ex)
        {
            try
            {
                ProbeLog.Write("crash", ex is null
                    ? $"{where}: unhandled non-exception object"
                    : $"{where}: {ex.GetType().FullName} 0x{ex.HResult:X8} {ex.Message}{Environment.NewLine}{ex}");
            }
            catch
            {
                // The logger itself failed while the process is already going down; there is
                // nowhere left to report to, and throwing here would only replace the real error.
            }
        }
    }

    /// <summary>Where the design tokens live. Also the key this handler matches on.</summary>
    private const string ThemeSheetUri = "pack://application:,,,/RemoteDeck;component/Resources/Theme.xaml";

    /// <summary>
    /// Re-reads <c>Resources/Theme.xaml</c> whenever the theme changes, as a brand-new dictionary.
    ///
    /// Measured, not assumed (task-1 probe): WPF invalidates a <c>DynamicResource</c> only for the
    /// keys of the dictionary that actually changed. When WPF-UI swaps its own theme dictionary, the
    /// <c>Rd*</c> keys are not among them — so a view keeps the token brush it resolved at startup,
    /// and that brush keeps the colour it resolved then too, expression or not. The tokens would
    /// freeze the application in the theme it launched in, silently.
    ///
    /// Merging a fresh instance fixes both halves at once: its brushes resolve their
    /// <c>DynamicResource</c> colours against the theme now in place, and replacing a merged
    /// dictionary is itself a change over the <c>Rd*</c> keys, which is what invalidates the views
    /// reading them.
    /// </summary>
    private static void OnApplicationThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        // Current is null while the application is shutting down, and the watcher may still fire.
        var merged = System.Windows.Application.Current?.Resources.MergedDictionaries;
        if (merged is null)
        {
            return;
        }

        for (var i = 0; i < merged.Count; i++)
        {
            if (merged[i].Source?.OriginalString == ThemeSheetUri)
            {
                merged[i] = new ResourceDictionary { Source = new Uri(ThemeSheetUri, UriKind.Absolute) };
                return;
            }
        }
    }

    /// <summary>
    /// The UI language is Windows' own (<see cref="CultureInfo.CurrentUICulture"/>) and v1 offers no
    /// setting to change it. REMOTEDECK_UI_CULTURE overrides it for verification only: it is
    /// how the French pass is checked without changing the machine's language, and it is read here,
    /// before any window exists, because a <c>{x:Static}</c> binding reads its resource when the window
    /// is loaded and never again.
    /// </summary>
    private static void ApplyOverriddenCulture()
    {
        var name = Environment.GetEnvironmentVariable("REMOTEDECK_UI_CULTURE");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            ProbeLog.Write("startup", $"REMOTEDECK_UI_CULTURE={culture.Name}: UI culture overridden");
        }
        catch (CultureNotFoundException)
        {
            // A typo in the variable must not cost the launch; the system culture stands.
            ProbeLog.Write("startup", $"REMOTEDECK_UI_CULTURE=\"{name}\" is not a known culture; keeping the system one");
        }
    }
}
