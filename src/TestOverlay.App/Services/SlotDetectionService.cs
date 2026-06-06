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
        var borderSum = 0.0;
        var borderSumSq = 0.0;
        var borderCount = 0;
        var innerSum = 0.0;
        var innerSumSq = 0.0;
        var innerCount = 0;
        var innerStart = Math.Max(2, size / 5);
        var innerEnd = Math.Max(innerStart + 1, size - innerStart);

        for (var i = 0; i < size; i += Math.Max(1, size / 12))
        {
            AddSample(LumaAt(pixels, stride, x + i, y), ref borderSum, ref borderSumSq, ref borderCount);
            AddSample(LumaAt(pixels, stride, x + i, y + size - 1), ref borderSum, ref borderSumSq, ref borderCount);
            AddSample(LumaAt(pixels, stride, x, y + i), ref borderSum, ref borderSumSq, ref borderCount);
            AddSample(LumaAt(pixels, stride, x + size - 1, y + i), ref borderSum, ref borderSumSq, ref borderCount);
        }

        for (var yy = innerStart; yy < innerEnd; yy += Math.Max(1, size / 5))
        {
            for (var xx = innerStart; xx < innerEnd; xx += Math.Max(1, size / 5))
            {
                AddSample(LumaAt(pixels, stride, x + xx, y + yy), ref innerSum, ref innerSumSq, ref innerCount);
            }
        }

        var borderMean = borderSum / Math.Max(1, borderCount);
        var innerMean = innerSum / Math.Max(1, innerCount);
        var borderVariance = Math.Max(0, borderSumSq / Math.Max(1, borderCount) - borderMean * borderMean);
        var innerVariance = Math.Max(0, innerSumSq / Math.Max(1, innerCount) - innerMean * innerMean);
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

    private static void AddSample(double sample, ref double sum, ref double sumSq, ref int count)
    {
        sum += sample;
        sumSq += sample * sample;
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
}
