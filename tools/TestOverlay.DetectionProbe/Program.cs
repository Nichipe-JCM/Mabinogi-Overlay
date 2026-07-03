using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

if (args.Length >= 6 &&
    (args[0].Equals("buff-benchmark", StringComparison.OrdinalIgnoreCase) ||
     args[0].Equals("tuairim-benchmark", StringComparison.OrdinalIgnoreCase)))
{
    const int runCount = 30;
    var image = LoadImage(args[1]);
    var roi = ParseRoi(args, 2);
    var detector = new MonitorTemplateDetectionService();
    var recognizer = new MonitorValueRecognitionService();
    Func<Task<int?>> readValue;
    if (args[0].Equals("buff-benchmark", StringComparison.OrdinalIgnoreCase))
    {
        var detection = detector.DetectBuffs(image, roi);
        var activeMatch = detection.Matches.FirstOrDefault(match => match.IsActive)
                          ?? throw new InvalidOperationException("No active supported buff was detected in the ROI.");
        readValue = async () =>
            (await recognizer.ReadBuffTimeAsync(image, detection.Roi, activeMatch.Bounds)).RemainingSeconds;
        Console.WriteLine($"target=buff,key={activeMatch.NameKey},bounds={FormatRect(activeMatch.Bounds)}");
    }
    else
    {
        var detection = detector.DetectTuairim(image, roi)
                        ?? throw new InvalidOperationException("Tuairim UI was not detected in the ROI.");
        readValue = async () =>
            (await recognizer.ReadTuairimPercentAsync(image, detection.Bounds)).Percent;
        Console.WriteLine($"target=tuairim,bounds={FormatRect(detection.Bounds)}");
    }

    var coldClock = Stopwatch.StartNew();
    var coldValue = await readValue();
    coldClock.Stop();
    Console.WriteLine($"coldMs={coldClock.Elapsed.TotalMilliseconds:0.000},value={coldValue?.ToString() ?? "none"}");

    var samples = new List<double>(runCount);
    var values = new List<int?>(runCount);
    for (var index = 0; index < runCount; index++)
    {
        var clock = Stopwatch.StartNew();
        values.Add(await readValue());
        clock.Stop();
        samples.Add(clock.Elapsed.TotalMilliseconds);
        Console.WriteLine($"run={index + 1:00},ms={samples[^1]:0.000},value={values[^1]?.ToString() ?? "none"}");
    }

    var ordered = samples.Order().ToArray();
    Console.WriteLine(
        $"summary=runs:{runCount},success:{values.Count(value => value is not null)}," +
        $"minMs:{ordered[0]:0.000},p50Ms:{Percentile(ordered, 0.50):0.000}," +
        $"avgMs:{samples.Average():0.000},p95Ms:{Percentile(ordered, 0.95):0.000},maxMs:{ordered[^1]:0.000}");
    return values.All(value => value is not null) ? 0 : 1;
}

if (args.Length >= 6 &&
    (args[0].Equals("buff", StringComparison.OrdinalIgnoreCase) ||
     args[0].Equals("buff-value", StringComparison.OrdinalIgnoreCase)))
{
    var image = LoadImage(args[1]);
    var roi = ParseRoi(args, 2);
    var result = new MonitorTemplateDetectionService().DetectBuffs(image, roi);
    Console.WriteLine($"image={image.PixelWidth}x{image.PixelHeight}");
    Console.WriteLine($"roi={FormatRect(result.Roi)}");
    Console.WriteLine($"count={result.Matches.Count}");
    foreach (var match in result.Matches)
    {
        Console.WriteLine(
            $"{match.NameKey},{FormatRect(match.Bounds)},score={match.StructureScore:0.0000}," +
            $"active={match.IsActive},stateConfidence={match.StateConfidence:0.0000}");
        if (args[0].Equals("buff-value", StringComparison.OrdinalIgnoreCase) && match.IsActive)
        {
            var value = await new MonitorValueRecognitionService().ReadBuffTimeAsync(image, result.Roi, match.Bounds);
            Console.WriteLine(
                $"  seconds={value.RemainingSeconds?.ToString() ?? "none"},text={value.RecognizedText}," +
                $"valueBounds={FormatRect(value.Bounds)}");
        }
    }
    return result.Matches.Count > 0 ? 0 : 1;
}

if (args.Length >= 6 &&
    (args[0].Equals("tuairim", StringComparison.OrdinalIgnoreCase) ||
     args[0].Equals("tuairim-value", StringComparison.OrdinalIgnoreCase)))
{
    var image = LoadImage(args[1]);
    var roi = ParseRoi(args, 2);
    var result = new MonitorTemplateDetectionService().DetectTuairim(image, roi);
    Console.WriteLine($"image={image.PixelWidth}x{image.PixelHeight}");
    Console.WriteLine($"roi={FormatRect(roi)}");
    if (result is null)
    {
        Console.WriteLine("count=0");
        return 1;
    }

    Console.WriteLine("count=1");
    Console.WriteLine($"bounds={FormatRect(result.Bounds)}");
    Console.WriteLine($"score={result.Score:0.0000}");
    if (args[0].Equals("tuairim-value", StringComparison.OrdinalIgnoreCase))
    {
        var value = await new MonitorValueRecognitionService().ReadTuairimPercentAsync(image, result.Bounds);
        Console.WriteLine($"percent={value.Percent?.ToString() ?? "none"}");
        Console.WriteLine($"text={value.RecognizedText}");
        Console.WriteLine($"valueBounds={FormatRect(value.Bounds)}");
        if (value.Percent is null)
        {
            var diagnosticDirectory = Path.Combine(AppContext.BaseDirectory, "probe-diagnostics");
            MonitorValueRecognitionService.SaveDiagnosticImages(image, value.Bounds, diagnosticDirectory, "tuairim-value");
            Console.WriteLine($"diagnostics={diagnosticDirectory}");
        }
    }
    return 0;
}

if (args.Length < 6)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  TestOverlay.DetectionProbe <image-path> <top|vertical> <x> <y> <width> <height>");
    Console.Error.WriteLine("  TestOverlay.DetectionProbe buff <image-path> <x> <y> <width> <height>");
    Console.Error.WriteLine("  TestOverlay.DetectionProbe buff-value <image-path> <x> <y> <width> <height>");
    Console.Error.WriteLine("  TestOverlay.DetectionProbe buff-benchmark <image-path> <x> <y> <width> <height>");
    Console.Error.WriteLine("  TestOverlay.DetectionProbe tuairim <image-path> <x> <y> <width> <height>");
    Console.Error.WriteLine("  TestOverlay.DetectionProbe tuairim-value <image-path> <x> <y> <width> <height>");
    Console.Error.WriteLine("  TestOverlay.DetectionProbe tuairim-benchmark <image-path> <x> <y> <width> <height>");
    return 2;
}

var quickslotImage = LoadImage(args[0]);
var patternKind = args[1].Equals("vertical", StringComparison.OrdinalIgnoreCase)
    ? QuickslotSectionPatternKind.Vertical
    : QuickslotSectionPatternKind.TopGrouped;
var quickslotRoi = ParseRoi(args, 2);
var quickslotResult = new RoiSectionDetectionService().Detect(quickslotImage, quickslotRoi, patternKind);

Console.WriteLine($"image={quickslotImage.PixelWidth}x{quickslotImage.PixelHeight}");
Console.WriteLine($"roi={FormatRect(quickslotRoi)}");
Console.WriteLine($"pattern={patternKind}");
if (quickslotResult is null)
{
    Console.WriteLine("count=0");
    return 1;
}

Console.WriteLine($"count={quickslotResult.Slots.Count}");
Console.WriteLine($"gapX={quickslotResult.SmallGapX:0}");
Console.WriteLine($"gapY={quickslotResult.SmallGapY:0}");
Console.WriteLine($"largeGap={quickslotResult.LargeGap:0}");
Console.WriteLine($"score={quickslotResult.Score:0.00}");
Console.WriteLine($"detectedSlotSize={quickslotResult.Slots[0].Width:0}x{quickslotResult.Slots[0].Height:0}");
Console.WriteLine("id,x,y,width,height,score");
for (var index = 0; index < quickslotResult.Slots.Count; index++)
{
    var slot = quickslotResult.Slots[index];
    Console.WriteLine($"{index + 1},{slot.X:0},{slot.Y:0},{slot.Width:0},{slot.Height:0},{quickslotResult.Score:0.00}");
}

return 0;

static BitmapSource LoadImage(string path)
{
    var imagePath = Path.GetFullPath(path);
    if (!File.Exists(imagePath))
    {
        throw new FileNotFoundException("Image not found.", imagePath);
    }

    var image = new BitmapImage();
    image.BeginInit();
    image.CacheOption = BitmapCacheOption.OnLoad;
    image.UriSource = new Uri(imagePath);
    image.EndInit();
    image.Freeze();
    return image;
}

static Rect ParseRoi(string[] values, int offset) => new(
    double.Parse(values[offset]),
    double.Parse(values[offset + 1]),
    double.Parse(values[offset + 2]),
    double.Parse(values[offset + 3]));

static string FormatRect(Rect rect) => $"{rect.X:0},{rect.Y:0},{rect.Width:0}x{rect.Height:0}";

static double Percentile(IReadOnlyList<double> ordered, double percentile)
{
    var position = Math.Clamp((ordered.Count - 1) * percentile, 0, ordered.Count - 1);
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    if (lower == upper)
    {
        return ordered[lower];
    }

    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
}
