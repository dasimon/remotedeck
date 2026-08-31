using System.Globalization;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Data;
using RemoteDeck.Core.Security;

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

    /// <summary>The local database, opened and migrated at startup (spec §4). Null until <see cref="OnStartup"/> has run.</summary>
    public SqliteDatabase? Database { get; private set; }

    /// <summary>True only once the database has been created and migrated without error; the shell must not read it otherwise.</summary>
    public bool DatabaseReady { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
            Database.EnsureCreated();
            DatabaseReady = true;
            ProbeLog.Write("startup", $"Database ready at {Database.Path}, schema v{SchemaMigrator.CurrentVersion}");
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
        }
        services.AddSingleton<ICredentialVault, DpapiCredentialVault>();
        Services = services.BuildServiceProvider();
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
    /// setting to change it — spec §9. REMOTEDECK_UI_CULTURE overrides it for verification only: it is
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
