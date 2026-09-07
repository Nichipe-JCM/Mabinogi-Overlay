using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed partial class MonitorValueRecognitionService
{
    private const int OcrScale = 8;
    private readonly Lazy<OcrEngine> _engine = new(CreateEngine);

    public bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    public async Task<BatchBuffTimeReadResult> ReadBuffTimesAsync(
        BitmapSource source,
        Rect monitorRoi,
        IReadOnlyList<BuffIconMatch> activeAnchors,
        CancellationToken cancellationToken = default)
    {
        var columnBounds = CreateBuffTimeColumnBounds(source, monitorRoi, activeAnchors);
        if (columnBounds.IsEmpty || activeAnchors.Count == 0)
        {
            return new BatchBuffTimeReadResult(new Dictionary<string, BuffTimeReadResult>(), string.Empty, columnBounds);
        }

        var crop = Crop(source, columnBounds);
        var layout = await RecognizeLayoutAsync(crop, cancellationToken).ConfigureAwait(false);
        var lines = layout.Lines
            .Select(line => line with
            {
                Bounds = new Rect(
                    line.Bounds.X + columnBounds.X,
                    line.Bounds.Y + columnBounds.Y,
                    line.Bounds.Width,
                    line.Bounds.Height)
            })
            .ToArray();
        var reads = MatchBuffTimeLines(activeAnchors, lines);
        return new BatchBuffTimeReadResult(reads, NormalizeText(layout.Text), columnBounds);
    }

    public async Task<BuffTimeReadResult> ReadBuffTimeAsync(
        BitmapSource source,
        Rect monitorRoi,
        Rect iconBounds,
        CancellationToken cancellationToken = default)
    {
        var rowBounds = CreateBuffTimeBounds(source, monitorRoi, iconBounds);
        if (rowBounds.IsEmpty)
        {
            return new BuffTimeReadResult(null, string.Empty, rowBounds);
        }

        var crop = Crop(source, rowBounds);
        var (text, seconds) = await RecognizeCandidatesAsync(crop, ParseDurationSeconds, cancellationToken).ConfigureAwait(false);

        return new BuffTimeReadResult(seconds, NormalizeText(text), rowBounds);
    }

    public async Task<TuairimPercentReadResult> ReadTuairimPercentAsync(
        BitmapSource source,
        Rect anchorBounds,
        CancellationToken cancellationToken = default)
    {
        var valueBounds = CreateTuairimPercentBounds(source, anchorBounds);
        if (valueBounds.IsEmpty)
        {
            return new TuairimPercentReadResult(null, string.Empty, valueBounds);
        }

        var crop = Crop(source, valueBounds);
        var (text, percent) = await RecognizeCandidatesAsync(crop, ParsePercent, cancellationToken).ConfigureAwait(false);

        return new TuairimPercentReadResult(percent, NormalizeText(text), valueBounds);
    }

    public static void SaveDiagnosticImages(BitmapSource source, Rect bounds, string directory, string prefix)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var crop = Crop(source, bounds);
        SavePng(crop, Path.Combine(directory, $"{prefix}-raw.png"));
        SavePng(CreateTextMask(crop, invert: false), Path.Combine(directory, $"{prefix}-mask-light.png"));
        SavePng(CreateTextMask(crop, invert: true), Path.Combine(directory, $"{prefix}-mask-dark.png"));
    }

    private async Task<(string Text, int? Value)> RecognizeCandidatesAsync(
        BitmapSource crop,
        Func<string?, int?> parser,
        CancellationToken cancellationToken)
    {
        var recognized = new List<string>();
        var rawText = NormalizeText(await RecognizeAsync(crop, cancellationToken).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(rawText))
        {
            recognized.Add(rawText);
        }

        var rawValue = parser(rawText);
        if (rawValue is not null)
        {
            return (rawText, rawValue);
        }

        foreach (var invert in new[] { false, true })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = CreateTextMask(crop, invert);
            var text = NormalizeText(await RecognizeAsync(candidate, cancellationToken).ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(text))
            {
                recognized.Add(text);
            }

            var value = parser(text);
            if (value is not null)
            {
                return (string.Join(" | ", recognized.Distinct()), value);
            }
        }

        return (string.Join(" | ", recognized.Distinct()), null);
    }

    internal static int? ParseDurationSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeDurationOcrText(text);
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

        var numbers = NumberRegex().Matches(normalized)
            .Select(match => int.TryParse(match.Value, out var value) ? value : -1)
            .Where(value => value >= 0)
            .ToList();
        if (numbers.Count >= 2 && numbers[^1] < 60)
        {
            return numbers[^2] * 60 + numbers[^1];
        }

        // A visible minute unit with no minute digit is a truncated minute/second read,
        // not a valid seconds-only duration.
        if (normalized.Contains("\uBD84", StringComparison.Ordinal))
        {
            return null;
        }

        var secondOnlyMatch = SecondOnlyRegex().Match(normalized);
        if (secondOnlyMatch.Success && int.TryParse(secondOnlyMatch.Groups[1].Value, out var seconds))
        {
            return seconds;
        }

        if (numbers.Count != 1 || numbers[0] > 60)
        {
            return null;
        }

        var compactSingleValue = SingleNumberOnlyRegex().IsMatch(normalized);
        return compactSingleValue ? numbers[0] : null;
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

        var compactMatch = CompactPercentRegex().Match(NormalizeText(text));
        return compactMatch.Success &&
               int.TryParse(compactMatch.Groups[1].Value, out percent) &&
               percent is >= 0 and <= 100
            ? percent
            : null;
    }

    private async Task<string> RecognizeAsync(BitmapSource source, CancellationToken cancellationToken)
        => (await RecognizeLayoutAsync(source, cancellationToken).ConfigureAwait(false)).Text;

    private async Task<OcrLayoutResult> RecognizeLayoutAsync(
        BitmapSource source,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var maxDimension = Math.Max(source.PixelWidth, source.PixelHeight);
        var scale = Math.Clamp(2400 / Math.Max(1, maxDimension), 1, OcrScale);
        var padding = Math.Max(24, scale * 4);
        var scaled = AddPadding(Scale(source, scale), padding);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
        using var output = stream.AsStreamForWrite();
        encoder.Save(output);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
        var result = await _engine.Value.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var lines = result.Lines
            .Select(line => CreateRecognizedLine(line, scale, padding, source.PixelWidth, source.PixelHeight))
            .Where(line => line is not null)
            .Cast<RecognizedTextLine>()
            .ToArray();
        return new OcrLayoutResult(result.Text ?? string.Empty, lines);
    }

    private static RecognizedTextLine? CreateRecognizedLine(
        OcrLine line,
        int scale,
        int padding,
        int sourceWidth,
        int sourceHeight)
    {
        if (line.Words.Count == 0)
        {
            return null;
        }

        var left = line.Words.Min(word => word.BoundingRect.X);
        var top = line.Words.Min(word => word.BoundingRect.Y);
        var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
        var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
        var bounds = ClampRect(
            new Rect(
                (left - padding) / scale,
                (top - padding) / scale,
                (right - left) / scale,
                (bottom - top) / scale),
            sourceWidth,
            sourceHeight);
        return bounds.IsEmpty
            ? null
            : new RecognizedTextLine(NormalizeText(line.Text), bounds);
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
        var right = Math.Min(
            source.PixelWidth,
            Math.Max(monitorRoi.Right, iconBounds.Left + iconBounds.Width * 24));
        var bottom = Math.Min(
            monitorRoi.Bottom,
            iconBounds.Bottom + Math.Max(3, iconBounds.Height * 0.3));
        return ClampRect(new Rect(left, top, right - left, bottom - top), source.PixelWidth, source.PixelHeight);
    }

    private static Rect CreateBuffTimeColumnBounds(
        BitmapSource source,
        Rect monitorRoi,
        IReadOnlyList<BuffIconMatch> anchors)
    {
        if (anchors.Count == 0)
        {
            return Rect.Empty;
        }

        var averageIconHeight = anchors.Average(anchor => anchor.Bounds.Height);
        var left = Math.Max(
            monitorRoi.Left + monitorRoi.Width * 0.52,
            anchors.Max(anchor => anchor.Bounds.Right) + Math.Max(4, averageIconHeight * 0.5));
        var top = anchors.Min(anchor => anchor.Bounds.Top) - Math.Max(3, averageIconHeight * 0.35);
        var right = monitorRoi.Right;
        var bottom = anchors.Max(anchor => anchor.Bounds.Bottom) + Math.Max(4, averageIconHeight * 0.4);
        return ClampRect(new Rect(left, top, right - left, bottom - top), source.PixelWidth, source.PixelHeight);
    }

    internal static IReadOnlyDictionary<string, BuffTimeReadResult> MatchBuffTimeLines(
        IReadOnlyList<BuffIconMatch> anchors,
        IReadOnlyList<RecognizedTextLine> lines)
    {
        var candidates = lines
            .Select((line, index) => new
            {
                Index = index,
                Line = line,
                Seconds = ParseDurationSeconds(line.Text)
            })
            .Where(candidate => candidate.Seconds is not null)
            .ToArray();
        var usedLines = new HashSet<int>();
        var usedAnchors = new HashSet<string>(StringComparer.Ordinal);
        var results = new Dictionary<string, BuffTimeReadResult>(StringComparer.Ordinal);
        var matches = anchors
            .SelectMany(anchor => candidates.Select(candidate => new
            {
                Anchor = anchor,
                Candidate = candidate,
                Distance = Math.Abs(
                    candidate.Line.Bounds.Top + candidate.Line.Bounds.Height / 2 -
                    (anchor.Bounds.Top + anchor.Bounds.Height / 2))
            }))
            .Where(match => match.Distance <= Math.Max(10, match.Anchor.Bounds.Height * 1.25))
            .OrderBy(match => match.Distance)
            .ToArray();
        foreach (var match in matches)
        {
            if (usedAnchors.Contains(match.Anchor.NameKey) || usedLines.Contains(match.Candidate.Index))
            {
                continue;
            }

            usedAnchors.Add(match.Anchor.NameKey);
            usedLines.Add(match.Candidate.Index);
            results[match.Anchor.NameKey] = new BuffTimeReadResult(
                match.Candidate.Seconds,
                match.Candidate.Line.Text,
                match.Candidate.Line.Bounds);
        }

        return results;
    }

    private static Rect CreateTuairimPercentBounds(BitmapSource source, Rect anchorBounds)
    {
        var left = anchorBounds.Left + anchorBounds.Width * 0.55;
        var top = anchorBounds.Top + anchorBounds.Height * 0.74;
        var right = anchorBounds.Right + anchorBounds.Width * 0.35;
        var bottom = anchorBounds.Bottom + anchorBounds.Height * 0.32;
        return ClampRect(
            new Rect(left, top, right - left, bottom - top),
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
        if (scale <= 1)
        {
            return source;
        }

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var sourceStride = converted.PixelWidth * 4;
        var sourcePixels = new byte[sourceStride * converted.PixelHeight];
        converted.CopyPixels(sourcePixels, sourceStride, 0);
        var width = converted.PixelWidth * scale;
        var height = converted.PixelHeight * scale;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var sourceY = y / scale;
            for (var x = 0; x < width; x++)
            {
                var sourceOffset = sourceY * sourceStride + x / scale * 4;
                var targetOffset = y * stride + x * 4;
                pixels[targetOffset] = sourcePixels[sourceOffset];
                pixels[targetOffset + 1] = sourcePixels[sourceOffset + 1];
                pixels[targetOffset + 2] = sourcePixels[sourceOffset + 2];
                pixels[targetOffset + 3] = sourcePixels[sourceOffset + 3];
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateTextMask(BitmapSource source, bool invert)
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
            var minimum = Math.Min(red, Math.Min(green, blue));
            var maximum = Math.Max(red, Math.Max(green, blue));
            var brightNeutral = minimum >= 110 && maximum - minimum <= 105;
            var redText = red >= 105 && red - green >= 35 && red - blue >= 25;
            var isText = brightNeutral || redText;
            var value = (byte)(isText == invert ? 255 : 0);
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

    private static BitmapSource AddPadding(BitmapSource source, int padding)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var sourceStride = converted.PixelWidth * 4;
        var sourcePixels = new byte[sourceStride * converted.PixelHeight];
        converted.CopyPixels(sourcePixels, sourceStride, 0);
        var width = converted.PixelWidth + padding * 2;
        var height = converted.PixelHeight + padding * 2;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)255);
        for (var y = 0; y < converted.PixelHeight; y++)
        {
            System.Buffer.BlockCopy(
                sourcePixels,
                y * sourceStride,
                pixels,
                (y + padding) * stride + padding * 4,
                sourceStride);
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string NormalizeText(string? text) =>
        Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeDurationOcrText(string? text)
    {
        var normalized = NormalizeText(text);
        normalized = Regex.Replace(
            normalized,
            @"\uD06C(?=\s*\uBD84)",
            "3",
            RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"(?<=\d)\s*[\uC870\uD638](?=\s*$)",
            "\uCD08",
            RegexOptions.CultureInvariant);
        return Regex.Replace(
            normalized,
            @"(?<=\uBD84)\s*\uD45C\uD1A0(?=\s*$)",
            " 30\uCD08",
            RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"(\d{1,2})\s*[:：]\s*(\d{1,2})", RegexOptions.CultureInvariant)]
    private static partial Regex ColonDurationRegex();

    [GeneratedRegex(@"(\d{1,2})[^\d]{0,3}(?:분|m|min)[^\d]{0,5}(\d{1,2})[^\d]{0,3}(?:초|s|sec)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalizedDurationRegex();

    [GeneratedRegex(@"(\d{1,2})[^\d]{0,3}(?:초|s|sec)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecondOnlyRegex();

    [GeneratedRegex(@"(\d{1,3})\s*[%％]", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"^[^\p{L}\d]{0,2}(\d{1,3})[^\p{L}\d]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CompactPercentRegex();

    [GeneratedRegex(@"\d{1,3}", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^[^\p{L}\d]{0,2}\d{1,2}[^\p{L}\d]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleNumberOnlyRegex();
}

public sealed record BuffTimeReadResult(int? RemainingSeconds, string RecognizedText, Rect Bounds);

public sealed record BatchBuffTimeReadResult(
    IReadOnlyDictionary<string, BuffTimeReadResult> Reads,
    string RecognizedText,
    Rect Bounds);

internal sealed record RecognizedTextLine(string Text, Rect Bounds);

internal sealed record OcrLayoutResult(string Text, IReadOnlyList<RecognizedTextLine> Lines);

public sealed record TuairimPercentReadResult(int? Percent, string RecognizedText, Rect Bounds);
