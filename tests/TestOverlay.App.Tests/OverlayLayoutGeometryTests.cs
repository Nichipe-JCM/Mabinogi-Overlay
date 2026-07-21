using System.Windows;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class OverlayLayoutGeometryTests
{
    [Fact]
    public void PreviewWindowBounds_RoundTrip_PreservesLogical4KScaledCanvas()
    {
        const double logicalWidthAt150Percent = 2560;
        const double logicalHeightAt150Percent = 1440;
        var window = OverlayLayoutGeometry.CreatePreviewWindowBounds(
            0,
            0,
            logicalWidthAt150Percent,
            logicalHeightAt150Percent,
            headerHeight: 34,
            borderThickness: 2,
            minimumCanvasWidth: 120,
            minimumCanvasHeight: 80);

        var canvas = OverlayLayoutGeometry.ReadCanvasBounds(
            window.X,
            window.Y,
            window.Width,
            window.Height,
            headerHeight: 34,
            borderThickness: 2,
            minimumCanvasWidth: 120,
            minimumCanvasHeight: 80);

        Assert.Equal(new Rect(0, 0, logicalWidthAt150Percent, logicalHeightAt150Percent), canvas);
    }

    [Fact]
    public void ClampSnappedCoordinate_AtCanvasEdge_RemainsOnGrid()
    {
        var coordinate = OverlayLayoutGeometry.ClampSnappedCoordinate(667, 667, 10);

        Assert.Equal(660, coordinate);
    }

    [Fact]
    public void ResizeSlotFromTopLeft_DoesNotMoveGridAnchor()
    {
        var resized = OverlayLayoutGeometry.ResizeSlotFromTopLeft(
            new Rect(120, 240, 48, 48),
            new Size(32, 32),
            0.9);

        Assert.Equal(120, resized.X);
        Assert.Equal(240, resized.Y);
        Assert.Equal(28.8, resized.Width, 3);
        Assert.Equal(28.8, resized.Height, 3);
    }
}
