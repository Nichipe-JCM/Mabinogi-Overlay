using System.Windows;

namespace TestOverlay.App.Services;

public static class OverlayLayoutGeometry
{
    public static Rect CreatePreviewWindowBounds(
        double canvasLeft,
        double canvasTop,
        double canvasWidth,
        double canvasHeight,
        double headerHeight,
        double borderThickness,
        double minimumCanvasWidth,
        double minimumCanvasHeight)
    {
        var width = Math.Max(minimumCanvasWidth, canvasWidth);
        var height = Math.Max(minimumCanvasHeight, canvasHeight);
        return new Rect(
            canvasLeft - borderThickness,
            canvasTop - headerHeight - borderThickness,
            width + (borderThickness * 2),
            height + headerHeight + (borderThickness * 2));
    }

    public static Rect ReadCanvasBounds(
        double windowLeft,
        double windowTop,
        double windowWidth,
        double windowHeight,
        double headerHeight,
        double borderThickness,
        double minimumCanvasWidth,
        double minimumCanvasHeight)
    {
        return new Rect(
            windowLeft + borderThickness,
            windowTop + headerHeight + borderThickness,
            Math.Max(minimumCanvasWidth, windowWidth - (borderThickness * 2)),
            Math.Max(minimumCanvasHeight, windowHeight - headerHeight - (borderThickness * 2)));
    }

    public static double ClampSnappedCoordinate(double value, double maximum, double gridSize)
    {
        maximum = Math.Max(0, maximum);
        gridSize = Math.Max(1, gridSize);
        if (gridSize <= 1)
        {
            return Math.Clamp(value, 0, maximum);
        }

        var snapped = Math.Round(value / gridSize) * gridSize;
        var snappedMaximum = Math.Floor(maximum / gridSize) * gridSize;
        return Math.Clamp(snapped, 0, snappedMaximum);
    }

    public static double SnapUp(double value, double gridSize)
    {
        gridSize = Math.Max(1, gridSize);
        return Math.Ceiling(Math.Max(0, value) / gridSize) * gridSize;
    }

    public static Vector CalculateSnappedGroupDelta(
        IReadOnlyCollection<Rect> origins,
        Size canvasSize,
        Vector requestedDelta,
        double gridSize)
    {
        if (origins.Count == 0)
        {
            return default;
        }

        var bounds = origins.First();
        foreach (var origin in origins.Skip(1))
        {
            bounds.Union(origin);
        }

        var maximumLeft = Math.Max(0, canvasSize.Width - bounds.Width);
        var maximumTop = Math.Max(0, canvasSize.Height - bounds.Height);
        var targetLeft = ClampSnappedCoordinate(
            bounds.Left + requestedDelta.X,
            maximumLeft,
            gridSize);
        var targetTop = ClampSnappedCoordinate(
            bounds.Top + requestedDelta.Y,
            maximumTop,
            gridSize);
        return new Vector(targetLeft - bounds.Left, targetTop - bounds.Top);
    }

    public static Rect ResizeSlotFromTopLeft(Rect current, Size sourceSize, double scale)
    {
        var clampedScale = Math.Clamp(scale, 0.1, 10);
        return new Rect(
            current.X,
            current.Y,
            Math.Max(1, sourceSize.Width * clampedScale),
            Math.Max(1, sourceSize.Height * clampedScale));
    }
}
