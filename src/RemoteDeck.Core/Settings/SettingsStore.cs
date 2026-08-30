using System.Text.Json;

namespace RemoteDeck.Core.Settings;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON. Losing this file only costs window geometry,
/// so <see cref="Load"/> never throws: anything unreadable falls back to defaults.
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>%APPDATA%\RemoteDeck\settings.json — beside the database, roaming with the profile.</summary>
    public static string DefaultPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDeck", "settings.json");

    /// <summary>Returns the stored settings, or defaults when the file is missing, unreadable or corrupt.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings atomically: a temporary file, then a single replacing move.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, path, overwrite: true);
    }
}
