using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TestOverlay.App.Services;

public static class MonitorFrameVisibilityEvaluator
{
    private const double DarkLuminance = 10;
    private const double BrightLuminance = 245;
    private const double MaximumDarkRatio = 0.92;
    private const double MaximumBrightRatio = 0.85;
    private const double MinimumExtremeFrameDeviation = 4;
    private const int MaximumSamples = 20_000;

    public static MonitorFrameVisibilityResult Evaluate(BitmapSource source, Rect roi)
    {
        var bounds = Clamp(roi, source.PixelWidth, source.PixelHeight);
        if (bounds.IsEmpty)
        {
            return MonitorFrameVisibilityResult.Obscured("empty-roi");
        }

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixelBounds = new Int32Rect(
            (int)bounds.X,
            (int)bounds.Y,
            (int)bounds.Width,
            (int)bounds.Height);
        var stride = pixelBounds.Width * 4;
        var pixels = new byte[stride * pixelBounds.Height];
        converted.CopyPixels(pixelBounds, pixels, stride, 0);

        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(
            pixelBounds.Width * (double)pixelBounds.Height / MaximumSamples)));
        var count = 0;
        var dark = 0;
        var bright = 0;
        double sum = 0;
        double sumSquares = 0;
        for (var y = 0; y < pixelBounds.Height; y += step)
        {
            for (var x = 0; x < pixelBounds.Width; x += step)
            {
                var offset = y * stride + x * 4;
                var luminance = (pixels[offset + 2] * 54 + pixels[offset + 1] * 183 + pixels[offset] * 19) / 256.0;
                count++;
                sum += luminance;
                sumSquares += luminance * luminance;
                dark += luminance <= DarkLuminance ? 1 : 0;
                bright += luminance >= BrightLuminance ? 1 : 0;
            }
        }

        var mean = sum / Math.Max(1, count);
        var deviation = Math.Sqrt(Math.Max(0, sumSquares / Math.Max(1, count) - mean * mean));
        var darkRatio = dark / (double)Math.Max(1, count);
        var brightRatio = bright / (double)Math.Max(1, count);
        var reason = darkRatio >= MaximumDarkRatio
            ? "dark-clipping"
            : brightRatio >= MaximumBrightRatio
                ? "bright-clipping"
                : deviation < MinimumExtremeFrameDeviation && mean is < 25 or > 230
                    ? "contrast-collapse"
                    : string.Empty;
        return new MonitorFrameVisibilityResult(
            reason.Length > 0,
            reason,
            mean,
            deviation,
            darkRatio,
            brightRatio);
    }

    private static Rect Clamp(Rect roi, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(roi.Left), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Floor(roi.Top), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling(roi.Right), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(roi.Bottom), top + 1, height);
        return right <= left || bottom <= top
            ? Rect.Empty
            : new Rect(left, top, right - left, bottom - top);
    }
}

public sealed record MonitorFrameVisibilityResult(
    bool IsObscured,
    string Reason,
    double MeanLuminance,
    double LuminanceDeviation,
    double DarkRatio,
    double BrightRatio)
{
    public static MonitorFrameVisibilityResult Obscured(string reason) =>
        new(true, reason, 0, 0, 0, 0);
}
