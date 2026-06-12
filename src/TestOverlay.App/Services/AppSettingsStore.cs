using System.IO;
using System.Text.Json;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string SettingsPath { get; } = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public string DefaultProfileDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "save");

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings { ProfileDirectory = DefaultProfileDirectory };
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options)
                           ?? new AppSettings();
            settings.ProfileDirectory = NormalizeProfileDirectory(settings.ProfileDirectory);
            return settings;
        }
        catch
        {
            return new AppSettings { ProfileDirectory = DefaultProfileDirectory };
        }
    }

    public void Save(AppSettings settings)
    {
        settings.ProfileDirectory = NormalizeProfileDirectory(settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? AppContext.BaseDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }

    public string NormalizeProfileDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultProfileDirectory;
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
    }
}
