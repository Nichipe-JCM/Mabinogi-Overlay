using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class SlotDetectionService
{
    public IReadOnlyList<SlotCandidate> Detect(BitmapSource source, int minSize, int maxSize)
    {
        var bitmap = EnsureBgra32(source);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var raw = new List<CandidateInfo>();
        var min = Math.Clamp(minSize, 12, 160);
        var max = Math.Clamp(maxSize, min, 180);

        for (var size = min; size <= max; size += Math.Max(2, size / 16))
        {
            var step = Math.Max(2, size / 12);
            var topHeight = Math.Max(size + 2, bitmap.PixelHeight / 5);
            var sideHeight = Math.Max(size + 2, bitmap.PixelHeight * 45 / 100);
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(0, 0, bitmap.PixelWidth, topHeight));
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(0, 0, Math.Min(bitmap.PixelWidth, bitmap.PixelWidth / 5), sideHeight));
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(Math.Max(0, bitmap.PixelWidth * 4 / 5), 0, bitmap.PixelWidth / 5, sideHeight));
        }

        var gridCandidates = ScoreGridRuns(raw);
        var picked = SuppressOverlappingSlots(gridCandidates.OrderByDescending(item => item.Score))
            .Take(160)
            .Select((item, index) => new SlotCandidate(index + 1, item.Rect, item.Score))
            .ToList();

        return picked;
    }

    private static void ScanRegion(
        ICollection<CandidateInfo> raw,
        byte[] pixels,
        int width,
        int height,
        int stride,
        int size,
        int step,
        Int32Rect region)
    {
        var right = Math.Min(width - size - 1, region.X + region.Width - size);
        var bottom = Math.Min(height - size - 1, region.Y + region.Height - size);

        for (var y = region.Y; y <= bottom; y += step)
        {
            for (var x = region.X; x <= right; x += step)
            {
                var score = ScoreSlotFrame(pixels, width, height, stride, x, y, size);
                if (score >= 54)
                {
                    raw.Add(new CandidateInfo(new Rect(x, y, size, size), size, score));
                }
            }
        }
    }

    private static double ScoreSlotFrame(byte[] pixels, int width, int height, int stride, int x, int y, int size)
    {
        if (x <= 0 || y <= 0 || x + size >= width - 1 || y + size >= height - 1)
        {
            return 0;
        }

        var edgeSum = 0.0;
        var continuityHits = 0;
        var samples = 0;
        var sampleStep = Math.Max(1, size / 12);
        for (var i = 0; i < size; i += Math.Max(1, size / 12))
        {
            AddEdgeSample(VerticalGradient(pixels, stride, x + i, y), ref edgeSum, ref continuityHits, ref samples);
            AddEdgeSample(VerticalGradient(pixels, stride, x + i, y + size - 1), ref edgeSum, ref continuityHits, ref samples);
            AddEdgeSample(HorizontalGradient(pixels, stride, x, y + i), ref edgeSum, ref continuityHits, ref samples);
            AddEdgeSample(HorizontalGradient(pixels, stride, x + size - 1, y + i), ref edgeSum, ref continuityHits, ref samples);
        }

        var continuity = continuityHits / (double)Math.Max(1, samples);
        var averageEdge = edgeSum / Math.Max(1, samples);
        var cornerScore =
            DiagonalCornerScore(pixels, width, height, stride, x, y, sampleStep) +
            DiagonalCornerScore(pixels, width, height, stride, x + size - 1, y, sampleStep) +
            DiagonalCornerScore(pixels, width, height, stride, x, y + size - 1, sampleStep) +
            DiagonalCornerScore(pixels, width, height, stride, x + size - 1, y + size - 1, sampleStep);

        return averageEdge * 0.9 + continuity * 50 + cornerScore * 0.18;
    }

    private static List<CandidateInfo> ScoreGridRuns(IReadOnlyList<CandidateInfo> candidates)
    {
        var bySize = candidates.GroupBy(candidate => candidate.Size);
        var scored = new List<CandidateInfo>();

        foreach (var group in bySize)
        {
            var list = NonMaximumSuppress(group.OrderByDescending(item => item.Score), 0.22)
                .Take(900)
                .ToList();

            foreach (var candidate in list)
            {
                var horizontalRun = CountRun(candidate, list, Axis.Horizontal);
                var verticalRun = CountRun(candidate, list, Axis.Vertical);
                var bestRun = Math.Max(horizontalRun, verticalRun);
                if (bestRun < 3)
                {
                    continue;
                }

                var crossRun = Math.Min(horizontalRun, verticalRun);
                scored.Add(candidate with { Score = candidate.Score + bestRun * 70 + crossRun * 35 });
            }
        }

        return scored;
    }

    private static int CountRun(CandidateInfo candidate, IReadOnlyList<CandidateInfo> candidates, Axis axis)
    {
        var size = candidate.Size;
        var tolerance = Math.Max(3, size / 8);
        var maxGap = Math.Max(5, size / 5);
        var bestRun = 1;

        for (var gap = -1; gap <= maxGap; gap++)
        {
            var pitch = size + gap;
            var run = 1 +
                CountDirection(candidate, candidates, axis, -1, pitch, tolerance) +
                CountDirection(candidate, candidates, axis, 1, pitch, tolerance);
            bestRun = Math.Max(bestRun, run);
        }

        return bestRun;
    }

    private static int CountDirection(
        CandidateInfo candidate,
        IReadOnlyList<CandidateInfo> candidates,
        Axis axis,
        int direction,
        int pitch,
        int tolerance)
    {
        var count = 0;
        var current = candidate;
        while (true)
        {
            var next = FindNeighbor(current, candidates, axis, direction, pitch, tolerance);
            if (next is null)
            {
                return count;
            }

            count++;
            current = next;
        }
    }

    private static CandidateInfo? FindNeighbor(
        CandidateInfo candidate,
        IReadOnlyList<CandidateInfo> candidates,
        Axis axis,
        int direction,
        int pitch,
        int tolerance)
    {
        var targetX = candidate.Rect.X + (axis == Axis.Horizontal ? direction * pitch : 0);
        var targetY = candidate.Rect.Y + (axis == Axis.Vertical ? direction * pitch : 0);
        return candidates
            .Where(other => !ReferenceEquals(candidate, other))
            .Where(other => Math.Abs(other.Rect.X - targetX) <= tolerance &&
                            Math.Abs(other.Rect.Y - targetY) <= tolerance)
            .OrderByDescending(other => other.Score)
            .FirstOrDefault();
    }

    private static IEnumerable<CandidateInfo> NonMaximumSuppress(IEnumerable<CandidateInfo> candidates, double overlapLimit)
    {
        var selected = new List<CandidateInfo>();
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

    private static IEnumerable<CandidateInfo> SuppressOverlappingSlots(IEnumerable<CandidateInfo> candidates)
    {
        var selected = new List<CandidateInfo>();
        foreach (var candidate in candidates)
        {
            if (selected.Any(existing => SlotsConflict(existing.Rect, candidate.Rect)))
            {
                continue;
            }

            selected.Add(candidate);
            yield return candidate;
        }
    }

    private static bool SlotsConflict(Rect existing, Rect candidate)
    {
        var intersection = Rect.Intersect(existing, candidate);
        if (!intersection.IsEmpty)
        {
            var smallerArea = Math.Min(existing.Width * existing.Height, candidate.Width * candidate.Height);
            var intersectionRatio = smallerArea <= 0 ? 0 : intersection.Width * intersection.Height / smallerArea;
            if (intersectionRatio >= 0.08)
            {
                return true;
            }
        }

        var existingCenterX = existing.X + existing.Width / 2;
        var existingCenterY = existing.Y + existing.Height / 2;
        var candidateCenterX = candidate.X + candidate.Width / 2;
        var candidateCenterY = candidate.Y + candidate.Height / 2;
        var dx = existingCenterX - candidateCenterX;
        var dy = existingCenterY - candidateCenterY;
        var centerDistance = Math.Sqrt(dx * dx + dy * dy);
        var centerConflictDistance = Math.Min(existing.Width, candidate.Width) * 0.72;
        return centerDistance <= centerConflictDistance;
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

    private static double LumaAt(byte[] pixels, int stride, int x, int y)
    {
        var offset = y * stride + x * 4;
        return pixels[offset] * 0.0722 + pixels[offset + 1] * 0.7152 + pixels[offset + 2] * 0.2126;
    }

    private static double VerticalGradient(byte[] pixels, int stride, int x, int y) =>
        Math.Abs(LumaAt(pixels, stride, x, y - 1) - LumaAt(pixels, stride, x, y + 1));

    private static double HorizontalGradient(byte[] pixels, int stride, int x, int y) =>
        Math.Abs(LumaAt(pixels, stride, x - 1, y) - LumaAt(pixels, stride, x + 1, y));

    private static double DiagonalCornerScore(byte[] pixels, int width, int height, int stride, int x, int y, int offset)
    {
        var center = LumaAt(pixels, stride, x, y);
        var a = LumaAt(pixels, stride, Math.Clamp(x - offset, 1, width - 2), Math.Clamp(y - offset, 1, height - 2));
        var b = LumaAt(pixels, stride, Math.Clamp(x + offset, 1, width - 2), Math.Clamp(y + offset, 1, height - 2));
        return Math.Abs(center - a) + Math.Abs(center - b);
    }

    private static void AddEdgeSample(double sample, ref double sum, ref int hits, ref int count)
    {
        sum += sample;
        if (sample >= 24)
        {
            hits++;
        }

        count++;
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

    private enum Axis
    {
        Horizontal,
        Vertical
    }

    private sealed record CandidateInfo(Rect Rect, int Size, double Score);
}
