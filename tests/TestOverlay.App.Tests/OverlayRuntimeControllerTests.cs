using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class OverlayRuntimeControllerTests
{
    [Fact]
    public void NormalizeOverlayPosition_RecoversFromGapBetweenStaggeredMonitors()
    {
        System.Windows.Rect[] monitors = [new(0, 0, 1920, 1080), new(1920, 1080, 1920, 1080)];
        var result = OverlayRuntimeController.NormalizeOverlayPosition(2200, 100, 200, 200, monitors);
        var window = new System.Windows.Rect(result.Left, result.Top, 200, 200);
        Assert.Contains(monitors, monitor => monitor.IntersectsWith(window));
        Assert.NotEqual((2200d, 100d), result);
    }

    [Theory]
    [InlineData(30, 33)]
    [InlineData(60, 17)]
    [InlineData(119, 8)]
    [InlineData(144, 7)]
    public void RefreshIntervalFromFps_CoercesToSupportedRates(int fps, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, OverlayRuntimeController.RefreshIntervalFromFps(fps));
    }

    [Fact]
    public void NormalizeOverlayPosition_PreservesNegativeCoordinatesOnVisibleSecondaryMonitor()
    {
        var result = OverlayRuntimeController.NormalizeOverlayPosition(
            left: -1600,
            top: 100,
            width: 720,
            height: 320,
            virtualLeft: -1920,
            virtualTop: 0,
            virtualWidth: 3840,
            virtualHeight: 1080);

        Assert.Equal(-1600, result.Left);
        Assert.Equal(100, result.Top);
    }

    [Fact]
    public void NormalizeOverlayPosition_RecoversWindowCompletelyOutsideVirtualDesktop()
    {
        var result = OverlayRuntimeController.NormalizeOverlayPosition(
            left: 9000,
            top: -5000,
            width: 720,
            height: 320,
            virtualLeft: -1920,
            virtualTop: 0,
            virtualWidth: 3840,
            virtualHeight: 1080);

        Assert.Equal(1200, result.Left);
        Assert.Equal(0, result.Top);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, false)]
    [InlineData(1920, 1080, 2560, 1440, true)]
    [InlineData(1920, 1080, 0, 1440, false)]
    public void FrameSizeChanged_RequiresPositiveDifferentDimensions(
        int currentWidth,
        int currentHeight,
        int nextWidth,
        int nextHeight,
        bool expected)
    {
        Assert.Equal(
            expected,
            WgcCaptureService.FrameSizeChanged(currentWidth, currentHeight, nextWidth, nextHeight));
    }
}
