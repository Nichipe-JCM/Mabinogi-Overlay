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

    [Fact]
    public void CalculateSnappedGroupDelta_PreservesEveryRelativeOffset()
    {
        var origins = new[]
        {
            new Rect(13, 17, 40, 40),
            new Rect(61, 17, 40, 40),
            new Rect(13, 65, 40, 40)
        };

        var delta = OverlayLayoutGeometry.CalculateSnappedGroupDelta(
            origins,
            new Size(400, 300),
            new Vector(14, 9),
            10);

        Assert.Equal(new Vector(17, 13), delta);
        Assert.Equal(48, (origins[1].X + delta.X) - (origins[0].X + delta.X));
        Assert.Equal(48, (origins[2].Y + delta.Y) - (origins[0].Y + delta.Y));
        Assert.Equal(0, (origins[0].X + delta.X) % 10);
        Assert.Equal(0, (origins[0].Y + delta.Y) % 10);
    }

    [Fact]
    public void CalculateSnappedGroupDelta_ClampsWholeGroupAtCanvasEdge()
    {
        var origins = new[]
        {
            new Rect(20, 20, 40, 40),
            new Rect(70, 20, 40, 40)
        };

        var delta = OverlayLayoutGeometry.CalculateSnappedGroupDelta(
            origins,
            new Size(123, 100),
            new Vector(500, 0),
            10);

        Assert.Equal(10, delta.X);
        Assert.Equal(120, origins[1].Right + delta.X);
    }

    [Theory]
    [InlineData(8, 10, 10)]
    [InlineData(40.1, 10, 50)]
    [InlineData(40, 10, 40)]
    public void SnapUp_AlwaysReturnsNextGridLine(double value, double gridSize, double expected)
    {
        Assert.Equal(expected, OverlayLayoutGeometry.SnapUp(value, gridSize));
    }
}
