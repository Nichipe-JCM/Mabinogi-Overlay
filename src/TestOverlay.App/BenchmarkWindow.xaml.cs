using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using VorticeColor4 = Vortice.Mathematics.Color4;
using VorticeSizeI = Vortice.Mathematics.SizeI;

namespace TestOverlay.App;

public partial class BenchmarkWindow : Window
{
    private readonly AppLog _log = new();
    private readonly WindowCaptureService _captureService = new();
    private readonly CpuCompositedOverlayRenderer _cpuCompositedRenderer = new();
    private bool _isRunning;
    private int _overlayWidth = 960;
    private int _overlayHeight = 540;
    private int _iterations = 180;

    public BenchmarkWindow()
    {
        InitializeComponent();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
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
        StartButton.IsEnabled = false;
        OutputBox.Clear();
        BenchmarkProgress.Value = 0;

        try
        {
            var settings = ReadSettings();
            _iterations = settings.Iterations;
            _overlayWidth = settings.OverlayWidth;
            _overlayHeight = settings.OverlayHeight;

            Append("Benchmark settings loaded. Pressing Start runs selected cases and writes results to app.log.");
            _log.Info(
                $"Benchmark started: syntheticSource={settings.SourceWidth}x{settings.SourceHeight}, " +
                $"overlay={settings.OverlayWidth}x{settings.OverlayHeight}, iterations={settings.Iterations}, " +
                $"multipliers={string.Join('/', settings.Multipliers)}, modes={string.Join('/', settings.Modes.Select(RenderModeLabel))}");

            var source = CreateSyntheticFrame(settings.SourceWidth, settings.SourceHeight);
            var baseSlots = CreateBaseSlots(source);
            var cases = new List<BenchmarkCase>();
            foreach (var multiplier in settings.Multipliers)
            {
                foreach (var mode in settings.Modes)
                {
                    cases.Add(new BenchmarkCase(mode, multiplier));
                }
            }

            if (cases.Count == 0)
            {
                StatusText.Text = "No benchmark cases selected.";
                Append("No benchmark cases selected.");
                return;
            }

            for (var index = 0; index < cases.Count; index++)
            {
                var benchmarkCase = cases[index];
                StatusText.Text = $"Running {RenderModeLabel(benchmarkCase.Mode)} {benchmarkCase.Multiplier}x...";
                BenchmarkProgress.Value = index / (double)cases.Count;
                await Task.Yield();

                var slots = ExpandSlots(baseSlots, benchmarkCase.Multiplier);
                var result = benchmarkCase.Mode switch
                {
                    OverlayRenderMode.CpuComposited => RunCpuComposited(source, slots),
                    OverlayRenderMode.GpuDxgi => RunGpuDxgiOffscreen(source, slots),
                    _ => RunExistingCpuWpf(source, slots)
                };

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
            StartButton.IsEnabled = true;
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
            var frame = _cpuCompositedRenderer.Render(source, slots, _overlayWidth, _overlayHeight);
            GC.KeepAlive(frame);
        });

    private BenchmarkResult RunGpuDxgiOffscreen(BitmapSource source, IReadOnlyList<OverlaySlot> slots)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        using var gpu = new GpuOffscreenBenchmark(source.PixelWidth, source.PixelHeight, _overlayWidth, _overlayHeight, pixels);
        return Measure(() => gpu.Render(slots));
    }

    private BenchmarkResult Measure(Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var startGen0 = GC.CollectionCount(0);
        var startGen1 = GC.CollectionCount(1);
        var startGen2 = GC.CollectionCount(2);
        var startMemory = GC.GetTotalMemory(false);
        var samples = new double[_iterations];
        var stopwatch = new Stopwatch();

        for (var i = 0; i < _iterations; i++)
        {
            stopwatch.Restart();
            action();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var endMemory = GC.GetTotalMemory(false);
        Array.Sort(samples);
        return new BenchmarkResult(
            _iterations,
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
                if (destinationRect.Y + destinationRect.Height > 540)
                {
                    destinationRect = new Rect(destinationRect.X, destinationRect.Y % Math.Max(1, 540 - 34), destinationRect.Width, destinationRect.Height);
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

    private BenchmarkSettings ReadSettings()
    {
        var (sourceWidth, sourceHeight) = ReadSizeCombo(SourceSizeCombo, 1920, 1080);
        var (overlayWidth, overlayHeight) = ReadSizeCombo(OverlaySizeCombo, 960, 540);
        var iterations = ReadComboInt(IterationsCombo, 180);
        var multipliers = MultipliersBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? Math.Clamp(parsed, 1, 100) : 0)
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        if (multipliers.Count == 0)
        {
            multipliers.Add(1);
        }

        var modes = new List<OverlayRenderMode>();
        if (CpuWpfCheck.IsChecked == true)
        {
            modes.Add(OverlayRenderMode.CpuWpf);
        }

        if (CpuCompositedCheck.IsChecked == true)
        {
            modes.Add(OverlayRenderMode.CpuComposited);
        }

        if (GpuDxgiCheck.IsChecked == true)
        {
            modes.Add(OverlayRenderMode.GpuDxgi);
        }

        return new BenchmarkSettings(sourceWidth, sourceHeight, overlayWidth, overlayHeight, iterations, multipliers, modes);
    }

    private static (int Width, int Height) ReadSizeCombo(ComboBox comboBox, int fallbackWidth, int fallbackHeight)
    {
        var text = (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        var parts = text.Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var width) &&
               int.TryParse(parts[1], out var height)
            ? (width, height)
            : (fallbackWidth, fallbackHeight);
    }

    private static int ReadComboInt(ComboBox comboBox, int fallback)
    {
        var text = (comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        return int.TryParse(text, out var value) ? Math.Clamp(value, 1, 10000) : fallback;
    }

    private sealed record BenchmarkResult(
        int Frames,
        double AverageMs,
        double P95Ms,
        double MaxMs,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        double MemoryDeltaMb);

    private sealed record BenchmarkSettings(
        int SourceWidth,
        int SourceHeight,
        int OverlayWidth,
        int OverlayHeight,
        int Iterations,
        IReadOnlyList<int> Multipliers,
        IReadOnlyList<OverlayRenderMode> Modes);

    private sealed class GpuOffscreenBenchmark : IDisposable
    {
        private readonly ID3D11Device _d3dDevice;
        private readonly ID3D11DeviceContext _d3dContext;
        private readonly IDXGIDevice _dxgiDevice;
        private readonly ID2D1Device _d2dDevice;
        private readonly ID2D1DeviceContext _d2dContext;
        private readonly ID2D1Bitmap1 _sourceBitmap;
        private readonly ID2D1Bitmap1 _targetBitmap;

        public GpuOffscreenBenchmark(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, byte[] sourcePixels)
        {
            _d3dDevice = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                D3DFeatureLevel.Level_11_1,
                D3DFeatureLevel.Level_11_0,
                D3DFeatureLevel.Level_10_1,
                D3DFeatureLevel.Level_10_0);
            _d3dContext = _d3dDevice.ImmediateContext;
            _dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            _d2dDevice = D2D1.D2D1CreateDevice(_dxgiDevice);
            _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

            var bitmapProperties = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
                96,
                96,
                BitmapOptions.None);
            var handle = GCHandle.Alloc(sourcePixels, GCHandleType.Pinned);
            try
            {
                _sourceBitmap = _d2dContext.CreateBitmap(
                    new VorticeSizeI(sourceWidth, sourceHeight),
                    handle.AddrOfPinnedObject(),
                    (uint)(sourceWidth * 4),
                    bitmapProperties);
            }
            finally
            {
                handle.Free();
            }

            var targetProperties = new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96,
                96,
                BitmapOptions.Target);
            _targetBitmap = _d2dContext.CreateBitmap(
                new VorticeSizeI(targetWidth, targetHeight),
                nint.Zero,
                0,
                targetProperties);
            _d2dContext.Target = _targetBitmap;
        }

        public void Render(IReadOnlyList<OverlaySlot> slots)
        {
            _d2dContext.BeginDraw();
            _d2dContext.Clear(new VorticeColor4(0, 0, 0, 0));
            foreach (var slot in slots)
            {
                _d2dContext.DrawBitmap(
                    _sourceBitmap,
                    ToRawRect(slot.OverlayRect),
                    (float)Math.Clamp(slot.Opacity, 0.05, 1),
                    Vortice.Direct2D1.InterpolationMode.NearestNeighbor,
                    ToRawRect(slot.Source.SourceRect),
                    null);
            }

            _d2dContext.EndDraw();
            _d3dContext.Flush();
        }

        public void Dispose()
        {
            _targetBitmap.Dispose();
            _sourceBitmap.Dispose();
            _d2dContext.Dispose();
            _d2dDevice.Dispose();
            _dxgiDevice.Dispose();
            _d3dContext.Dispose();
            _d3dDevice.Dispose();
        }

        private static RawRectF ToRawRect(Rect rect) =>
            new(
                (float)rect.X,
                (float)rect.Y,
                (float)(rect.X + rect.Width),
                (float)(rect.Y + rect.Height));
    }
}
