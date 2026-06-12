using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class BenchmarkWindow : Window
{
    private const int SourceWidth = 1920;
    private const int SourceHeight = 1080;
    private const int OverlayWidth = 960;
    private const int OverlayHeight = 540;
    private const int Iterations = 180;

    private readonly AppLog _log = new();
    private readonly WindowCaptureService _captureService = new();
    private readonly CpuCompositedOverlayRenderer _cpuCompositedRenderer = new();
    private bool _isRunning;

    public BenchmarkWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RunBenchmarkAsync();
    }

    private async void RunAgainButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBenchmarkAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RunBenchmarkAsync()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        RunAgainButton.IsEnabled = false;
        OutputBox.Clear();
        BenchmarkProgress.Value = 0;

        try
        {
            Append("Benchmark started. Results are also written to app.log.");
            _log.Info("Benchmark started: syntheticSource=1920x1080, iterations=180, multipliers=1/5/10/20");

            var source = CreateSyntheticFrame(SourceWidth, SourceHeight);
            var baseSlots = CreateBaseSlots(source);
            var cases = new List<BenchmarkCase>();
            foreach (var multiplier in new[] { 1, 5, 10, 20 })
            {
                cases.Add(new BenchmarkCase(OverlayRenderMode.CpuWpf, multiplier));
                cases.Add(new BenchmarkCase(OverlayRenderMode.CpuComposited, multiplier));
            }

            Append("GPU/DXGI benchmark note: GPU mode depends on WGC + DirectComposition runtime. Use live overlay GPU renderer stats in app.log for that mode.");
            _log.Info("Benchmark GPU/DXGI skipped: synthetic benchmark does not create a WGC GraphicsCaptureItem; use live GPU renderer stats for GPU/DXGI load.");

            for (var index = 0; index < cases.Count; index++)
            {
                var benchmarkCase = cases[index];
                StatusText.Text = $"Running {RenderModeLabel(benchmarkCase.Mode)} {benchmarkCase.Multiplier}x...";
                BenchmarkProgress.Value = index / (double)cases.Count;
                await Task.Yield();

                var slots = ExpandSlots(baseSlots, benchmarkCase.Multiplier);
                var result = benchmarkCase.Mode == OverlayRenderMode.CpuComposited
                    ? RunCpuComposited(source, slots)
                    : RunExistingCpuWpf(source, slots);

                var line =
                    $"mode={RenderModeLabel(benchmarkCase.Mode)}, multiplier={benchmarkCase.Multiplier}x, " +
                    $"effectiveSlots={slots.Count}, frames={result.Frames}, avgMs={result.AverageMs:0.00}, " +
                    $"p95Ms={result.P95Ms:0.00}, maxMs={result.MaxMs:0.00}, " +
                    $"gen0={result.Gen0Collections}, gen1={result.Gen1Collections}, gen2={result.Gen2Collections}, " +
                    $"memoryDeltaMB={result.MemoryDeltaMb:0.00}";
                Append(line);
                _log.Info($"Benchmark result: {line}");
            }

            BenchmarkProgress.Value = 1;
            StatusText.Text = $"Benchmark complete. Log: {_log.LogPath}";
            Append($"Benchmark complete. Log: {_log.LogPath}");
            _log.Info("Benchmark completed.");
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Benchmark failed: {exception.Message}";
            Append(exception.ToString());
            _log.Error("Benchmark failed.", exception);
        }
        finally
        {
            RunAgainButton.IsEnabled = true;
            _isRunning = false;
        }
    }

    private BenchmarkResult RunExistingCpuWpf(BitmapSource source, IReadOnlyList<OverlaySlot> slots) =>
        Measure(() =>
        {
            foreach (var slot in slots)
            {
                slot.Preview = _captureService.Crop(source, slot.Source.SourceRect);
            }

            var imageCount = 0;
            foreach (var slot in slots)
            {
                if (slot.Preview is not null)
                {
                    var image = new Image
                    {
                        Width = slot.OverlayRect.Width,
                        Height = slot.OverlayRect.Height,
                        Source = slot.Preview,
                        Stretch = Stretch.Fill,
                        Opacity = slot.Opacity,
                        Focusable = false,
                        IsHitTestVisible = false
                    };
                    GC.KeepAlive(image);
                    imageCount++;
                }
            }

            GC.KeepAlive(imageCount);
        });

    private BenchmarkResult RunCpuComposited(BitmapSource source, IReadOnlyList<OverlaySlot> slots) =>
        Measure(() =>
        {
            var frame = _cpuCompositedRenderer.Render(source, slots, OverlayWidth, OverlayHeight);
            GC.KeepAlive(frame);
        });

    private BenchmarkResult Measure(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var startGen0 = GC.CollectionCount(0);
        var startGen1 = GC.CollectionCount(1);
        var startGen2 = GC.CollectionCount(2);
        var startMemory = GC.GetTotalMemory(false);
        var samples = new double[Iterations];
        var stopwatch = new Stopwatch();

        for (var i = 0; i < Iterations; i++)
        {
            stopwatch.Restart();
            action();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var endMemory = GC.GetTotalMemory(false);
        Array.Sort(samples);
        return new BenchmarkResult(
            Iterations,
            samples.Average(),
            samples[(int)Math.Clamp(Math.Ceiling(samples.Length * 0.95) - 1, 0, samples.Length - 1)],
            samples[^1],
            GC.CollectionCount(0) - startGen0,
            GC.CollectionCount(1) - startGen1,
            GC.CollectionCount(2) - startGen2,
            (endMemory - startMemory) / 1024.0 / 1024.0);
    }

    private static BitmapSource CreateSyntheticFrame(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var offset = row + x * 4;
                pixels[offset] = (byte)((x * 3 + y * 5) & 0xFF);
                pixels[offset + 1] = (byte)((x * 7 + y * 2) & 0xFF);
                pixels[offset + 2] = (byte)((x * 11 + y * 13) & 0xFF);
                pixels[offset + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static List<OverlaySlot> CreateBaseSlots(BitmapSource source)
    {
        var slots = new List<OverlaySlot>();
        var id = 1;
        for (var group = 0; group < 3; group++)
        {
            var groupX = 8 + group * 147;
            for (var row = 0; row < 2; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    var sourceRect = new Rect(groupX + column * 32, 18 + row * 37, 29, 29);
                    var destinationRect = new Rect((id - 1) % 12 * 34, (id - 1) / 12 * 34, 29, 29);
                    var candidate = new SlotCandidate(id, sourceRect, 100);
                    slots.Add(new OverlaySlot(candidate, destinationRect, source, 1, 1));
                    id++;
                }
            }
        }

        return slots;
    }

    private static List<OverlaySlot> ExpandSlots(IReadOnlyList<OverlaySlot> baseSlots, int multiplier)
    {
        var slots = new List<OverlaySlot>(baseSlots.Count * multiplier);
        var id = 1;
        for (var repeat = 0; repeat < multiplier; repeat++)
        {
            foreach (var baseSlot in baseSlots)
            {
                var cell = id - 1;
                var destinationRect = new Rect(
                    cell % 24 * 34,
                    cell / 24 * 34,
                    baseSlot.OverlayRect.Width,
                    baseSlot.OverlayRect.Height);
                if (destinationRect.Y + destinationRect.Height > OverlayHeight)
                {
                    destinationRect = new Rect(destinationRect.X, destinationRect.Y % Math.Max(1, OverlayHeight - 34), destinationRect.Width, destinationRect.Height);
                }

                var candidate = new SlotCandidate(id, baseSlot.Source.SourceRect, baseSlot.Source.Score);
                slots.Add(new OverlaySlot(candidate, destinationRect, baseSlot.Preview, baseSlot.Opacity, baseSlot.Scale));
                id++;
            }
        }

        return slots;
    }

    private void Append(string message)
    {
        OutputBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        OutputBox.ScrollToEnd();
    }

    private static string RenderModeLabel(OverlayRenderMode mode) =>
        mode switch
        {
            OverlayRenderMode.CpuComposited => "CPU/Composited",
            OverlayRenderMode.GpuDxgi => "GPU/DXGI",
            _ => "CPU/WPF"
        };

    private sealed record BenchmarkCase(OverlayRenderMode Mode, int Multiplier);

    private sealed record BenchmarkResult(
        int Frames,
        double AverageMs,
        double P95Ms,
        double MaxMs,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        double MemoryDeltaMb);
}
