using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace TestOverlay.App.Services;

public sealed partial class MonitorValueRecognitionService
{
    private const int OcrScale = 4;
    private readonly Lazy<OcrEngine> _engine = new(CreateEngine);

    public async Task<BuffTimeReadResult> ReadBuffTimeAsync(
        BitmapSource source,
        Rect monitorRoi,
        Rect iconBounds)
    {
        var rowBounds = CreateBuffTimeBounds(source, monitorRoi, iconBounds);
        if (rowBounds.IsEmpty)
        {
            return new BuffTimeReadResult(null, string.Empty, rowBounds);
        }

        var crop = Crop(source, rowBounds);
        var text = await RecognizeAsync(crop).ConfigureAwait(false);
        var seconds = ParseDurationSeconds(text);
        if (seconds is null)
        {
            var highContrast = CreateTextMask(crop);
            var maskText = await RecognizeAsync(highContrast).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(maskText))
            {
                text = string.IsNullOrWhiteSpace(text) ? maskText : $"{text} | {maskText}";
            }
            seconds = ParseDurationSeconds(maskText);
        }

        return new BuffTimeReadResult(seconds, NormalizeText(text), rowBounds);
    }

    public async Task<TuairimPercentReadResult> ReadTuairimPercentAsync(
        BitmapSource source,
        Rect anchorBounds)
    {
        var valueBounds = CreateTuairimPercentBounds(source, anchorBounds);
        if (valueBounds.IsEmpty)
        {
            return new TuairimPercentReadResult(null, string.Empty, valueBounds);
        }

        var crop = Crop(source, valueBounds);
        var text = await RecognizeAsync(crop).ConfigureAwait(false);
        var percent = ParsePercent(text);
        if (percent is null)
        {
            var highContrast = CreateTextMask(crop);
            var maskText = await RecognizeAsync(highContrast).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(maskText))
            {
                text = string.IsNullOrWhiteSpace(text) ? maskText : $"{text} | {maskText}";
            }
            percent = ParsePercent(maskText);
        }

        return new TuairimPercentReadResult(percent, NormalizeText(text), valueBounds);
    }

    internal static int? ParseDurationSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeText(text);
        var colonMatch = ColonDurationRegex().Match(normalized);
        if (colonMatch.Success &&
            int.TryParse(colonMatch.Groups[1].Value, out var colonMinutes) &&
            int.TryParse(colonMatch.Groups[2].Value, out var colonSeconds) &&
            colonSeconds < 60)
        {
            return colonMinutes * 60 + colonSeconds;
        }

        var localizedMatch = LocalizedDurationRegex().Match(normalized);
        if (localizedMatch.Success &&
            int.TryParse(localizedMatch.Groups[1].Value, out var localizedMinutes) &&
            int.TryParse(localizedMatch.Groups[2].Value, out var localizedSeconds) &&
            localizedSeconds < 60)
        {
            return localizedMinutes * 60 + localizedSeconds;
        }

        var secondOnlyMatch = SecondOnlyRegex().Match(normalized);
        if (secondOnlyMatch.Success && int.TryParse(secondOnlyMatch.Groups[1].Value, out var seconds))
        {
            return seconds;
        }

        var numbers = NumberRegex().Matches(normalized)
            .Select(match => int.TryParse(match.Value, out var value) ? value : -1)
            .Where(value => value >= 0)
            .ToList();
        if (numbers.Count >= 2 && numbers[^1] < 60)
        {
            return numbers[^2] * 60 + numbers[^1];
        }

        return numbers.Count == 1 && numbers[0] <= 60 ? numbers[0] : null;
    }

    internal static int? ParsePercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = PercentRegex().Match(NormalizeText(text));
        if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
        {
            return percent is >= 0 and <= 100 ? percent : null;
        }

        var numberMatches = NumberRegex().Matches(NormalizeText(text));
        var numberMatch = numberMatches.Count == 0 ? null : numberMatches[^1];
        return numberMatch is not null &&
               int.TryParse(numberMatch.Value, out percent) &&
               percent is >= 0 and <= 100
            ? percent
            : null;
    }

    private async Task<string> RecognizeAsync(BitmapSource source)
    {
        var maxDimension = Math.Max(source.PixelWidth, source.PixelHeight);
        var scale = Math.Clamp(2400 / Math.Max(1, maxDimension), 1, OcrScale);
        var scaled = Scale(source, scale);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
        using var output = stream.AsStreamForWrite();
        encoder.Save(output);
        await output.FlushAsync().ConfigureAwait(false);

        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);
        var result = await _engine.Value.RecognizeAsync(bitmap);
        return result.Text ?? string.Empty;
    }

    private static OcrEngine CreateEngine()
    {
        var userEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (userEngine is not null)
        {
            return userEngine;
        }

        return OcrEngine.TryCreateFromLanguage(new Language("en-US"))
               ?? throw new InvalidOperationException("Windows OCR is not available for the current user.");
    }

    private static Rect CreateBuffTimeBounds(BitmapSource source, Rect monitorRoi, Rect iconBounds)
    {
        var left = iconBounds.Right + Math.Max(2, iconBounds.Width * 0.15);
        var top = iconBounds.Top - Math.Max(2, iconBounds.Height * 0.2);
        var right = Math.Min(monitorRoi.Right, source.PixelWidth);
        var bottom = Math.Min(
            monitorRoi.Bottom,
            iconBounds.Bottom + Math.Max(3, iconBounds.Height * 0.3));
        return ClampRect(new Rect(left, top, right - left, bottom - top), source.PixelWidth, source.PixelHeight);
    }

    private static Rect CreateTuairimPercentBounds(BitmapSource source, Rect anchorBounds)
    {
        var left = anchorBounds.Left + anchorBounds.Width * 0.28;
        var top = anchorBounds.Top + anchorBounds.Height * 0.48;
        return ClampRect(
            new Rect(left, top, anchorBounds.Right - left, anchorBounds.Bottom - top),
            source.PixelWidth,
            source.PixelHeight);
    }

    private static Rect ClampRect(Rect rect, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(rect.Left), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Floor(rect.Top), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling(rect.Right), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), top + 1, height);
        return right <= left || bottom <= top
            ? Rect.Empty
            : new Rect(left, top, right - left, bottom - top);
    }

    private static BitmapSource Crop(BitmapSource source, Rect bounds)
    {
        var crop = new CroppedBitmap(
            source,
            new Int32Rect((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height));
        crop.Freeze();
        return crop;
    }

    private static BitmapSource Scale(BitmapSource source, int scale)
    {
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static BitmapSource CreateTextMask(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var brightNeutral = Math.Min(red, Math.Min(green, blue)) >= 150 &&
                                Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) <= 85;
            var redText = red >= 135 && red - green >= 45 && red - blue >= 35;
            var value = (byte)(brightNeutral || redText ? 0 : 255);
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static string NormalizeText(string? text) =>
        Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    [GeneratedRegex(@"(\d{1,2})\s*[:：]\s*(\d{1,2})", RegexOptions.CultureInvariant)]
    private static partial Regex ColonDurationRegex();

    [GeneratedRegex(@"(\d{1,2})\s*(?:분|m|min)\s*(\d{1,2})\s*(?:초|s|sec)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalizedDurationRegex();

    [GeneratedRegex(@"(\d{1,2})\s*(?:초|s|sec)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecondOnlyRegex();

    [GeneratedRegex(@"(\d{1,3})\s*[%％]", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"\d{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();
}

public sealed record BuffTimeReadResult(int? RemainingSeconds, string RecognizedText, Rect Bounds);

public sealed record TuairimPercentReadResult(int? Percent, string RecognizedText, Rect Bounds);
