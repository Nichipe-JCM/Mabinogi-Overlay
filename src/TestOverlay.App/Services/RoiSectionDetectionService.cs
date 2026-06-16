using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class RoiSectionDetectionService
{
    private const int MinDetectedSlotSize = 22;
    private const int DarkBorderTolerance = 140;
    private const double MinBorderInnerContrast = 12;
    private const double MinBorderEdge = 18;
    private const double RequiredStrongSideCoverage = 0.90;
    private const double RequiredVerticalSideCoverage = 0.85;
    private const double RequiredStrongSideContrastCoverage = 0.35;
    private const double RequiredVerticalSideContrastCoverage = 0.20;
    private const double TopGroupedLabelCornerMaskRatio = 0.25;
    private const double MinAnchorScore = 10;
    private const double MinPatternScore = 10;
    private const int MaxTopGroupedAnchorCandidates = 256;
    private const int MaxVerticalAnchorCandidates = 256;
    private const int MaxRawAnchorCandidates = 1600;
    private const int AnchorScanStep = 1;
    private const int MinGap = 2;
    private const int MaxSmallGap = 30;
    private const int MaxLargeGap = 60;
    private const double InsetContentSampleRatio = 0.10;
    private const double MinInsetIconActivityDelta = 8;
    private const double RequiredContentSlotCoverage = 0.75;

    public SectionDetectionResult? Detect(
        BitmapSource source,
        Rect roi,
        QuickslotSectionPatternKind patternKind,
        IList<string>? diagnostics = null)
    {
        var bitmap = EnsureBgra32(source);
        var imageRect = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
        roi.Intersect(imageRect);
        if (roi.IsEmpty || roi.Width < 24 || roi.Height < 24)
        {
            diagnostics?.Add($"invalid roi after clipping: {FormatRect(roi)}");
            return null;
        }

        var edge = EdgeImage.From(bitmap);
        var pattern = PatternSpec.From(patternKind);
        var anchors = FindBottomRightAnchoredSlotCandidates(
                edge,
                roi,
                pattern,
                patternKind,
                diagnostics)
            .ToList();
        diagnostics?.Add($"anchors={anchors.Count}");
        foreach (var anchor in anchors.Take(16))
        {
            diagnostics?.Add($"anchor {FormatRect(anchor.Rect)} score={anchor.Score:0.000}");
        }

        if (anchors.Count == 0)
        {
            return null;
        }

        PatternFit? best = null;
        var fitCount = 0;
        foreach (var anchor in anchors)
        {
            foreach (var fit in EnumerateFits(edge, roi, pattern, patternKind, anchor))
            {
                fitCount++;
                if (best is null || fit.Score > best.Score)
                {
                    best = fit;
                }
            }
        }

        diagnostics?.Add($"fits={fitCount}");
        if (best is null)
        {
            return null;
        }

        if (best.Score < MinPatternScore)
        {
            var contentCoverage = ContentCoverage(edge, patternKind, best.Slots);
            diagnostics?.Add(
                $"best rejected: score={best.Score:0.000} below minPatternScore={MinPatternScore:0.000}, contentCoverage={contentCoverage:0.000}");
            return null;
        }

        best = NormalizeTopLeftOversizedFit(edge, roi, patternKind, best, diagnostics);

        diagnostics?.Add(
            $"best first={FormatRect(best.Slots[0])} gapX={best.SmallGapX:0} gapY={best.SmallGapY:0} large={best.LargeGap:0} score={best.Score:0.000} contentCoverage={ContentCoverage(edge, patternKind, best.Slots):0.000}");
        return new SectionDetectionResult(
            patternKind,
            best.Slots,
            best.SmallGapX,
            best.SmallGapY,
            best.LargeGap,
            best.Score);
    }

    private static IEnumerable<SlotAnchor> FindBottomRightAnchoredSlotCandidates(
        EdgeImage edge,
        Rect roi,
        PatternSpec pattern,
        QuickslotSectionPatternKind patternKind,
        IList<string>? diagnostics)
    {
        var candidates = new List<SlotAnchor>();
        var totalSlotColumns = pattern.GroupColumns * pattern.GroupColumnsCount;
        var totalSlotRows = pattern.GroupRows * pattern.GroupRowsCount;
        var maxSize = Math.Min(
            300,
            (int)Math.Floor(Math.Min(roi.Width / totalSlotColumns, roi.Height / totalSlotRows)));
        var minSize = Math.Max(MinDetectedSlotSize, (int)Math.Floor(maxSize * 0.62));
        diagnostics?.Add($"anchor search roi={FormatRect(roi)} maxSize={maxSize} minSize={minSize} step={AnchorScanStep}");
        if (maxSize < minSize)
        {
            return [];
        }

        var left = (int)Math.Floor(roi.Left);
        var top = (int)Math.Floor(roi.Top);
        var right = (int)Math.Ceiling(roi.Right);
        var bottom = (int)Math.Ceiling(roi.Bottom);

        for (var slotSize = minSize; slotSize <= maxSize; slotSize++)
        {
            for (var bottomRightY = top + slotSize - 1; bottomRightY < bottom; bottomRightY += AnchorScanStep)
            {
                for (var bottomRightX = left + slotSize - 1; bottomRightX < right; bottomRightX += AnchorScanStep)
                {
                    var x = bottomRightX - slotSize + 1;
                    var y = bottomRightY - slotSize + 1;
                    var rect = new Rect(x, y, slotSize, slotSize);
                    var anchorScore = edge.ScoreAnchor(rect, patternKind);
                    if (anchorScore < 10)
                    {
                        continue;
                    }

                    candidates.Add(new SlotAnchor(rect, anchorScore));
                }

                if (candidates.Count > 5000)
                {
                    candidates = candidates
                        .OrderByDescending(candidate => candidate.Score)
                        .Take(MaxRawAnchorCandidates)
                        .ToList();
                }
            }
        }

        var feasibleRaw = candidates
            .OrderByDescending(candidate => candidate.Score)
            .Where(candidate => CanSeedFitPatternWithMinimumGaps(roi, pattern, patternKind, candidate.Rect))
            .ToList();
        var seedPool = SelectPatternSeedPool(feasibleRaw, patternKind);
        diagnostics?.Add(
            $"rawAnchors={candidates.Count} feasibleRawSeeds={feasibleRaw.Count} seedPool={seedPool.Count}");
        return seedPool;
    }

    private static IReadOnlyList<SlotAnchor> SelectPatternSeedPool(
        IReadOnlyList<SlotAnchor> feasibleRaw,
        QuickslotSectionPatternKind patternKind)
    {
        if (feasibleRaw.Count == 0)
        {
            return [];
        }

        var maxCandidates = patternKind == QuickslotSectionPatternKind.Vertical
            ? MaxVerticalAnchorCandidates
            : MaxTopGroupedAnchorCandidates;
        return feasibleRaw
            .Where(candidate => candidate.Score >= MinAnchorScore)
            .GroupBy(candidate => (
                X: (int)Math.Round(candidate.Rect.X),
                Y: (int)Math.Round(candidate.Rect.Y),
                W: (int)Math.Round(candidate.Rect.Width),
                H: (int)Math.Round(candidate.Rect.Height)))
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Rect.Width)
            .ThenBy(candidate => candidate.Rect.Y)
            .ThenBy(candidate => candidate.Rect.X)
            .Take(maxCandidates)
            .ToList();
    }

    private static IEnumerable<PatternFit> EnumerateFits(
        EdgeImage edge,
        Rect roi,
        PatternSpec pattern,
        QuickslotSectionPatternKind patternKind,
        SlotAnchor anchor)
    {
        for (var smallGapX = MinGap; smallGapX <= MaxSmallGap; smallGapX++)
        {
            for (var smallGapY = MinGap; smallGapY <= MaxSmallGap; smallGapY++)
            {
                if (patternKind == QuickslotSectionPatternKind.Vertical && smallGapX != smallGapY)
                {
                    continue;
                }

                var maxLargeGap = patternKind == QuickslotSectionPatternKind.TopGrouped ? MaxLargeGap : MinGap;
                for (var largeGap = MinGap; largeGap <= maxLargeGap; largeGap++)
                {
                    if (!IsAllowedGapCombination(patternKind, smallGapX, smallGapY, largeGap))
                    {
                        continue;
                    }

                    var slots = BuildPatternSlots(anchor.Rect, pattern, smallGapX, smallGapY, largeGap).ToList();
                    if (slots.Any(slot => !Contains(roi, slot)))
                    {
                        continue;
                    }

                    var score = ScorePattern(edge, roi, patternKind, slots);
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

    private static double ScorePattern(
        EdgeImage edge,
        Rect roi,
        QuickslotSectionPatternKind patternKind,
        IReadOnlyList<Rect> slots)
    {
        var contentCoverage = ContentCoverage(edge, patternKind, slots);
        if (contentCoverage < RequiredContentSlotCoverage)
        {
            return 0;
        }

        var scores = slots.Select(slot => edge.ScoreSlotBorder(slot, patternKind)).ToList();
        var average = scores.Average();
        var variance = scores.Sum(score => Math.Pow(score - average, 2)) / Math.Max(1, scores.Count);
        var deviation = Math.Sqrt(variance);
        var bounds = BoundingRect(slots);
        var roiArea = Math.Max(1, roi.Width * roi.Height);
        var coverage = Math.Min(1, bounds.Width * bounds.Height / roiArea);
        var topLeftPenalty = ((bounds.Left - roi.Left) * 0.02) + ((bounds.Top - roi.Top) * 0.03);
        return average - deviation * 0.25 + coverage * 3 - topLeftPenalty;
    }

    private static double ContentCoverage(
        EdgeImage edge,
        QuickslotSectionPatternKind patternKind,
        IReadOnlyCollection<Rect> slots) =>
        slots.Count(slot => edge.HasMeaningfulSlotContent(slot, patternKind)) /
        (double)Math.Max(1, slots.Count);

    private static PatternFit NormalizeTopLeftOversizedFit(
        EdgeImage edge,
        Rect roi,
        QuickslotSectionPatternKind patternKind,
        PatternFit fit,
        IList<string>? diagnostics)
    {
        if (!IsCompressedGapFit(patternKind, fit))
        {
            diagnostics?.Add(
                $"oversize check: gapX={fit.SmallGapX:0}, gapY={fit.SmallGapY:0}, large={fit.LargeGap:0}, action=keep-gap");
            return fit;
        }

        var insetVotes = fit.Slots.Count(slot => edge.HasInsetIconContent(slot, patternKind));
        var requiredVotes = Math.Max(1, (int)Math.Ceiling(fit.Slots.Count * 0.50));
        if (insetVotes < requiredVotes)
        {
            diagnostics?.Add($"oversize check: insetVotes={insetVotes}/{fit.Slots.Count}, required={requiredVotes}, action=keep");
            return fit;
        }

        var normalizedSlots = fit.Slots
            .Select(slot => new Rect(slot.X + 1, slot.Y + 1, slot.Width - 1, slot.Height - 1))
            .ToList();
        if (normalizedSlots.Any(slot => slot.Width < MinDetectedSlotSize || slot.Height < MinDetectedSlotSize) ||
            normalizedSlots.Any(slot => !Contains(roi, slot)))
        {
            diagnostics?.Add($"oversize check: insetVotes={insetVotes}/{fit.Slots.Count}, required={requiredVotes}, action=blocked");
            return fit;
        }

        var normalizedGapX = fit.SmallGapX + 1;
        var normalizedGapY = fit.SmallGapY + 1;
        var normalizedLargeGap = patternKind == QuickslotSectionPatternKind.TopGrouped
            ? fit.LargeGap + 1
            : fit.LargeGap;
        var normalizedScore = ScorePattern(edge, roi, patternKind, normalizedSlots);
        diagnostics?.Add(
            $"oversize check: insetVotes={insetVotes}/{fit.Slots.Count}, required={requiredVotes}, action=inset, " +
            $"from first={FormatRect(fit.Slots[0])} gapX={fit.SmallGapX:0} gapY={fit.SmallGapY:0} large={fit.LargeGap:0} score={fit.Score:0.000}, " +
            $"to first={FormatRect(normalizedSlots[0])} gapX={normalizedGapX:0} gapY={normalizedGapY:0} large={normalizedLargeGap:0} score={normalizedScore:0.000}");
        return new PatternFit(
            normalizedSlots,
            normalizedGapX,
            normalizedGapY,
            normalizedLargeGap,
            normalizedScore);
    }

    private static bool IsCompressedGapFit(
        QuickslotSectionPatternKind patternKind,
        PatternFit fit) =>
        patternKind == QuickslotSectionPatternKind.Vertical
            ? fit.SmallGapX <= MinGap && fit.SmallGapY <= MinGap
            : fit.SmallGapX <= MinGap;

    private static Rect BoundingRect(IReadOnlyList<Rect> slots)
    {
        var left = slots.Min(slot => slot.Left);
        var top = slots.Min(slot => slot.Top);
        var right = slots.Max(slot => slot.Right);
        var bottom = slots.Max(slot => slot.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static bool Contains(Rect container, Rect item) =>
        item.Left >= container.Left &&
        item.Top >= container.Top &&
        item.Right <= container.Right &&
        item.Bottom <= container.Bottom;

    private static bool IsAllowedGapCombination(
        QuickslotSectionPatternKind patternKind,
        double smallGapX,
        double smallGapY,
        double largeGap) =>
        patternKind == QuickslotSectionPatternKind.Vertical
            ? DoubleEquals(smallGapX, smallGapY)
            : smallGapX < smallGapY &&
              smallGapY < largeGap &&
              smallGapY < smallGapX * 5 &&
              largeGap < smallGapX * 8;

    private static bool DoubleEquals(double left, double right) =>
        Math.Abs(left - right) < 0.0001;

    private static bool CanSeedFitPatternWithMinimumGaps(
        Rect roi,
        PatternSpec pattern,
        QuickslotSectionPatternKind patternKind,
        Rect seed)
    {
        var (smallGapX, smallGapY, largeGap) = patternKind == QuickslotSectionPatternKind.TopGrouped
            ? (MinGap, MinGap + 1, MinGap + 2)
            : (MinGap, MinGap, MinGap);
        var slots = BuildPatternSlots(seed, pattern, smallGapX, smallGapY, largeGap);
        var bounds = BoundingRect(slots.ToList());
        return Contains(roi, bounds);
    }

    private static string FormatRect(Rect rect) =>
        $"x={rect.X:0.###},y={rect.Y:0.###},w={rect.Width:0.###},h={rect.Height:0.###}";

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
        private readonly double[] _hitIntegral;
        private readonly double[] _topBorderHitIntegral;
        private readonly double[] _bottomBorderHitIntegral;
        private readonly double[] _leftBorderHitIntegral;
        private readonly double[] _rightBorderHitIntegral;
        private readonly double[] _contentActivityIntegral;

        private EdgeImage(
            int width,
            int height,
            double[] integral,
            double[] hitIntegral,
            double[] topBorderHitIntegral,
            double[] bottomBorderHitIntegral,
            double[] leftBorderHitIntegral,
            double[] rightBorderHitIntegral,
            double[] contentActivityIntegral)
        {
            Width = width;
            Height = height;
            _integral = integral;
            _hitIntegral = hitIntegral;
            _topBorderHitIntegral = topBorderHitIntegral;
            _bottomBorderHitIntegral = bottomBorderHitIntegral;
            _leftBorderHitIntegral = leftBorderHitIntegral;
            _rightBorderHitIntegral = rightBorderHitIntegral;
            _contentActivityIntegral = contentActivityIntegral;
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
            var blackIntegral = new double[(width + 1) * (height + 1)];
            var topBorderIntegral = new double[(width + 1) * (height + 1)];
            var bottomBorderIntegral = new double[(width + 1) * (height + 1)];
            var leftBorderIntegral = new double[(width + 1) * (height + 1)];
            var rightBorderIntegral = new double[(width + 1) * (height + 1)];
            var contentActivityIntegral = new double[(width + 1) * (height + 1)];
            for (var y = 1; y <= height; y++)
            {
                var rowSum = 0.0;
                var blackRowSum = 0.0;
                var topBorderRowSum = 0.0;
                var bottomBorderRowSum = 0.0;
                var leftBorderRowSum = 0.0;
                var rightBorderRowSum = 0.0;
                var contentActivityRowSum = 0.0;
                for (var x = 1; x <= width; x++)
                {
                    var pixelOffset = (y - 1) * stride + (x - 1) * 4;
                    var blue = pixels[pixelOffset];
                    var green = pixels[pixelOffset + 1];
                    var red = pixels[pixelOffset + 2];
                    var sourceX = x - 1;
                    var sourceY = y - 1;
                    var sourceIndex = sourceY * width + sourceX;
                    var edgeValue = edge[(y - 1) * width + (x - 1)];
                    var isDark = IsNearBlack(blue, green, red);
                    var chroma = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
                    var contentActivity = chroma + edgeValue * 0.35;
                    rowSum += edgeValue;
                    blackRowSum += isDark ? 1 : 0;
                    topBorderRowSum += IsOnePixelBorderHit(luma, edge, width, height, sourceX, sourceY, 0, 1, isDark) ? 1 : 0;
                    bottomBorderRowSum += IsOnePixelBorderHit(luma, edge, width, height, sourceX, sourceY, 0, -1, isDark) ? 1 : 0;
                    leftBorderRowSum += IsOnePixelBorderHit(luma, edge, width, height, sourceX, sourceY, 1, 0, isDark) ? 1 : 0;
                    rightBorderRowSum += IsOnePixelBorderHit(luma, edge, width, height, sourceX, sourceY, -1, 0, isDark) ? 1 : 0;
                    contentActivityRowSum += contentActivity;
                    integral[y * (width + 1) + x] = integral[(y - 1) * (width + 1) + x] + rowSum;
                    blackIntegral[y * (width + 1) + x] = blackIntegral[(y - 1) * (width + 1) + x] + blackRowSum;
                    topBorderIntegral[y * (width + 1) + x] = topBorderIntegral[(y - 1) * (width + 1) + x] + topBorderRowSum;
                    bottomBorderIntegral[y * (width + 1) + x] = bottomBorderIntegral[(y - 1) * (width + 1) + x] + bottomBorderRowSum;
                    leftBorderIntegral[y * (width + 1) + x] = leftBorderIntegral[(y - 1) * (width + 1) + x] + leftBorderRowSum;
                    rightBorderIntegral[y * (width + 1) + x] = rightBorderIntegral[(y - 1) * (width + 1) + x] + rightBorderRowSum;
                    contentActivityIntegral[y * (width + 1) + x] = contentActivityIntegral[(y - 1) * (width + 1) + x] + contentActivityRowSum;
                }
            }

            return new EdgeImage(
                width,
                height,
                integral,
                blackIntegral,
                topBorderIntegral,
                bottomBorderIntegral,
                leftBorderIntegral,
                rightBorderIntegral,
                contentActivityIntegral);
        }

        private static bool IsNearBlack(byte blue, byte green, byte red) =>
            blue + green + red < DarkBorderTolerance;

        private static bool IsOnePixelBorderHit(
            IReadOnlyList<double> luma,
            IReadOnlyList<double> edge,
            int width,
            int height,
            int x,
            int y,
            int innerDx,
            int innerDy,
            bool isDark)
        {
            if (!isDark)
            {
                return false;
            }

            var innerX = x + innerDx;
            var innerY = y + innerDy;
            if (innerX < 0 || innerX >= width || innerY < 0 || innerY >= height)
            {
                return false;
            }

            var index = y * width + x;
            var innerIndex = innerY * width + innerX;
            return luma[innerIndex] - luma[index] >= MinBorderInnerContrast ||
                   edge[index] >= MinBorderEdge;
        }

        public double ScoreAnchor(Rect rect, QuickslotSectionPatternKind patternKind) =>
            ScoreBottomRightAnchor(rect);

        private double ScoreBottomRightAnchor(Rect rect)
        {
            var x = (int)Math.Round(rect.X);
            var y = (int)Math.Round(rect.Y);
            var width = (int)Math.Round(rect.Width);
            var height = (int)Math.Round(rect.Height);
            if (width < 6 || height < 6)
            {
                return 0;
            }

            var bottomCoverage = Coverage(x, y + height - 1, width, 1);
            var rightCoverage = Coverage(x + width - 1, y, 1, height);
            if (bottomCoverage < RequiredStrongSideCoverage ||
                rightCoverage < RequiredStrongSideCoverage)
            {
                return 0;
            }

            var bottomContrastCoverage = BottomBorderCoverage(x, y + height - 1, width, 1);
            var rightContrastCoverage = RightBorderCoverage(x + width - 1, y, 1, height);
            if (bottomContrastCoverage < RequiredStrongSideContrastCoverage ||
                rightContrastCoverage < RequiredStrongSideContrastCoverage)
            {
                return 0;
            }

            var cornerCoverage = Coverage(
                x + Math.Max(0, width - 3),
                y + Math.Max(0, height - 3),
                Math.Min(3, width),
                Math.Min(3, height));
            var bottomAverage = Average(x, y + height - 1, width, 1);
            var rightAverage = Average(x + width - 1, y, 1, height);
            return (bottomCoverage * 45) +
                   (rightCoverage * 45) +
                   (bottomContrastCoverage * 20) +
                   (rightContrastCoverage * 20) +
                   (cornerCoverage * 20) +
                   ((bottomAverage + rightAverage) * 0.04);
        }

        public double ScoreSlotBorder(Rect rect, QuickslotSectionPatternKind patternKind)
        {
            var x = (int)Math.Round(rect.X);
            var y = (int)Math.Round(rect.Y);
            var width = (int)Math.Round(rect.Width);
            var height = (int)Math.Round(rect.Height);
            if (width < 6 || height < 6)
            {
                return 0;
            }

            return patternKind == QuickslotSectionPatternKind.Vertical
                ? ScoreStrictSlotBorder(x, y, width, height)
                : ScoreTopGroupedSlotBorder(x, y, width, height);
        }

        public bool HasInsetIconContent(Rect rect, QuickslotSectionPatternKind patternKind)
        {
            var x = (int)Math.Round(rect.X);
            var y = (int)Math.Round(rect.Y);
            var width = (int)Math.Round(rect.Width);
            var height = (int)Math.Round(rect.Height);
            var innerWidth = width - 1;
            var innerHeight = height - 1;
            if (innerWidth < MinDetectedSlotSize || innerHeight < MinDetectedSlotSize)
            {
                return false;
            }

            var innerLabelMask = patternKind == QuickslotSectionPatternKind.TopGrouped
                ? (int)Math.Round(
                    Math.Min(innerWidth, innerHeight) * TopGroupedLabelCornerMaskRatio,
                    MidpointRounding.AwayFromZero)
                : 0;

            var insetX = x + 1;
            var insetY = y + 1;
            var contentX = insetX + 1;
            var contentY = insetY + 1;
            var contentWidth = Math.Max(1, innerWidth - 2);
            var contentHeight = Math.Max(1, innerHeight - 2);
            if (patternKind == QuickslotSectionPatternKind.TopGrouped)
            {
                contentX += innerLabelMask;
                contentWidth -= innerLabelMask;
            }

            if (contentWidth <= 0 || contentHeight <= 0)
            {
                return false;
            }

            var stripHeight = Math.Clamp(
                (int)Math.Round(Math.Min(innerWidth, innerHeight) * InsetContentSampleRatio, MidpointRounding.AwayFromZero),
                1,
                contentHeight);
            var stripWidth = Math.Clamp(
                (int)Math.Round(Math.Min(innerWidth, innerHeight) * InsetContentSampleRatio, MidpointRounding.AwayFromZero),
                1,
                contentWidth);
            var topContentActivity = AverageContentActivity(contentX, contentY, contentWidth, stripHeight);
            var leftContentActivity = AverageContentActivity(contentX, contentY, stripWidth, contentHeight);
            var centerContentActivity = AverageContentActivity(contentX, contentY, contentWidth, contentHeight);
            var borderReferenceActivity = AverageContentActivity(insetX, insetY, innerWidth, 1);
            var activity = Math.Max(centerContentActivity, (topContentActivity + leftContentActivity) / 2);
            return activity >= borderReferenceActivity + MinInsetIconActivityDelta;
        }

        public bool HasMeaningfulSlotContent(Rect rect, QuickslotSectionPatternKind patternKind)
        {
            var x = (int)Math.Round(rect.X);
            var y = (int)Math.Round(rect.Y);
            var width = (int)Math.Round(rect.Width);
            var height = (int)Math.Round(rect.Height);
            if (width < 6 || height < 6)
            {
                return false;
            }

            var labelMask = patternKind == QuickslotSectionPatternKind.TopGrouped
                ? (int)Math.Round(
                    Math.Min(width, height) * TopGroupedLabelCornerMaskRatio,
                    MidpointRounding.AwayFromZero)
                : 0;
            var contentX = x + 1 + labelMask;
            var contentY = y + 1;
            var contentWidth = width - 2 - labelMask;
            var contentHeight = height - 2;
            if (contentWidth <= 0 || contentHeight <= 0)
            {
                return false;
            }

            var activity = AverageContentActivity(contentX, contentY, contentWidth, contentHeight);
            var darkness = Coverage(contentX, contentY, contentWidth, contentHeight);
            return activity >= AverageContentActivity(x, y, width, 1) + MinInsetIconActivityDelta &&
                   darkness < 0.95;
        }

        private double ScoreTopGroupedSlotBorder(int x, int y, int width, int height)
        {
            var strictScore = ScoreStrictSlotBorder(x, y, width, height);
            if (strictScore > 0)
            {
                return strictScore + 12;
            }

            return ScoreLabeledSlotBorder(x, y, width, height);
        }

        private double ScoreLabeledSlotBorder(int x, int y, int width, int height)
        {
            var labelMask = (int)Math.Round(
                Math.Min(width, height) * TopGroupedLabelCornerMaskRatio,
                MidpointRounding.AwayFromZero);
            var topCoverage = Coverage(x + labelMask, y, width - labelMask, 1);
            var bottomCoverage = Coverage(x, y + height - 1, width, 1);
            var leftCoverage = Coverage(x, y + labelMask, 1, height - labelMask);
            var rightCoverage = Coverage(x + width - 1, y, 1, height);
            if (rightCoverage < RequiredStrongSideCoverage ||
                bottomCoverage < RequiredStrongSideCoverage ||
                leftCoverage < RequiredVerticalSideCoverage ||
                topCoverage < RequiredVerticalSideCoverage)
            {
                return 0;
            }

            var topContrastCoverage = TopBorderCoverage(x + labelMask, y, width - labelMask, 1);
            var bottomContrastCoverage = BottomBorderCoverage(x, y + height - 1, width, 1);
            var leftContrastCoverage = LeftBorderCoverage(x, y + labelMask, 1, height - labelMask);
            var rightContrastCoverage = RightBorderCoverage(x + width - 1, y, 1, height);
            if (bottomContrastCoverage < RequiredStrongSideContrastCoverage ||
                rightContrastCoverage < RequiredStrongSideContrastCoverage ||
                leftContrastCoverage < RequiredVerticalSideContrastCoverage ||
                topContrastCoverage < RequiredVerticalSideContrastCoverage)
            {
                return 0;
            }

            var sideCoverage =
                (rightCoverage * 0.30) +
                (bottomCoverage * 0.30) +
                (leftCoverage * 0.20) +
                (topCoverage * 0.20);
            var contrastCoverage =
                (rightContrastCoverage * 0.30) +
                (bottomContrastCoverage * 0.30) +
                (leftContrastCoverage * 0.20) +
                (topContrastCoverage * 0.20);
            var sideAverage =
                (Average(x + labelMask, y, width - labelMask, 1) +
                 Average(x, y + height - 1, width, 1) +
                 Average(x, y + labelMask, 1, height - labelMask) +
                 Average(x + width - 1, y, 1, height)) / 4;
            var innerWidth = Math.Max(1, width - 2);
            var innerHeight = Math.Max(1, height - 2);
            var innerAverage = Average(x + 1, y + 1, innerWidth, innerHeight);
            return sideCoverage * 70 + contrastCoverage * 55 + sideAverage * 0.08 - innerAverage * 0.04;
        }

        private double ScoreStrictSlotBorder(int x, int y, int width, int height)
        {
            var topCoverage = Coverage(x, y, width, 1);
            var bottomCoverage = Coverage(x, y + height - 1, width, 1);
            var leftCoverage = Coverage(x, y, 1, height);
            var rightCoverage = Coverage(x + width - 1, y, 1, height);
            if (topCoverage < RequiredVerticalSideCoverage ||
                bottomCoverage < RequiredVerticalSideCoverage ||
                leftCoverage < RequiredVerticalSideCoverage ||
                rightCoverage < RequiredVerticalSideCoverage)
            {
                return 0;
            }

            var topContrastCoverage = TopBorderCoverage(x, y, width, 1);
            var bottomContrastCoverage = BottomBorderCoverage(x, y + height - 1, width, 1);
            var leftContrastCoverage = LeftBorderCoverage(x, y, 1, height);
            var rightContrastCoverage = RightBorderCoverage(x + width - 1, y, 1, height);
            var contrastCoverage = (topContrastCoverage + bottomContrastCoverage + leftContrastCoverage + rightContrastCoverage) / 4;
            if (topContrastCoverage < RequiredVerticalSideContrastCoverage ||
                bottomContrastCoverage < RequiredVerticalSideContrastCoverage ||
                leftContrastCoverage < RequiredVerticalSideContrastCoverage ||
                rightContrastCoverage < RequiredVerticalSideContrastCoverage)
            {
                return 0;
            }

            var cornerSize = Math.Min(2, Math.Min(width, height));
            var cornerCoverage =
                (Coverage(x, y, cornerSize, cornerSize) +
                 Coverage(x + width - cornerSize, y, cornerSize, cornerSize) +
                 Coverage(x, y + height - cornerSize, cornerSize, cornerSize) +
                 Coverage(x + width - cornerSize, y + height - cornerSize, cornerSize, cornerSize)) / 4;
            var sideCoverage = (topCoverage + bottomCoverage + leftCoverage + rightCoverage) / 4;
            var sideAverage =
                (Average(x, y, width, 1) +
                 Average(x, y + height - 1, width, 1) +
                 Average(x, y, 1, height) +
                 Average(x + width - 1, y, 1, height)) / 4;
            var innerWidth = Math.Max(1, width - 2);
            var innerHeight = Math.Max(1, height - 2);
            var innerAverage = Average(x + 1, y + 1, innerWidth, innerHeight);
            return sideCoverage * 70 + contrastCoverage * 55 + cornerCoverage * 20 + sideAverage * 0.08 - innerAverage * 0.04;
        }

        private double Average(int x, int y, int width, int height) =>
            Sum(x, y, width, height) / Math.Max(1, width * height);

        private double AverageContentActivity(int x, int y, int width, int height) =>
            SumContentActivity(x, y, width, height) / Math.Max(1, width * height);

        private double Coverage(int x, int y, int width, int height) =>
            SumHits(x, y, width, height) / Math.Max(1, width * height);

        private double TopBorderCoverage(int x, int y, int width, int height) =>
            SumHits(_topBorderHitIntegral, x, y, width, height) / Math.Max(1, width * height);

        private double BottomBorderCoverage(int x, int y, int width, int height) =>
            SumHits(_bottomBorderHitIntegral, x, y, width, height) / Math.Max(1, width * height);

        private double LeftBorderCoverage(int x, int y, int width, int height) =>
            SumHits(_leftBorderHitIntegral, x, y, width, height) / Math.Max(1, width * height);

        private double RightBorderCoverage(int x, int y, int width, int height) =>
            SumHits(_rightBorderHitIntegral, x, y, width, height) / Math.Max(1, width * height);

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

        private double SumHits(int x, int y, int width, int height)
        {
            return SumHits(_hitIntegral, x, y, width, height);
        }

        private double SumContentActivity(int x, int y, int width, int height)
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
            return _contentActivityIntegral[y2 * stride + x2]
                   - _contentActivityIntegral[y1 * stride + x2]
                   - _contentActivityIntegral[y2 * stride + x1]
                   + _contentActivityIntegral[y1 * stride + x1];
        }

        private double SumHits(double[] hitIntegral, int x, int y, int width, int height)
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
            return hitIntegral[y2 * stride + x2]
                   - hitIntegral[y1 * stride + x2]
                   - hitIntegral[y2 * stride + x1]
                   + hitIntegral[y1 * stride + x1];
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
