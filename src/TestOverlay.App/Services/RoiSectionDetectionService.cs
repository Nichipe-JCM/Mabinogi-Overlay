using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class RoiSectionDetectionService
{
    private const int MaxAnchorCandidates = 10;
    private const int MaxSmallGap = 30;
    private const int MaxLargeGap = 80;

    public SectionDetectionResult? Detect(
        BitmapSource source,
        Rect roi,
        QuickslotSectionPatternKind patternKind,
        int expectedSlotWidth,
        int expectedSlotHeight)
    {
        var bitmap = EnsureBgra32(source);
        var imageRect = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        roi.Intersect(imageRect);
        if (roi.IsEmpty || roi.Width < 24 || roi.Height < 24)
        {
            return null;
        }

        var edge = EdgeImage.From(bitmap);
        var pattern = PatternSpec.From(patternKind);
        var anchors = FindTopLeftSlotCandidates(
                edge,
                roi,
                Math.Clamp(expectedSlotWidth, 1, 300),
                Math.Clamp(expectedSlotHeight, 1, 300))
            .ToList();
        if (anchors.Count == 0)
        {
            return null;
        }

        PatternFit? best = null;
        foreach (var anchor in anchors)
        {
            foreach (var fit in EnumerateFits(edge, roi, pattern, patternKind, anchor))
            {
                if (best is null || fit.Score > best.Score)
                {
                    best = fit;
                }
            }
        }

        if (best is null)
        {
            return null;
        }

        return new SectionDetectionResult(
            patternKind,
            best.Slots,
            best.SmallGapX,
            best.SmallGapY,
            best.LargeGap,
            best.Score);
    }

    private static IEnumerable<SlotAnchor> FindTopLeftSlotCandidates(
        EdgeImage edge,
        Rect roi,
        int expectedSlotWidth,
        int expectedSlotHeight)
    {
        var candidates = new List<SlotAnchor>();
        var minWidth = Math.Max(12, expectedSlotWidth - 8);
        var maxWidth = Math.Min(300, expectedSlotWidth + 10);
        var minHeight = Math.Max(12, expectedSlotHeight - 8);
        var maxHeight = Math.Min(300, expectedSlotHeight + 10);
        var left = (int)Math.Floor(roi.Left);
        var top = (int)Math.Floor(roi.Top);
        var right = (int)Math.Ceiling(roi.Right);
        var bottom = (int)Math.Ceiling(roi.Bottom);

        for (var slotWidth = minWidth; slotWidth <= maxWidth; slotWidth++)
        {
            for (var slotHeight = minHeight; slotHeight <= maxHeight; slotHeight++)
            {
                for (var y = top; y <= bottom - slotHeight; y += 2)
                {
                    for (var x = left; x <= right - slotWidth; x += 2)
                    {
                        var rect = new Rect(x, y, slotWidth, slotHeight);
                        var edgeScore = edge.ScoreSlotBorder(rect);
                        if (edgeScore < 10)
                        {
                            continue;
                        }

                        var upperLeftBias = ((x - roi.Left) * 0.04) + ((y - roi.Top) * 0.07);
                        candidates.Add(new SlotAnchor(rect, edgeScore - upperLeftBias));
                    }
                }

                if (candidates.Count > 5000)
                {
                    candidates = candidates
                        .OrderByDescending(candidate => candidate.Score)
                        .Take(1200)
                        .ToList();
                }
            }
        }

        return NonMaximumSuppress(candidates.OrderByDescending(candidate => candidate.Score).Take(1200), 0.35)
            .Take(MaxAnchorCandidates);
    }

    private static IEnumerable<PatternFit> EnumerateFits(
        EdgeImage edge,
        Rect roi,
        PatternSpec pattern,
        QuickslotSectionPatternKind patternKind,
        SlotAnchor anchor)
    {
        var maxLargeGap = patternKind == QuickslotSectionPatternKind.TopGrouped ? MaxLargeGap : 0;
        for (var smallGapX = 0; smallGapX <= MaxSmallGap; smallGapX++)
        {
            for (var smallGapY = 0; smallGapY <= MaxSmallGap; smallGapY++)
            {
                for (var largeGap = 0; largeGap <= maxLargeGap; largeGap++)
                {
                    var slots = BuildPatternSlots(anchor.Rect, pattern, smallGapX, smallGapY, largeGap).ToList();
                    if (slots.Any(slot => !ContainsWithTolerance(roi, slot, 2)))
                    {
                        continue;
                    }

                    var score = ScorePattern(edge, roi, slots);
                    yield return new PatternFit(slots, smallGapX, smallGapY, largeGap, score);
                }
            }
        }
    }

    private static IEnumerable<Rect> BuildPatternSlots(
        Rect seed,
        PatternSpec pattern,
        double smallGapX,
        double smallGapY,
        double largeGap)
    {
        var innerPitchX = seed.Width + smallGapX;
        var innerPitchY = seed.Height + smallGapY;
        var groupPitchX = (pattern.GroupColumns * seed.Width) +
                          (Math.Max(0, pattern.GroupColumns - 1) * smallGapX) +
                          pattern.GroupGapX(largeGap);
        var groupPitchY = (pattern.GroupRows * seed.Height) +
                          (Math.Max(0, pattern.GroupRows - 1) * smallGapY) +
                          pattern.GroupGapY(largeGap);

        for (var groupY = 0; groupY < pattern.GroupRowsCount; groupY++)
        {
            for (var groupX = 0; groupX < pattern.GroupColumnsCount; groupX++)
            {
                for (var row = 0; row < pattern.GroupRows; row++)
                {
                    for (var column = 0; column < pattern.GroupColumns; column++)
                    {
                        yield return new Rect(
                            seed.X + groupX * groupPitchX + column * innerPitchX,
                            seed.Y + groupY * groupPitchY + row * innerPitchY,
                            seed.Width,
                            seed.Height);
                    }
                }
            }
        }
    }

    private static double ScorePattern(EdgeImage edge, Rect roi, IReadOnlyList<Rect> slots)
    {
        var scores = slots.Select(edge.ScoreSlotBorder).ToList();
        var average = scores.Average();
        var variance = scores.Sum(score => Math.Pow(score - average, 2)) / Math.Max(1, scores.Count);
        var deviation = Math.Sqrt(variance);
        var bounds = BoundingRect(slots);
        var roiArea = Math.Max(1, roi.Width * roi.Height);
        var coverage = Math.Min(1, bounds.Width * bounds.Height / roiArea);
        var topLeftPenalty = ((bounds.Left - roi.Left) * 0.02) + ((bounds.Top - roi.Top) * 0.03);
        return average - deviation * 0.25 + coverage * 8 - topLeftPenalty;
    }

    private static Rect BoundingRect(IReadOnlyList<Rect> slots)
    {
        var left = slots.Min(slot => slot.Left);
        var top = slots.Min(slot => slot.Top);
        var right = slots.Max(slot => slot.Right);
        var bottom = slots.Max(slot => slot.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static bool ContainsWithTolerance(Rect container, Rect item, double tolerance) =>
        item.Left >= container.Left - tolerance &&
        item.Top >= container.Top - tolerance &&
        item.Right <= container.Right + tolerance &&
        item.Bottom <= container.Bottom + tolerance;

    private static IEnumerable<SlotAnchor> NonMaximumSuppress(IEnumerable<SlotAnchor> candidates, double overlapLimit)
    {
        var selected = new List<SlotAnchor>();
        foreach (var candidate in candidates)
        {
            if (selected.Any(existing => IntersectionOverUnion(existing.Rect, candidate.Rect) > overlapLimit))
            {
                continue;
            }

            selected.Add(candidate);
            yield return candidate;
        }
    }

    private static double IntersectionOverUnion(Rect a, Rect b)
    {
        var intersection = Rect.Intersect(a, b);
        if (intersection.IsEmpty)
        {
            return 0;
        }

        var intersectionArea = intersection.Width * intersection.Height;
        var unionArea = a.Width * a.Height + b.Width * b.Height - intersectionArea;
        return unionArea <= 0 ? 0 : intersectionArea / unionArea;
    }

    private static BitmapSource EnsureBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private sealed record SlotAnchor(Rect Rect, double Score);

    private sealed record PatternFit(List<Rect> Slots, double SmallGapX, double SmallGapY, double LargeGap, double Score);

    private sealed record PatternSpec(
        int GroupColumns,
        int GroupRows,
        int GroupColumnsCount,
        int GroupRowsCount,
        Func<double, double> GroupGapX,
        Func<double, double> GroupGapY)
    {
        public static PatternSpec From(QuickslotSectionPatternKind kind) =>
            kind == QuickslotSectionPatternKind.Vertical
                ? new PatternSpec(2, 8, 1, 1, _ => 0, _ => 0)
                : new PatternSpec(4, 2, 3, 1, largeGap => largeGap, _ => 0);
    }

    private sealed class EdgeImage
    {
        private readonly double[] _integral;

        private EdgeImage(int width, int height, double[] integral)
        {
            Width = width;
            Height = height;
            _integral = integral;
        }

        public int Width { get; }

        public int Height { get; }

        public static EdgeImage From(BitmapSource bitmap)
        {
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            var luma = new double[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = y * stride + x * 4;
                    luma[y * width + x] =
                        pixels[offset] * 0.0722 +
                        pixels[offset + 1] * 0.7152 +
                        pixels[offset + 2] * 0.2126;
                }
            }

            var edge = new double[width * height];
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    var dx = Math.Abs(luma[y * width + x - 1] - luma[y * width + x + 1]);
                    var dy = Math.Abs(luma[(y - 1) * width + x] - luma[(y + 1) * width + x]);
                    edge[y * width + x] = Math.Min(255, dx + dy);
                }
            }

            var integral = new double[(width + 1) * (height + 1)];
            for (var y = 1; y <= height; y++)
            {
                var rowSum = 0.0;
                for (var x = 1; x <= width; x++)
                {
                    rowSum += edge[(y - 1) * width + (x - 1)];
                    integral[y * (width + 1) + x] = integral[(y - 1) * (width + 1) + x] + rowSum;
                }
            }

            return new EdgeImage(width, height, integral);
        }

        public double ScoreSlotBorder(Rect rect)
        {
            var x = (int)Math.Round(rect.X);
            var y = (int)Math.Round(rect.Y);
            var width = (int)Math.Round(rect.Width);
            var height = (int)Math.Round(rect.Height);
            if (width < 6 || height < 6)
            {
                return 0;
            }

            var border = Math.Clamp(Math.Min(width, height) / 8, 2, 5);
            var outer = Sum(x, y, width, height);
            var innerWidth = Math.Max(1, width - border * 2);
            var innerHeight = Math.Max(1, height - border * 2);
            var inner = Sum(x + border, y + border, innerWidth, innerHeight);
            var borderArea = Math.Max(1, width * height - innerWidth * innerHeight);
            var innerArea = Math.Max(1, innerWidth * innerHeight);
            var borderAverage = (outer - inner) / borderArea;
            var innerAverage = inner / innerArea;
            return borderAverage - innerAverage * 0.12;
        }

        private double Sum(int x, int y, int width, int height)
        {
            var x1 = Math.Clamp(x, 0, Width);
            var y1 = Math.Clamp(y, 0, Height);
            var x2 = Math.Clamp(x + width, 0, Width);
            var y2 = Math.Clamp(y + height, 0, Height);
            if (x2 <= x1 || y2 <= y1)
            {
                return 0;
            }

            var stride = Width + 1;
            return _integral[y2 * stride + x2]
                   - _integral[y1 * stride + x2]
                   - _integral[y2 * stride + x1]
                   + _integral[y1 * stride + x1];
        }
    }
}

public sealed record SectionDetectionResult(
    QuickslotSectionPatternKind PatternKind,
    IReadOnlyList<Rect> Slots,
    double SmallGapX,
    double SmallGapY,
    double LargeGap,
    double Score);
