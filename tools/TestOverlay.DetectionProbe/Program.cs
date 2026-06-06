using System.IO;
using System.Windows.Media.Imaging;
using TestOverlay.App.Services;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: TestOverlay.DetectionProbe <image-path> [min-size] [max-size]");
    return 2;
}

var imagePath = Path.GetFullPath(args[0]);
var minSize = args.Length > 1 && int.TryParse(args[1], out var parsedMin) ? parsedMin : 16;
var maxSize = args.Length > 2 && int.TryParse(args[2], out var parsedMax) ? parsedMax : 96;

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

var detector = new SlotDetectionService();
var candidates = detector.Detect(image, minSize, maxSize);

Console.WriteLine($"image={image.PixelWidth}x{image.PixelHeight}");
Console.WriteLine($"range={minSize}-{maxSize}");
Console.WriteLine($"count={candidates.Count}");
Console.WriteLine("id,x,y,width,height,score");
foreach (var candidate in candidates)
{
    Console.WriteLine($"{candidate.Id},{candidate.SourceRect.X:0},{candidate.SourceRect.Y:0},{candidate.SourceRect.Width:0},{candidate.SourceRect.Height:0},{candidate.Score:0.00}");
}

return 0;
