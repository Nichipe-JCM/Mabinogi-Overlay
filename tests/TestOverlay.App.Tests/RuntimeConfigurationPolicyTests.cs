using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class RuntimeConfigurationPolicyTests
{
    [Fact]
    public void RendererPreference_PairsGpuRendererWithWgc()
    {
        var result = RuntimeConfigurationPolicy.Normalize(
            OverlayRenderMode.GpuDxgi,
            CaptureBackend.DxgiDesktopDuplication,
            RuntimeSelectionPreference.Renderer);

        Assert.Equal(OverlayRenderMode.GpuDxgi, result.RenderMode);
        Assert.Equal(CaptureBackend.Wgc, result.CaptureBackend);
    }

    [Fact]
    public void CapturePreference_PairsMonitorCaptureWithCpuCompositedRenderer()
    {
        var result = RuntimeConfigurationPolicy.Normalize(
            OverlayRenderMode.GpuDxgi,
            CaptureBackend.DxgiDesktopDuplication,
            RuntimeSelectionPreference.CaptureBackend);

        Assert.Equal(OverlayRenderMode.CpuComposited, result.RenderMode);
        Assert.Equal(CaptureBackend.DxgiDesktopDuplication, result.CaptureBackend);
    }

    [Fact]
    public void UnknownEnumValues_FallBackToSupportedDefaults()
    {
        var result = RuntimeConfigurationPolicy.Normalize(
            (OverlayRenderMode)999,
            (CaptureBackend)999);

        Assert.Equal(OverlayRenderMode.GpuDxgi, result.RenderMode);
        Assert.Equal(CaptureBackend.Wgc, result.CaptureBackend);
    }
}
