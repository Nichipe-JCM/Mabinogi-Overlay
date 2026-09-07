using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class CpuCompositedOverlayRendererTests
{
    [Theory]
    [InlineData(4, 1)]
    [InlineData(8, 0.5)]
    [InlineData(2, 0.2)]
    public void ReusableOutputPreservesNearestSamplingOpacityAndClearsOldSlots(int size, double opacity)
    {
        RunSta(() =>
        {
            var pixels = Enumerable.Range(0, 4 * 4 * 4).Select(i => (byte)i).ToArray();
            var source = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
            var slot = new OverlaySlot(new SlotCandidate(1, new Rect(0, 0, 4, 4), 1), new Rect(0, 0, size, size), source);
            var renderer = new CpuCompositedOverlayRenderer();
            var frame = renderer.Render(source, [slot], size, size, opacity, reuseOutput: true);
            var actual = new byte[size * size * 4];
            frame.CopyPixels(actual, size * 4, 0);
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var input = ((y * 4 / size) * 4 + x * 4 / size) * 4;
                var output = (y * size + x) * 4;
                Assert.Equal(pixels.AsSpan(input, 3).ToArray(), actual.AsSpan(output, 3).ToArray());
                Assert.Equal((byte)Math.Round(255 * opacity), actual[output + 3]);
            }
            var empty = renderer.Render(source, [], size, size, reuseOutput: true);
            Assert.Same(frame, empty);
            empty.CopyPixels(actual, size * 4, 0);
            Assert.All(actual, value => Assert.Equal(0, value));
            var resized = renderer.Render(source, [], size + 1, size, reuseOutput: true);
            Assert.NotSame(empty, resized);
            Assert.True(renderer.Render(source, [], size, size).IsFrozen);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { error = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
