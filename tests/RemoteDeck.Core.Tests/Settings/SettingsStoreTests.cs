using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"remotedeck-settings-{Guid.NewGuid():N}");

    private string File_ => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Load_returns_defaults_when_the_file_is_missing()
    {
        var settings = new SettingsStore(File_).Load();

        Assert.Equal(300, settings.PaneWidth);
        Assert.False(settings.PaneCollapsed);
        Assert.Null(settings.WindowLeft);
        Assert.Null(settings.LastConnectionId);
    }

    [Fact]
    public void Save_then_load_round_trips_every_property()
    {
        var store = new SettingsStore(File_);
        var saved = new AppSettings
        {
            PaneWidth = 412.5,
            PaneCollapsed = true,
            WindowLeft = 10.25,
            WindowTop = 20.5,
            WindowWidth = 1280,
            WindowHeight = 720,
            WindowMaximized = true,
            LastConnectionId = 42,
        };

        store.Save(saved);
        var loaded = store.Load();

        Assert.Equal(412.5, loaded.PaneWidth);
        Assert.True(loaded.PaneCollapsed);
        Assert.Equal(10.25, loaded.WindowLeft);
        Assert.Equal(20.5, loaded.WindowTop);
        Assert.Equal(1280, loaded.WindowWidth);
        Assert.Equal(720, loaded.WindowHeight);
        Assert.True(loaded.WindowMaximized);
        Assert.Equal(42, loaded.LastConnectionId);
    }

    [Fact]
    public void Load_returns_defaults_when_the_file_is_not_valid_json()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, "{ not json");

        var settings = new SettingsStore(File_).Load();

        Assert.Equal(300, settings.PaneWidth);
    }

    [Fact]
    public void Save_creates_the_parent_directory()
    {
        var nested = Path.Combine(_dir, "nested", "settings.json");

        new SettingsStore(nested).Save(new AppSettings());

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void RestoreLastSession_defaults_to_false()
    {
        // Ouvrir RemoteDeck ne doit se connecter à rien tant que l'utilisateur ne l'a pas demandé
        // (spec espaces §7).
        Assert.False(new AppSettings().RestoreLastSession);
    }

    [Fact]
    public void LastSession_round_trips()
    {
        var store = new SettingsStore(File_);
        store.Save(new AppSettings
        {
            RestoreLastSession = true,
            LastSession =
            [
                new LastSessionEntry { ConnectionId = 7, Ordinal = 0, Detached = false },
                new LastSessionEntry
                {
                    ConnectionId = 9,
                    Ordinal = 1,
                    Detached = true,
                    Placement = new DetachedWindowPlacement(10, 20, 1280, 800, true),
                },
            ],
        });

        var read = store.Load();

        Assert.True(read.RestoreLastSession);
        Assert.Equal(2, read.LastSession.Count);
        Assert.Equal(7, read.LastSession[0].ConnectionId);
        Assert.Null(read.LastSession[0].Placement);
        Assert.True(read.LastSession[1].Detached);
        Assert.Equal(new DetachedWindowPlacement(10, 20, 1280, 800, true), read.LastSession[1].Placement);
    }

    [Fact]
    public void LastSession_is_never_null_even_when_the_file_says_null()
    {
        // Le fichier est éditable à la main : "lastSession": null écrase l'initialiseur de propriété.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, """{ "lastSession": null, "detachedWindows": null }""");

        var read = new SettingsStore(File_).Load();

        Assert.NotNull(read.LastSession);
        Assert.Empty(read.LastSession);
        Assert.NotNull(read.DetachedWindows);
    }
}
