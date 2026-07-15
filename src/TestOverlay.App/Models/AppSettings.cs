namespace TestOverlay.App.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; }

    public string ProfileDirectory { get; set; } = string.Empty;

    public OverlayRenderMode OverlayRenderMode { get; set; } = OverlayRenderMode.GpuDxgi;

    public bool AutomaticRendererSelection { get; set; } = true;

    public CaptureBackend CaptureBackend { get; set; } = CaptureBackend.Wgc;

    public string Language { get; set; } = TestOverlay.App.Services.LocalizationService.English;

    public bool CompactModeEnabled { get; set; }

    public bool BuffIconsOnly { get; set; }
}

