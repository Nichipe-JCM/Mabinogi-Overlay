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
    private WriteableBitmap? _output;
    private int[] _sourceOffsets = [];

    public BitmapSource Render(BitmapSource sourceFrame, IReadOnlyList<OverlaySlot> slots, int width, int height, double defaultSlotOpacity = 1, bool reuseOutput = false)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        EnsureTargetBuffer(width, height);
        Array.Clear(_targetBuffer, 0, _targetBuffer.Length);

        foreach (var slot in slots)
        {
            if (slot.Kind != OverlayElementKind.Quickslot)
            {
                continue;
            }

            CompositeSlot(sourceFrame, slot, defaultSlotOpacity);
        }

        if (reuseOutput)
        {
            if (_output is null || _output.PixelWidth != width || _output.PixelHeight != height)
                _output = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _output.WritePixels(new Int32Rect(0, 0, width, height), _targetBuffer, width * 4, 0);
            return _output;
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

    private void CompositeSlot(BitmapSource sourceFrame, OverlaySlot slot, double defaultSlotOpacity)
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

        var opacity = slot.EffectiveOpacity(defaultSlotOpacity);
        if (opacity <= 0) return;
        var alpha = opacity >= 0.999 ? (byte)255 : (byte)Math.Round(255 * opacity);
        var sourceStride = sourceRect.Width * 4;
        var sourceLength = sourceStride * sourceRect.Height;
        if (_sourceBuffer.Length < sourceLength)
        {
            _sourceBuffer = new byte[sourceLength];
        }

        sourceFrame.CopyPixels(sourceRect, _sourceBuffer, sourceStride, 0);

        if (_sourceOffsets.Length < destinationRect.Width) _sourceOffsets = new int[destinationRect.Width];
        for (var x = 0; x < destinationRect.Width; x++)
            _sourceOffsets[x] = Math.Min(sourceRect.Width - 1, (int)((long)x * sourceRect.Width / destinationRect.Width)) * 4;

        for (var y = 0; y < destinationRect.Height; y++)
        {
            var sourceY = Math.Min(sourceRect.Height - 1, (int)((long)y * sourceRect.Height / destinationRect.Height));
            var targetY = destinationRect.Y + y;
            var targetRowOffset = (targetY * _targetWidth + destinationRect.X) * 4;
            var sourceRowOffset = sourceY * sourceStride;

            if (sourceRect.Width == destinationRect.Width)
            {
                Buffer.BlockCopy(_sourceBuffer, sourceRowOffset, _targetBuffer, targetRowOffset, destinationRect.Width * 4);
                for (var x = 0; x < destinationRect.Width; x++) _targetBuffer[targetRowOffset + x * 4 + 3] = alpha;
                continue;
            }
            for (var x = 0; x < destinationRect.Width; x++)
            {
                var sourceOffset = sourceRowOffset + _sourceOffsets[x];
                var targetOffset = targetRowOffset + x * 4;
                _targetBuffer[targetOffset] = _sourceBuffer[sourceOffset];
                _targetBuffer[targetOffset + 1] = _sourceBuffer[sourceOffset + 1];
                _targetBuffer[targetOffset + 2] = _sourceBuffer[sourceOffset + 2];
                _targetBuffer[targetOffset + 3] = alpha;
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

}
