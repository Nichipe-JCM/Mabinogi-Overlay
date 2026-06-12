using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

if (args.Length < 6)
{
    Console.Error.WriteLine("Usage: TestOverlay.DetectionProbe <image-path> <top|vertical> <x> <y> <width> <height>");
    return 2;
}

var imagePath = Path.GetFullPath(args[0]);
var patternKind = args[1].Equals("vertical", StringComparison.OrdinalIgnoreCase)
    ? QuickslotSectionPatternKind.Vertical
    : QuickslotSectionPatternKind.TopGrouped;
var x = double.Parse(args[2]);
var y = double.Parse(args[3]);
var width = double.Parse(args[4]);
var height = double.Parse(args[5]);

if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"Image not found: {imagePath}");
    return 3;
}

var image = new BitmapImage();
image.BeginInit();
image.CacheOption = BitmapCacheOption.OnLoad;
image.UriSource = new Uri(imagePath);
image.EndInit();
image.Freeze();

var detector = new RoiSectionDetectionService();
var result = detector.Detect(image, new Rect(x, y, width, height), patternKind);

Console.WriteLine($"image={image.PixelWidth}x{image.PixelHeight}");
Console.WriteLine($"roi={x:0},{y:0},{width:0}x{height:0}");
Console.WriteLine($"pattern={patternKind}");
if (result is null)
{
    Console.WriteLine("count=0");
    return 1;
}

Console.WriteLine($"count={result.Slots.Count}");
Console.WriteLine($"gapX={result.SmallGapX:0}");
Console.WriteLine($"gapY={result.SmallGapY:0}");
Console.WriteLine($"largeGap={result.LargeGap:0}");
Console.WriteLine($"score={result.Score:0.00}");
Console.WriteLine($"detectedSlotSize={result.Slots[0].Width:0}x{result.Slots[0].Height:0}");
Console.WriteLine("id,x,y,width,height,score");
for (var i = 0; i < result.Slots.Count; i++)
{
    var slot = result.Slots[i];
    Console.WriteLine($"{i + 1},{slot.X:0},{slot.Y:0},{slot.Width:0},{slot.Height:0},{result.Score:0.00}");
}

return 0;
