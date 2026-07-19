using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Native;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Security.Authorization.AppCapabilityAccess;
using Windows.Storage.Streams;

namespace TestOverlay.App.Services;

public sealed class WgcCaptureService
{
    private readonly object _sync = new();
    private readonly object _borderlessAccessSync = new();
    private readonly AppLog _log;
    private Task<WgcBorderlessAccessState>? _borderlessAccessTask;
    private WgcBorderlessAccessState _borderlessAccessState = WgcBorderlessAccessState.Unknown;
    private IDirect3DDevice? _liveDevice;
    private Direct3D11CaptureFramePool? _liveFramePool;
    private GraphicsCaptureSession? _liveSession;
    private TypedEventHandler<Direct3D11CaptureFramePool, object>? _liveFrameArrivedHandler;
    private BitmapSource? _latestFrame;
    private int _processingLiveGeneration;
    private int _liveGeneration;
    private long _minimumLiveFrameIntervalTicks;
    private long _lastConvertedLiveFrameTicks;

    public WgcCaptureService(AppLog log)
    {
        _log = log;
    }

    public Exception? LastLiveCaptureException { get; private set; }

    public bool IsBorderlessCaptureAllowed =>
        _borderlessAccessState == WgcBorderlessAccessState.Allowed;

    public async Task<BitmapSource> CaptureOnceAsync(GraphicsCaptureItem item, TimeSpan timeout)
    {
        await EnsureBorderlessAccessAsync();

        using var cancellation = new CancellationTokenSource(timeout);
        var device = Direct3D11Interop.CreateDevice();
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            item.Size);
        using var session = framePool.CreateCaptureSession(item);
        var bitmapTask = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameClaimed = 0;

        using var registration = cancellation.Token.Register(() => bitmapTask.TrySetCanceled(cancellation.Token));
        TypedEventHandler<Direct3D11CaptureFramePool, object> frameArrivedHandler = (sender, _) =>
        {
            var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            if (Interlocked.Exchange(ref frameClaimed, 1) != 0)
            {
                frame.Dispose();
                return;
            }

            _ = CompleteSingleFrameAsync(frame, bitmapTask, cancellation.Token);
        };
        framePool.FrameArrived += frameArrivedHandler;

        try
        {
            session.IsCursorCaptureEnabled = false;
            TryDisableCaptureBorder(session);
            session.StartCapture();
            return await bitmapTask.Task.ConfigureAwait(false);
        }
        finally
        {
            framePool.FrameArrived -= frameArrivedHandler;
        }
    }

    private static async Task CompleteSingleFrameAsync(
        Direct3D11CaptureFrame frame,
        TaskCompletionSource<BitmapSource> completion,
        CancellationToken cancellationToken)
    {
        using (frame)
        {
            try
            {
                var copyOperation = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                using var softwareBitmap = await copyOperation.AsTask(cancellationToken).ConfigureAwait(false);
                completion.TrySetResult(ToBitmapSource(softwareBitmap));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }

    public void StartLiveCapture(GraphicsCaptureItem item, int maxFps = 0)
    {
        StopLiveCapture();
        var generation = Interlocked.Increment(ref _liveGeneration);
        _minimumLiveFrameIntervalTicks = maxFps > 0
            ? Math.Max(1, Stopwatch.Frequency / maxFps)
            : 0;
        _lastConvertedLiveFrameTicks = 0;

        _liveDevice = Direct3D11Interop.CreateDevice();
        _liveFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _liveDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        _liveSession = _liveFramePool.CreateCaptureSession(item);
        _liveSession.IsCursorCaptureEnabled = false;
        TryDisableCaptureBorder(_liveSession);
        _liveFrameArrivedHandler = (sender, args) => LiveFramePool_FrameArrived(sender, args, generation);
        _liveFramePool.FrameArrived += _liveFrameArrivedHandler;
        _liveSession.StartCapture();
    }

    public Task<WgcBorderlessAccessState> EnsureBorderlessAccessAsync()
    {
        lock (_borderlessAccessSync)
        {
            return _borderlessAccessTask ??= RequestBorderlessAccessAsync();
        }
    }

    public void StopLiveCapture()
    {
        Interlocked.Increment(ref _liveGeneration);
        if (_liveFramePool is not null && _liveFrameArrivedHandler is not null)
        {
            _liveFramePool.FrameArrived -= _liveFrameArrivedHandler;
        }

        _liveSession?.Dispose();
        _liveFramePool?.Dispose();
        _liveDevice?.Dispose();
        _liveSession = null;
        _liveFramePool = null;
        _liveDevice = null;
        _liveFrameArrivedHandler = null;
        _minimumLiveFrameIntervalTicks = 0;
        _lastConvertedLiveFrameTicks = 0;
        LastLiveCaptureException = null;
        lock (_sync)
        {
            _latestFrame = null;
        }
    }

    public bool TryGetLatestFrame(out BitmapSource? frame)
    {
        lock (_sync)
        {
            frame = _latestFrame;
            return frame is not null;
        }
    }

    private async void LiveFramePool_FrameArrived(
        Direct3D11CaptureFramePool sender,
        object args,
        int generation)
    {
        if (generation != Volatile.Read(ref _liveGeneration))
        {
            return;
        }

        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var minimumInterval = Volatile.Read(ref _minimumLiveFrameIntervalTicks);
        var lastConverted = Volatile.Read(ref _lastConvertedLiveFrameTicks);
        if (minimumInterval > 0 && lastConverted > 0 && now - lastConverted < minimumInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _processingLiveGeneration, generation, 0) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _lastConvertedLiveFrameTicks, now);
            using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().ConfigureAwait(false);
            if (generation != Volatile.Read(ref _liveGeneration))
            {
                return;
            }

            var bitmap = ToBitmapSource(softwareBitmap);
            lock (_sync)
            {
                if (generation == Volatile.Read(ref _liveGeneration))
                {
                    _latestFrame = bitmap;
                }
            }

            if (generation == Volatile.Read(ref _liveGeneration))
            {
                LastLiveCaptureException = null;
            }
        }
        catch (ObjectDisposedException)
        {
            // The live capture was stopped while a frame callback was still unwinding.
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _liveGeneration))
            {
                LastLiveCaptureException = ex;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _processingLiveGeneration, 0, generation);
        }
    }

    private static BitmapSource ToBitmapSource(SoftwareBitmap source)
    {
        using var converted = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                              source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
            ? SoftwareBitmap.Copy(source)
            : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var height = converted.PixelHeight;
        var width = converted.PixelWidth;
        var stride = width * 4;
        var managed = new byte[stride * height];
        var pixelBuffer = new Windows.Storage.Streams.Buffer((uint)managed.Length);
        converted.CopyToBuffer(pixelBuffer);

        using var reader = DataReader.FromBuffer(pixelBuffer);
        reader.ReadBytes(managed);

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            managed,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private async Task<WgcBorderlessAccessState> RequestBorderlessAccessAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348) ||
            !ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess") ||
            !ApiInformation.IsPropertyPresent(
                "Windows.Graphics.Capture.GraphicsCaptureSession",
                "IsBorderRequired"))
        {
            _borderlessAccessState = WgcBorderlessAccessState.Unsupported;
            _log.Info("WGC borderless capture is unavailable on this Windows build. Capture will continue with the system border.");
            return _borderlessAccessState;
        }

        try
        {
            _log.Info("Requesting user consent for WGC borderless capture.");
            var accessStatus = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            _borderlessAccessState = accessStatus == AppCapabilityAccessStatus.Allowed
                ? WgcBorderlessAccessState.Allowed
                : WgcBorderlessAccessState.Denied;
            _log.Info($"WGC borderless capture access result: {accessStatus}.");
        }
        catch (Exception exception)
        {
            _borderlessAccessState = WgcBorderlessAccessState.Failed;
            _log.Error(
                "WGC borderless capture access request failed. Capture will continue with the system border.",
                exception);
        }

        return _borderlessAccessState;
    }

    private bool TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        if (!IsBorderlessCaptureAllowed)
        {
            return false;
        }

        try
        {
            var property = typeof(GraphicsCaptureSession).GetProperty("IsBorderRequired");
            if (property is null)
            {
                _log.Info("WGC borderless capture was allowed, but IsBorderRequired is not available on the session.");
                return false;
            }

            property.SetValue(session, false);
            var borderRequired = property.GetValue(session) as bool?;
            _log.Info($"WGC capture border disabled for session: effectiveValue={borderRequired?.ToString() ?? "unknown"}.");
            return borderRequired == false;
        }
        catch (Exception exception)
        {
            _log.Error("Failed to disable the WGC capture border for the session.", exception);
            return false;
        }
    }
}

public enum WgcBorderlessAccessState
{
    Unknown,
    Unsupported,
    Allowed,
    Denied,
    Failed
}
