using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public static class AppSettingsMigration
{
    public const int CurrentSchemaVersion = 3;

    public static void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion >= CurrentSchemaVersion)
        {
            return;
        }

        if (settings.SchemaVersion < 1)
        {
            var captureBackend = Enum.IsDefined(settings.CaptureBackend)
                ? settings.CaptureBackend
                : CaptureBackend.Wgc;
            var renderMode = Enum.IsDefined(settings.OverlayRenderMode)
                ? settings.OverlayRenderMode
                : OverlayRenderMode.GpuDxgi;

            settings.CaptureBackend = captureBackend;
            settings.OverlayRenderMode = renderMode;
            settings.AutomaticRendererSelection =
                renderMode == RuntimeConfigurationPolicy.ResolveAutomaticRenderer(captureBackend);
        }

        if (settings.SchemaVersion < 3 && string.IsNullOrWhiteSpace(settings.ActiveProfileName))
        {
            settings.ActiveProfileName = "default";
        }

        settings.SchemaVersion = CurrentSchemaVersion;
    }
}
