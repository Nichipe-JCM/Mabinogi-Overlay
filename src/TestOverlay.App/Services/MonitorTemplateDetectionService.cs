using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class MonitorTemplateDetectionService
{
    private const double MinimumBuffStructureScore = 0.80;
    private const double MinimumTuairimScore = 0.85;
    private readonly Lazy<TemplateCatalog> _catalog = new(LoadCatalog);

    public BuffWindowDetectionResult DetectBuffs(BitmapSource source, Rect roi)
    {
        var image = PixelImage.FromBitmapSource(source);
        var searchRoi = ClampRoi(roi, image.Width, image.Height);
        var rawMatches = new List<BuffIconMatch>();
        foreach (var descriptor in _catalog.Value.Buffs)
        {
            var candidate = FindBestBuffCandidate(image, searchRoi, descriptor.On);
            if (candidate is null || candidate.Score < MinimumBuffStructureScore)
            {
                continue;
            }

            var onScaled = descriptor.On.Scale(candidate.Width, candidate.Height);
            var offScaled = descriptor.Off.Scale(candidate.Width, candidate.Height);
            var onError = CalculateColorError(image, candidate.X, candidate.Y, onScaled);
            var offError = CalculateColorError(image, candidate.X, candidate.Y, offScaled);
            var active = onError <= offError;
            var stateConfidence = Math.Abs(onError - offError) / Math.Max(1, Math.Max(onError, offError));
            rawMatches.Add(new BuffIconMatch(
                descriptor.NameKey,
                new Rect(candidate.X, candidate.Y, candidate.Width, candidate.Height),
                candidate.Score,
                active,
                stateConfidence));
        }

        return new BuffWindowDetectionResult(searchRoi, SelectAlignedBuffMatches(rawMatches));
    }

    public IReadOnlyList<BuffIconMatch> EvaluateBuffAnchors(
        BitmapSource source,
        IEnumerable<BuffIconMatch> anchors)
    {
        var image = PixelImage.FromBitmapSource(source);
        var descriptors = _catalog.Value.Buffs.ToDictionary(descriptor => descriptor.NameKey, StringComparer.Ordinal);
        var results = new List<BuffIconMatch>();
        foreach (var anchor in anchors)
        {
            if (!descriptors.TryGetValue(anchor.NameKey, out var descriptor))
            {
                continue;
            }

            var bounds = ClampRoi(anchor.Bounds, image.Width, image.Height);
            var width = Math.Max(8, (int)Math.Round(bounds.Width));
            var height = Math.Max(8, (int)Math.Round(bounds.Height));
            if (bounds.X + width > image.Width || bounds.Y + height > image.Height)
            {
                continue;
            }

            var onScaled = descriptor.On.Scale(width, height);
            var offScaled = descriptor.Off.Scale(width, height);
            var onKernel = MatchKernel.Create(onScaled, MaskKind.AllOpaque, coarse: false);
            var offKernel = MatchKernel.Create(offScaled, MaskKind.AllOpaque, coarse: false);
            var x = (int)Math.Round(bounds.X);
            var y = (int)Math.Round(bounds.Y);
            var onScore = CalculateCorrelation(image, x, y, onKernel);
            var offScore = CalculateCorrelation(image, x, y, offKernel);
            var structureScore = Math.Max(onScore, offScore);
            if (structureScore < 0.55)
            {
                continue;
            }

            var onError = CalculateColorError(image, x, y, onScaled);
            var offError = CalculateColorError(image, x, y, offScaled);
            var active = onError <= offError;
            var stateConfidence = Math.Abs(onError - offError) / Math.Max(1, Math.Max(onError, offError));
            results.Add(new BuffIconMatch(
                anchor.NameKey,
                new Rect(x, y, width, height),
                structureScore,
                active,
                stateConfidence));
        }

        return results;
    }

    public TuairimDetectionResult? DetectTuairim(BitmapSource source, Rect roi)
    {
        var image = PixelImage.FromBitmapSource(source);
        var searchRoi = ClampRoi(roi, image.Width, image.Height);
        var template = _catalog.Value.Tuairim;
        var nativeKernel = MatchKernel.Create(
            template.Scale(template.Width, template.Height),
            MaskKind.TuairimStableShape,
            coarse: true);
        MatchCandidate? best = Search(image, searchRoi, nativeKernel, step: 1, best: null);
        if (best is not null && best.Score >= 0.85)
        {
            best = Refine(image, searchRoi, template, best, MaskKind.TuairimStableShape, sizeRadius: 2);
            return new TuairimDetectionResult(
                searchRoi,
                new Rect(best.X, best.Y, best.Width, best.Height),
                best.Score);
        }

        for (var scaleStep = 6; scaleStep <= 18; scaleStep++)
        {
            var scale = scaleStep / 10.0;
            var width = Math.Max(24, (int)Math.Round(template.Width * scale));
            var height = Math.Max(20, (int)Math.Round(template.Height * scale));
            if (width > searchRoi.Width || height > searchRoi.Height)
            {
                continue;
            }

            var scaled = template.Scale(width, height);
            var kernel = MatchKernel.Create(scaled, MaskKind.TuairimStableShape, coarse: true);
            best = Search(image, searchRoi, kernel, step: 1, best);
        }

        if (best is null)
        {
            return null;
        }

        best = Refine(image, searchRoi, template, best, MaskKind.TuairimStableShape, sizeRadius: 4);
        return best.Score >= MinimumTuairimScore
            ? new TuairimDetectionResult(
                searchRoi,
                new Rect(best.X, best.Y, best.Width, best.Height),
                best.Score)
            : null;
    }

    private static MatchCandidate? FindBestBuffCandidate(PixelImage image, Rect roi, TemplateImage template)
    {
        var leftSearchWidth = Math.Min(roi.Width, Math.Max(64, roi.Width * 0.4));
        var leftSearchRoi = new Rect(roi.X, roi.Y, leftSearchWidth, roi.Height);
        var nativeKernel = MatchKernel.Create(template.Scale(16, 16), MaskKind.AllOpaque, coarse: true);
        MatchCandidate? best = Search(image, leftSearchRoi, nativeKernel, step: 1, best: null);
        if (best is not null && best.Score >= 0.90)
        {
            return Refine(image, leftSearchRoi, template, best, MaskKind.AllOpaque, sizeRadius: 1);
        }

        for (var size = 12; size <= 32; size += 2)
        {
            if (size > leftSearchRoi.Width || size > leftSearchRoi.Height)
            {
                continue;
            }

            var scaled = template.Scale(size, size);
            var kernel = MatchKernel.Create(scaled, MaskKind.AllOpaque, coarse: true);
            best = Search(image, leftSearchRoi, kernel, step: 1, best);
        }

        return best is null
            ? null
            : Refine(image, leftSearchRoi, template, best, MaskKind.AllOpaque, sizeRadius: 2);
    }

    private static MatchCandidate? Search(
        PixelImage image,
        Rect roi,
        MatchKernel kernel,
        int step,
        MatchCandidate? best)
    {
        var left = Math.Max(0, (int)Math.Floor(roi.Left));
        var top = Math.Max(0, (int)Math.Floor(roi.Top));
        var right = Math.Min(image.Width, (int)Math.Ceiling(roi.Right));
        var bottom = Math.Min(image.Height, (int)Math.Ceiling(roi.Bottom));
        for (var y = top; y <= bottom - kernel.Height; y += step)
        {
            for (var x = left; x <= right - kernel.Width; x += step)
            {
                var score = CalculateCorrelation(image, x, y, kernel);
                if (best is null || score > best.Score)
                {
                    best = new MatchCandidate(x, y, kernel.Width, kernel.Height, score);
                }
            }
        }

        return best;
    }

    private static MatchCandidate Refine(
        PixelImage image,
        Rect roi,
        TemplateImage template,
        MatchCandidate initial,
        MaskKind maskKind,
        int sizeRadius)
    {
        var best = initial;
        var minWidth = Math.Max(8, initial.Width - sizeRadius);
        var maxWidth = initial.Width + sizeRadius;
        for (var width = minWidth; width <= maxWidth; width++)
        {
            var height = Math.Max(8, (int)Math.Round(template.Height * (width / (double)template.Width)));
            var scaled = template.Scale(width, height);
            var kernel = MatchKernel.Create(scaled, maskKind, coarse: false);
            for (var y = initial.Y - 3; y <= initial.Y + 3; y++)
            {
                for (var x = initial.X - 3; x <= initial.X + 3; x++)
                {
                    if (x < roi.Left || y < roi.Top || x + width > roi.Right || y + height > roi.Bottom)
                    {
                        continue;
                    }

                    var score = CalculateCorrelation(image, x, y, kernel);
                    if (score > best.Score)
                    {
                        best = new MatchCandidate(x, y, width, height, score);
                    }
                }
            }
        }

        return best;
    }

    private static double CalculateCorrelation(PixelImage image, int left, int top, MatchKernel kernel)
    {
        double sourceSum = 0;
        double sourceSquaredSum = 0;
        double productSum = 0;
        foreach (var sample in kernel.Samples)
        {
            var sourceValue = image.Gray[(top + sample.Y) * image.Width + left + sample.X];
            sourceSum += sourceValue;
            sourceSquaredSum += sourceValue * sourceValue;
            productSum += sourceValue * sample.Value;
        }

        var count = kernel.Samples.Length;
        var numerator = count * productSum - sourceSum * kernel.TemplateSum;
        var sourceVariance = count * sourceSquaredSum - sourceSum * sourceSum;
        var denominator = Math.Sqrt(Math.Max(0, sourceVariance) * kernel.TemplateVariance);
        return denominator <= 0.0001 ? -1 : numerator / denominator;
    }

    private static double CalculateColorError(PixelImage image, int left, int top, ScaledTemplate template)
    {
        double error = 0;
        var count = 0;
        for (var y = 0; y < template.Height; y++)
        {
            for (var x = 0; x < template.Width; x++)
            {
                var templateIndex = y * template.Width + x;
                if (template.Alpha[templateIndex] < 128)
                {
                    continue;
                }

                var sourceIndex = (top + y) * image.Width + left + x;
                error += Math.Abs(image.Red[sourceIndex] - template.Red[templateIndex]);
                error += Math.Abs(image.Green[sourceIndex] - template.Green[templateIndex]);
                error += Math.Abs(image.Blue[sourceIndex] - template.Blue[templateIndex]);
                count += 3;
            }
        }

        return count == 0 ? double.MaxValue : error / count;
    }

    private static IReadOnlyList<BuffIconMatch> SelectAlignedBuffMatches(List<BuffIconMatch> matches)
    {
        if (matches.Count <= 1)
        {
            return matches;
        }

        var bestCluster = matches
            .Select(seed => new
            {
                Seed = seed,
                Members = matches.Where(candidate =>
                    Math.Abs(candidate.Bounds.X - seed.Bounds.X) <= Math.Max(4, seed.Bounds.Width * 0.4) &&
                    Math.Abs(candidate.Bounds.Width - seed.Bounds.Width) <= Math.Max(3, seed.Bounds.Width * 0.25)).ToList()
            })
            .OrderByDescending(cluster => cluster.Members.Count)
            .ThenByDescending(cluster => cluster.Members.Sum(member => member.StructureScore))
            .First();

        return bestCluster.Members
            .OrderBy(match => match.Bounds.Y)
            .ToList();
    }

    private static Rect ClampRoi(Rect roi, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(roi.Left), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Floor(roi.Top), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling(roi.Right), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling(roi.Bottom), top + 1, height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static TemplateCatalog LoadCatalog() => new(
        [
            new BuffTemplateDescriptor(
                "monitor.buff.battle.overture",
                LoadTemplate("BattlefieldOn.png"),
                LoadTemplate("BattlefieldOff.png")),
            new BuffTemplateDescriptor(
                "monitor.buff.march.song",
                LoadTemplate("MarchOn.png"),
                LoadTemplate("MarchOff.png")),
            new BuffTemplateDescriptor(
                "monitor.buff.vivace",
                LoadTemplate("VivaceOn.png"),
                LoadTemplate("VivaceOff.png")),
            new BuffTemplateDescriptor(
                "monitor.buff.harvest.song",
                LoadTemplate("RichyearOn.png"),
                LoadTemplate("RichyearOff.png"))
        ],
        LoadTemplate("Tuairim.png"));

    private static TemplateImage LoadTemplate(string fileName)
    {
        var assemblyName = Uri.EscapeDataString(typeof(MonitorTemplateDetectionService).Assembly.GetName().Name!);
        var uri = new Uri($"pack://application:,,,/{assemblyName};component/Image/{fileName}", UriKind.Absolute);
        var streamInfo = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"Monitor detection template was not found: {fileName}");
        using var stream = streamInfo.Stream;
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return new TemplateImage(PixelImage.FromBitmapSource(decoder.Frames[0]));
    }

    private enum MaskKind
    {
        AllOpaque,
        TuairimStableShape
    }

    private sealed record TemplateCatalog(
        IReadOnlyList<BuffTemplateDescriptor> Buffs,
        TemplateImage Tuairim);

    private sealed record BuffTemplateDescriptor(
        string NameKey,
        TemplateImage On,
        TemplateImage Off);

    private sealed record MatchCandidate(int X, int Y, int Width, int Height, double Score);

    private sealed record MatchSample(int X, int Y, double Value);

    private sealed class MatchKernel
    {
        private MatchKernel(int width, int height, MatchSample[] samples)
        {
            Width = width;
            Height = height;
            Samples = samples;
            TemplateSum = samples.Sum(sample => sample.Value);
            var squaredSum = samples.Sum(sample => sample.Value * sample.Value);
            TemplateVariance = samples.Length * squaredSum - TemplateSum * TemplateSum;
        }

        public int Width { get; }
        public int Height { get; }
        public MatchSample[] Samples { get; }
        public double TemplateSum { get; }
        public double TemplateVariance { get; }

        public static MatchKernel Create(ScaledTemplate template, MaskKind maskKind, bool coarse)
        {
            var stride = coarse
                ? Math.Max(1, Math.Min(template.Width, template.Height) / 8)
                : Math.Max(1, Math.Min(template.Width, template.Height) / 14);
            var samples = new List<MatchSample>();
            var stableShapeRight = template.Width * 43.0 / 70.0;
            for (var y = 0; y < template.Height; y += stride)
            {
                for (var x = 0; x < template.Width; x += stride)
                {
                    var index = y * template.Width + x;
                    if (template.Alpha[index] < 128 ||
                        maskKind == MaskKind.TuairimStableShape && x >= stableShapeRight)
                    {
                        continue;
                    }

                    samples.Add(new MatchSample(x, y, template.Gray[index]));
                }
            }

            return new MatchKernel(template.Width, template.Height, samples.ToArray());
        }
    }

    private sealed class TemplateImage
    {
        private readonly PixelImage _source;

        public TemplateImage(PixelImage source) => _source = source;

        public int Width => _source.Width;
        public int Height => _source.Height;

        public ScaledTemplate Scale(int width, int height)
        {
            var red = new byte[width * height];
            var green = new byte[width * height];
            var blue = new byte[width * height];
            var gray = new byte[width * height];
            var alpha = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                var sourceY = Math.Clamp((int)Math.Round((y + 0.5) * Height / height - 0.5), 0, Height - 1);
                for (var x = 0; x < width; x++)
                {
                    var sourceX = Math.Clamp((int)Math.Round((x + 0.5) * Width / width - 0.5), 0, Width - 1);
                    var sourceIndex = sourceY * Width + sourceX;
                    var targetIndex = y * width + x;
                    red[targetIndex] = _source.Red[sourceIndex];
                    green[targetIndex] = _source.Green[sourceIndex];
                    blue[targetIndex] = _source.Blue[sourceIndex];
                    gray[targetIndex] = _source.Gray[sourceIndex];
                    alpha[targetIndex] = _source.Alpha[sourceIndex];
                }
            }

            return new ScaledTemplate(width, height, red, green, blue, gray, alpha);
        }
    }

    private sealed record ScaledTemplate(
        int Width,
        int Height,
        byte[] Red,
        byte[] Green,
        byte[] Blue,
        byte[] Gray,
        byte[] Alpha);

    private sealed class PixelImage
    {
        private PixelImage(int width, int height, byte[] red, byte[] green, byte[] blue, byte[] gray, byte[] alpha)
        {
            Width = width;
            Height = height;
            Red = red;
            Green = green;
            Blue = blue;
            Gray = gray;
            Alpha = alpha;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Red { get; }
        public byte[] Green { get; }
        public byte[] Blue { get; }
        public byte[] Gray { get; }
        public byte[] Alpha { get; }

        public static PixelImage FromBitmapSource(BitmapSource source)
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var length = converted.PixelWidth * converted.PixelHeight;
            var red = new byte[length];
            var green = new byte[length];
            var blue = new byte[length];
            var gray = new byte[length];
            var alpha = new byte[length];
            for (var index = 0; index < length; index++)
            {
                var sourceIndex = index * 4;
                blue[index] = pixels[sourceIndex];
                green[index] = pixels[sourceIndex + 1];
                red[index] = pixels[sourceIndex + 2];
                alpha[index] = pixels[sourceIndex + 3];
                gray[index] = (byte)Math.Clamp(
                    (red[index] * 299 + green[index] * 587 + blue[index] * 114 + 500) / 1000,
                    0,
                    255);
            }

            return new PixelImage(converted.PixelWidth, converted.PixelHeight, red, green, blue, gray, alpha);
        }
    }
}
