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
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(0, 0, bitmap.PixelWidth, Math.Min(bitmap.PixelHeight, bitmap.PixelHeight / 5)));
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(0, 0, Math.Min(bitmap.PixelWidth, bitmap.PixelWidth / 5), bitmap.PixelHeight));
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(Math.Max(0, bitmap.PixelWidth * 4 / 5), 0, bitmap.PixelWidth / 5, bitmap.PixelHeight));
        }

        var gridCandidates = ScoreGridNeighbors(raw);
        var picked = NonMaximumSuppress(gridCandidates.OrderByDescending(item => item.Score), 0.35)
            .Take(180)
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
                if (score >= 52)
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

        return averageEdge * 0.8 + continuity * 55 + cornerScore * 0.15;
    }

    private static List<CandidateInfo> ScoreGridNeighbors(IReadOnlyList<CandidateInfo> candidates)
    {
        var bySize = candidates.GroupBy(candidate => candidate.Size);
        var scored = new List<CandidateInfo>();

        foreach (var group in bySize)
        {
            var list = NonMaximumSuppress(group.OrderByDescending(item => item.Score), 0.25)
                .Take(600)
                .ToList();

            foreach (var candidate in list)
            {
                var neighbors = CountGridNeighbors(candidate, list);
                if (neighbors == 0)
                {
                    continue;
                }

                scored.Add(candidate with { Score = candidate.Score + neighbors * 45 });
            }
        }

        return scored;
    }

    private static int CountGridNeighbors(CandidateInfo candidate, IReadOnlyList<CandidateInfo> candidates)
    {
        var count = 0;
        var size = candidate.Size;
        var tolerance = Math.Max(3, size / 8);

        foreach (var other in candidates)
        {
            if (ReferenceEquals(candidate, other))
            {
                continue;
            }

            var dx = Math.Abs(other.Rect.X - candidate.Rect.X);
            var dy = Math.Abs(other.Rect.Y - candidate.Rect.Y);
            for (var gap = -1; gap <= Math.Max(8, size / 4); gap++)
            {
                var pitch = size + gap;
                if (Math.Abs(dx - pitch) <= tolerance && dy <= tolerance)
                {
                    count++;
                    break;
                }

                if (Math.Abs(dy - pitch) <= tolerance && dx <= tolerance)
                {
                    count++;
                    break;
                }
            }
        }

        return Math.Min(count, 4);
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
        if (sample >= 32)
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

    private sealed record CandidateInfo(Rect Rect, int Size, double Score);
}
