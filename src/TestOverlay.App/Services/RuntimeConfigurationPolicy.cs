using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public static class RuntimeConfigurationPolicy
{
    public static OverlayRenderMode ResolveAutomaticRenderer(CaptureBackend captureBackend) =>
        captureBackend switch
        {
            CaptureBackend.Wgc => OverlayRenderMode.GpuDxgi,
            CaptureBackend.DxgiDesktopDuplication or CaptureBackend.GdiBitBlt =>
                OverlayRenderMode.CpuComposited,
            _ => OverlayRenderMode.GpuDxgi
        };

    public static RuntimeConfiguration Normalize(
        OverlayRenderMode renderMode,
        CaptureBackend captureBackend,
        RuntimeSelectionPreference preference = RuntimeSelectionPreference.Renderer)
    {
        if (!Enum.IsDefined(renderMode))
        {
            renderMode = OverlayRenderMode.GpuDxgi;
        }

        if (!Enum.IsDefined(captureBackend))
        {
            captureBackend = CaptureBackend.Wgc;
        }

        if (renderMode == OverlayRenderMode.GpuDxgi && captureBackend != CaptureBackend.Wgc)
        {
            if (preference == RuntimeSelectionPreference.Renderer)
            {
                captureBackend = CaptureBackend.Wgc;
            }
            else
            {
                renderMode = OverlayRenderMode.CpuComposited;
            }
        }

        return new RuntimeConfiguration(renderMode, captureBackend);
    }
}

public enum RuntimeSelectionPreference
{
    Renderer,
    CaptureBackend
}

public sealed record RuntimeConfiguration(
    OverlayRenderMode RenderMode,
    CaptureBackend CaptureBackend);
