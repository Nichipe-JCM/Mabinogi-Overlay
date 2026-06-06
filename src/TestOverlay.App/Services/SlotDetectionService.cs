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

        var raw = new List<(Rect Rect, double Score)>();
        var min = Math.Clamp(minSize, 12, 160);
        var max = Math.Clamp(maxSize, min, 180);

        for (var size = min; size <= max; size += Math.Max(2, size / 16))
        {
            var step = Math.Max(3, size / 5);
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(0, 0, bitmap.PixelWidth, Math.Min(bitmap.PixelHeight, bitmap.PixelHeight / 5)));
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(0, 0, Math.Min(bitmap.PixelWidth, bitmap.PixelWidth / 5), bitmap.PixelHeight));
            ScanRegion(raw, pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, size, step, new Int32Rect(Math.Max(0, bitmap.PixelWidth * 4 / 5), 0, bitmap.PixelWidth / 5, bitmap.PixelHeight));
        }

        var picked = NonMaximumSuppress(raw.OrderByDescending(item => item.Score), 0.45)
            .Take(180)
            .Select((item, index) => new SlotCandidate(index + 1, item.Rect, item.Score))
            .ToList();

        return picked;
    }

    private static void ScanRegion(
        ICollection<(Rect Rect, double Score)> raw,
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
                var score = ScoreSquare(pixels, stride, x, y, size);
                if (score >= 34)
                {
                    raw.Add((new Rect(x, y, size, size), score));
                }
            }
        }
    }

    private static double ScoreSquare(byte[] pixels, int stride, int x, int y, int size)
    {
        var borderSamples = new List<double>(size * 4);
        var innerSamples = new List<double>(16);
        var innerStart = Math.Max(2, size / 5);
        var innerEnd = Math.Max(innerStart + 1, size - innerStart);

        for (var i = 0; i < size; i += Math.Max(1, size / 12))
        {
            borderSamples.Add(LumaAt(pixels, stride, x + i, y));
            borderSamples.Add(LumaAt(pixels, stride, x + i, y + size - 1));
            borderSamples.Add(LumaAt(pixels, stride, x, y + i));
            borderSamples.Add(LumaAt(pixels, stride, x + size - 1, y + i));
        }

        for (var yy = innerStart; yy < innerEnd; yy += Math.Max(1, size / 5))
        {
            for (var xx = innerStart; xx < innerEnd; xx += Math.Max(1, size / 5))
            {
                innerSamples.Add(LumaAt(pixels, stride, x + xx, y + yy));
            }
        }

        var borderMean = borderSamples.Average();
        var innerMean = innerSamples.Average();
        var borderVariance = Variance(borderSamples, borderMean);
        var innerVariance = Variance(innerSamples, innerMean);
        var contrast = Math.Abs(borderMean - innerMean);

        return contrast * 0.9 + Math.Sqrt(borderVariance) * 0.4 + Math.Sqrt(innerVariance) * 0.25;
    }

    private static IEnumerable<(Rect Rect, double Score)> NonMaximumSuppress(IEnumerable<(Rect Rect, double Score)> candidates, double overlapLimit)
    {
        var selected = new List<(Rect Rect, double Score)>();
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

    private static double Variance(IReadOnlyCollection<double> samples, double mean) =>
        samples.Sum(sample => Math.Pow(sample - mean, 2)) / Math.Max(1, samples.Count);

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
}
