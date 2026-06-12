namespace TestOverlay.App.Models;

public sealed class AppSettings
{
    public string ProfileDirectory { get; set; } = string.Empty;

    public OverlayRenderMode OverlayRenderMode { get; set; } = OverlayRenderMode.GpuDxgi;

    public CaptureBackend CaptureBackend { get; set; } = CaptureBackend.Wgc;
}
