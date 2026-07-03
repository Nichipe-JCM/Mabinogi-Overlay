using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

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
    Console.Error.WriteLine("  TestOverlay.DetectionProbe tuairim <image-path> <x> <y> <width> <height>");
    Console.Error.WriteLine("  TestOverlay.DetectionProbe tuairim-value <image-path> <x> <y> <width> <height>");
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
