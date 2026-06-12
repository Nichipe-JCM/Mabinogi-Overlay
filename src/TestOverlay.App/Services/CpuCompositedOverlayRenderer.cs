using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public sealed class CpuCompositedOverlayRenderer
{
    private byte[] _targetBuffer = [];
    private byte[] _sourceBuffer = [];
    private int _targetWidth;
    private int _targetHeight;

    public BitmapSource Render(BitmapSource sourceFrame, IReadOnlyList<OverlaySlot> slots, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        EnsureTargetBuffer(width, height);
        Array.Clear(_targetBuffer, 0, _targetBuffer.Length);

        foreach (var slot in slots)
        {
            CompositeSlot(sourceFrame, slot);
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            _targetBuffer,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void EnsureTargetBuffer(int width, int height)
    {
        var length = checked(width * height * 4);
        if (_targetBuffer.Length != length)
        {
            _targetBuffer = new byte[length];
        }

        _targetWidth = width;
        _targetHeight = height;
    }

    private void CompositeSlot(BitmapSource sourceFrame, OverlaySlot slot)
    {
        var sourceRect = ClampSourceRect(sourceFrame, slot.Source.SourceRect);
        var destinationRect = ClampDestinationRect(slot.OverlayRect);
        if (sourceRect.Width <= 0 ||
            sourceRect.Height <= 0 ||
            destinationRect.Width <= 0 ||
            destinationRect.Height <= 0)
        {
            return;
        }

        var sourceStride = sourceRect.Width * 4;
        var sourceLength = sourceStride * sourceRect.Height;
        if (_sourceBuffer.Length < sourceLength)
        {
            _sourceBuffer = new byte[sourceLength];
        }

        sourceFrame.CopyPixels(sourceRect, _sourceBuffer, sourceStride, 0);

        var opacity = Math.Clamp(slot.Opacity, 0.05, 1);
        for (var y = 0; y < destinationRect.Height; y++)
        {
            var sourceY = Math.Min(sourceRect.Height - 1, (int)((long)y * sourceRect.Height / destinationRect.Height));
            var targetY = destinationRect.Y + y;
            var targetRowOffset = (targetY * _targetWidth + destinationRect.X) * 4;
            var sourceRowOffset = sourceY * sourceStride;

            for (var x = 0; x < destinationRect.Width; x++)
            {
                var sourceX = Math.Min(sourceRect.Width - 1, (int)((long)x * sourceRect.Width / destinationRect.Width));
                var sourceOffset = sourceRowOffset + sourceX * 4;
                var targetOffset = targetRowOffset + x * 4;

                if (opacity >= 0.999)
                {
                    _targetBuffer[targetOffset] = _sourceBuffer[sourceOffset];
                    _targetBuffer[targetOffset + 1] = _sourceBuffer[sourceOffset + 1];
                    _targetBuffer[targetOffset + 2] = _sourceBuffer[sourceOffset + 2];
                    _targetBuffer[targetOffset + 3] = 255;
                }
                else
                {
                    _targetBuffer[targetOffset] = Blend(_sourceBuffer[sourceOffset], _targetBuffer[targetOffset], opacity);
                    _targetBuffer[targetOffset + 1] = Blend(_sourceBuffer[sourceOffset + 1], _targetBuffer[targetOffset + 1], opacity);
                    _targetBuffer[targetOffset + 2] = Blend(_sourceBuffer[sourceOffset + 2], _targetBuffer[targetOffset + 2], opacity);
                    _targetBuffer[targetOffset + 3] = 255;
                }
            }
        }
    }

    private Int32Rect ClampSourceRect(BitmapSource sourceFrame, Rect rect)
    {
        var x = Math.Clamp((int)Math.Round(rect.X), 0, Math.Max(0, sourceFrame.PixelWidth - 1));
        var y = Math.Clamp((int)Math.Round(rect.Y), 0, Math.Max(0, sourceFrame.PixelHeight - 1));
        var right = Math.Clamp((int)Math.Round(rect.X + rect.Width), x, sourceFrame.PixelWidth);
        var bottom = Math.Clamp((int)Math.Round(rect.Y + rect.Height), y, sourceFrame.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private Int32Rect ClampDestinationRect(Rect rect)
    {
        var x = Math.Clamp((int)Math.Round(rect.X), 0, Math.Max(0, _targetWidth - 1));
        var y = Math.Clamp((int)Math.Round(rect.Y), 0, Math.Max(0, _targetHeight - 1));
        var right = Math.Clamp((int)Math.Round(rect.X + rect.Width), x, _targetWidth);
        var bottom = Math.Clamp((int)Math.Round(rect.Y + rect.Height), y, _targetHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static byte Blend(byte source, byte destination, double opacity) =>
        (byte)Math.Clamp((int)Math.Round(source * opacity + destination * (1 - opacity)), 0, 255);
}
