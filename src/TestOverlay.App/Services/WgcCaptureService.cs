using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Native;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace TestOverlay.App.Services;

public sealed class WgcCaptureService
{
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
