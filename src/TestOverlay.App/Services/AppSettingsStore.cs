using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    static AppSettingsStore()
    {
        Options.Converters.Add(new JsonStringEnumConverter());
    }

    public string SettingsPath { get; } = Path.Combine(AppContext.BaseDirectory, "settings.json");

    public string DefaultProfileDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "save");

    public bool LastLoadRecoveredFromBackup { get; private set; }

    public Exception? LastLoadException { get; private set; }

    public AppSettings Load()
    {
        try
        {
            var result = AtomicJsonFile.Load<AppSettings>(SettingsPath, Options);
            var settings = result?.Value ?? new AppSettings();
            AppSettingsMigration.Apply(settings);
            Normalize(settings);
            LastLoadRecoveredFromBackup = result?.RecoveredFromBackup == true;
            LastLoadException = result?.PrimaryException;
            return settings;
        }
        catch (Exception exception)
        {
            LastLoadRecoveredFromBackup = false;
            LastLoadException = exception;
            return new AppSettings { ProfileDirectory = DefaultProfileDirectory };
        }
    }

    public void Save(AppSettings settings)
    {
        AppSettingsMigration.Apply(settings);
        Normalize(settings);
        AtomicJsonFile.Save(SettingsPath, settings, Options);
    }

    private void Normalize(AppSettings settings)
    {
        settings.ProfileDirectory = NormalizeProfileDirectory(settings.ProfileDirectory);
        settings.Language = LocalizationService.NormalizeLanguage(settings.Language);
        var requestedRenderMode = settings.AutomaticRendererSelection
            ? RuntimeConfigurationPolicy.ResolveAutomaticRenderer(settings.CaptureBackend)
            : settings.OverlayRenderMode;
        var runtime = RuntimeConfigurationPolicy.Normalize(
            requestedRenderMode,
            settings.CaptureBackend,
            RuntimeSelectionPreference.CaptureBackend);
        settings.OverlayRenderMode = runtime.RenderMode;
        settings.CaptureBackend = runtime.CaptureBackend;
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
