using System.Windows;
using RemoteDeck.App.Services;
using RemoteDeck.Core.Data;

namespace RemoteDeck.App;

/// <summary>Application entry point. Logs a startup marker so the lot-0 probe log always
/// starts with a run boundary, opens and migrates the local database, then hands over to
/// the <c>StartupUri</c> shell window.</summary>
// System.Windows.Application is qualified on purpose: UseWindowsForms puts
// System.Windows.Forms.Application in scope too, which makes the short name ambiguous.
public partial class App : System.Windows.Application
{
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
    }
}
