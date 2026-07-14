using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class AppSettingsMigrationTests
{
    [Theory]
    [InlineData(CaptureBackend.Wgc, OverlayRenderMode.GpuDxgi)]
    [InlineData(CaptureBackend.DxgiDesktopDuplication, OverlayRenderMode.CpuComposited)]
    [InlineData(CaptureBackend.GdiBitBlt, OverlayRenderMode.CpuComposited)]
    public void LegacyRecommendedPair_MigratesToAutomaticSelection(
        CaptureBackend captureBackend,
        OverlayRenderMode renderMode)
    {
        var settings = new AppSettings
        {
            SchemaVersion = 0,
            CaptureBackend = captureBackend,
            OverlayRenderMode = renderMode,
            AutomaticRendererSelection = false
        };

        AppSettingsMigration.Apply(settings);

        Assert.Equal(AppSettingsMigration.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.True(settings.AutomaticRendererSelection);
    }

    [Fact]
    public void LegacyCustomPair_PreservesManualRendererSelection()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 0,
            CaptureBackend = CaptureBackend.Wgc,
            OverlayRenderMode = OverlayRenderMode.CpuWpf,
            AutomaticRendererSelection = true
        };

        AppSettingsMigration.Apply(settings);

        Assert.False(settings.AutomaticRendererSelection);
        Assert.Equal(OverlayRenderMode.CpuWpf, settings.OverlayRenderMode);
    }

    [Fact]
    public void LegacyInvalidValues_RecoverToAutomaticWgcGpuDefaults()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 0,
            CaptureBackend = (CaptureBackend)999,
            OverlayRenderMode = (OverlayRenderMode)999
        };

        AppSettingsMigration.Apply(settings);

        Assert.Equal(CaptureBackend.Wgc, settings.CaptureBackend);
        Assert.Equal(OverlayRenderMode.GpuDxgi, settings.OverlayRenderMode);
        Assert.True(settings.AutomaticRendererSelection);
    }

    [Fact]
    public void SchemaOne_PreservesExplicitAutomaticSelection()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 1,
            CaptureBackend = CaptureBackend.Wgc,
            OverlayRenderMode = OverlayRenderMode.GpuDxgi,
            AutomaticRendererSelection = false
        };

        AppSettingsMigration.Apply(settings);

        Assert.Equal(AppSettingsMigration.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.False(settings.AutomaticRendererSelection);
    }

    [Fact]
    public void Current_schema_preserves_compact_mode_preference()
    {
        var settings = new AppSettings
        {
            SchemaVersion = AppSettingsMigration.CurrentSchemaVersion,
            CompactModeEnabled = true
        };

        AppSettingsMigration.Apply(settings);

        Assert.True(settings.CompactModeEnabled);
    }
}
