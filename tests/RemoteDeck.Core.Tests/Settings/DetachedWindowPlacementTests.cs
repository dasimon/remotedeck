using RemoteDeck.Core.Settings;

namespace RemoteDeck.Core.Tests.Settings;

public sealed class DetachedWindowPlacementTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"remotedeck-detached-{Guid.NewGuid():N}", "settings.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Detached_placements_survive_a_save_and_load()
    {
        var store = new SettingsStore(_path);
        var settings = store.Load();
        settings.DetachedWindows["42"] = new DetachedWindowPlacement(1920, 0, 1280, 800, true);

        store.Save(settings);
        var reloaded = store.Load();

        Assert.Single(reloaded.DetachedWindows);
        Assert.Equal(new DetachedWindowPlacement(1920, 0, 1280, 800, true), reloaded.DetachedWindows["42"]);
    }

    [Fact]
    public void A_settings_file_without_the_section_loads_with_an_empty_map()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """{ "paneWidth": 320 }""");

        var settings = new SettingsStore(_path).Load();

        Assert.Equal(320, settings.PaneWidth);
        Assert.NotNull(settings.DetachedWindows);
        Assert.Empty(settings.DetachedWindows);
    }
}
