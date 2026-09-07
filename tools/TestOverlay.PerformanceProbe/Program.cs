using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var reports = new List<object>();
        foreach (var sourceWidth in new[] { 1920, 3840 })
        foreach (var slotCount in new[] { 16, 64 })
        {
            var sourceHeight = sourceWidth * 9 / 16;
            var pixels = new byte[sourceWidth * sourceHeight * 4];
            new Random(12345).NextBytes(pixels);
            var source = BitmapSource.Create(sourceWidth, sourceHeight, 96, 96, PixelFormats.Bgra32, null, pixels, sourceWidth * 4);
            source.Freeze();
            var slots = Enumerable.Range(0, slotCount).Select(i => new OverlaySlot(
                new SlotCandidate(i + 1, new Rect(i % 16 * 40, i / 16 * 40, 32, 32), 1),
                new Rect(i % 16 * 40, i / 16 * 40, 32, 32), source)).ToArray();
            var renderer = new CpuCompositedOverlayRenderer();
            for (var i = 0; i < 30; i++) renderer.Render(source, slots, 720, 320, reuseOutput: true);
            const int iterations = 200;
            var times = new double[iterations];
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                var start = Stopwatch.GetTimestamp();
                renderer.Render(source, slots, 720, 320, reuseOutput: true);
                times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Array.Sort(times);
            reports.Add(new { sourceWidth, sourceHeight, slotCount, iterations,
                meanMs = times.Average(), medianMs = times[iterations / 2], p95Ms = times[(int)(iterations * .95) - 1],
                managedBytesPerFrame = allocated / iterations });
        }
        var json = JsonSerializer.Serialize(new { mode = "synthetic-cpu-compositor; excludes capture, OCR, display and native allocations",
            utc = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(), processorCount = Environment.ProcessorCount,
            results = reports }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (args.Length > 0) File.WriteAllText(Path.GetFullPath(args[0]), json);
    }
}
