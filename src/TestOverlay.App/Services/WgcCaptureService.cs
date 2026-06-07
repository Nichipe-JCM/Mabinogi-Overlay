using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Native;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TestOverlay.App.Services;

public sealed class WgcCaptureService
{
    private readonly object _sync = new();
    private IDirect3DDevice? _liveDevice;
    private Direct3D11CaptureFramePool? _liveFramePool;
    private GraphicsCaptureSession? _liveSession;
    private BitmapSource? _latestFrame;
    private int _isProcessingLiveFrame;
    private int _liveGeneration;

    public Exception? LastLiveCaptureException { get; private set; }

    public async Task<BitmapSource> CaptureOnceAsync(GraphicsCaptureItem item, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var device = Direct3D11Interop.CreateDevice();
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            item.Size);
        using var session = framePool.CreateCaptureSession(item);
        var frameTask = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellation.Token.Register(() => frameTask.TrySetCanceled(cancellation.Token));
        framePool.FrameArrived += (_, _) =>
        {
            var frame = framePool.TryGetNextFrame();
            if (frame is not null)
            {
                frameTask.TrySetResult(frame);
            }
        };

        session.IsCursorCaptureEnabled = false;
        TryDisableCaptureBorder(session);
        session.StartCapture();

        using var capturedFrame = await frameTask.Task.ConfigureAwait(false);
        using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(capturedFrame.Surface).AsTask(cancellation.Token).ConfigureAwait(false);
        return ToBitmapSource(softwareBitmap);
    }

    public void StartLiveCapture(GraphicsCaptureItem item)
    {
        StopLiveCapture();
        Interlocked.Increment(ref _liveGeneration);

        _liveDevice = Direct3D11Interop.CreateDevice();
        _liveFramePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _liveDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        _liveSession = _liveFramePool.CreateCaptureSession(item);
        _liveSession.IsCursorCaptureEnabled = false;
        TryDisableCaptureBorder(_liveSession);
        _liveFramePool.FrameArrived += LiveFramePool_FrameArrived;
        _liveSession.StartCapture();
    }

    public void StopLiveCapture()
    {
        Interlocked.Increment(ref _liveGeneration);
        if (_liveFramePool is not null)
        {
            _liveFramePool.FrameArrived -= LiveFramePool_FrameArrived;
        }

        _liveSession?.Dispose();
        _liveFramePool?.Dispose();
        _liveDevice?.Dispose();
        _liveSession = null;
        _liveFramePool = null;
        _liveDevice = null;
        LastLiveCaptureException = null;
        Interlocked.Exchange(ref _isProcessingLiveFrame, 0);
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

    private async void LiveFramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var generation = Volatile.Read(ref _liveGeneration);
        if (Interlocked.Exchange(ref _isProcessingLiveFrame, 1) == 1)
        {
            using var droppedFrame = sender.TryGetNextFrame();
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            using var softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().ConfigureAwait(false);
            if (generation != Volatile.Read(ref _liveGeneration))
            {
                return;
            }

            var bitmap = ToBitmapSource(softwareBitmap);
            lock (_sync)
            {
                _latestFrame = bitmap;
            }

            LastLiveCaptureException = null;
        }
        catch (ObjectDisposedException)
        {
            // The live capture was stopped while a frame callback was still unwinding.
        }
        catch (Exception ex)
        {
            LastLiveCaptureException = ex;
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessingLiveFrame, 0);
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

    private static void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        try
        {
            var property = typeof(GraphicsCaptureSession).GetProperty("IsBorderRequired");
            property?.SetValue(session, false);
        }
        catch
        {
            // Best effort only. Older Windows builds or missing borderless consent may ignore this.
        }
    }
}
