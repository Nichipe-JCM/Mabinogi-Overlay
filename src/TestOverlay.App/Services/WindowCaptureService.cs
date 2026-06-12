using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Native;

namespace TestOverlay.App.Services;

public sealed class WindowCaptureService
{
    public BitmapSource CaptureClientArea(GameWindowInfo window)
    {
        if (!Win32Methods.GetClientRect(window.Handle, out var rect))
        {
            throw new InvalidOperationException("Selected window client rectangle could not be read.");
        }

        var origin = new Win32Methods.PointNative { X = 0, Y = 0 };
        if (!Win32Methods.ClientToScreen(window.Handle, ref origin))
        {
            throw new InvalidOperationException("Selected window client position could not be resolved.");
        }

        var screenDc = Win32Methods.GetDC(0);
        var memoryDc = Win32Methods.CreateCompatibleDC(screenDc);
        var bitmapHandle = Win32Methods.CreateCompatibleBitmap(screenDc, rect.Width, rect.Height);
        var oldObject = Win32Methods.SelectObject(memoryDc, bitmapHandle);

        try
        {
            if (!Win32Methods.BitBlt(memoryDc, 0, 0, rect.Width, rect.Height, screenDc, origin.X, origin.Y, Win32Methods.Srccopy))
            {
                throw new InvalidOperationException("Window pixels could not be copied from the desktop surface.");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (oldObject != 0)
            {
                Win32Methods.SelectObject(memoryDc, oldObject);
            }

            Win32Methods.DeleteObject(bitmapHandle);
            Win32Methods.DeleteDC(memoryDc);
            Win32Methods.ReleaseDC(0, screenDc);
        }
    }

    public BitmapSource Crop(BitmapSource source, Rect rect)
    {
        var x = Math.Clamp((int)Math.Round(rect.X), 0, Math.Max(0, source.PixelWidth - 1));
        var y = Math.Clamp((int)Math.Round(rect.Y), 0, Math.Max(0, source.PixelHeight - 1));
        var width = Math.Clamp((int)Math.Round(rect.Width), 1, source.PixelWidth - x);
        var height = Math.Clamp((int)Math.Round(rect.Height), 1, source.PixelHeight - y);
        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
        cropped.Freeze();
        return cropped;
    }
}
