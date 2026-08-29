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
}
