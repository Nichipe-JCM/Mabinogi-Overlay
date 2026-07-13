using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class OverlayRuntimeControllerTests
{
    [Theory]
    [InlineData(30, 33)]
    [InlineData(60, 17)]
    [InlineData(119, 8)]
    [InlineData(144, 7)]
    public void RefreshIntervalFromFps_CoercesToSupportedRates(int fps, int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds, OverlayRuntimeController.RefreshIntervalFromFps(fps));
    }
}
