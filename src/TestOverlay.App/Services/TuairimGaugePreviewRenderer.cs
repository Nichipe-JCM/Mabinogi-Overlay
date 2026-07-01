using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TestOverlay.App.Services;

public static class TuairimGaugePreviewRenderer
{
    public const int BaseWidth = 160;
    public const int BaseHeight = 58;

    public static BitmapSource Render(int percent = 0)
    {
        percent = Math.Clamp(percent, 0, 100);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(230, 17, 19, 21)),
                new Pen(new SolidColorBrush(Color.FromRgb(0x89, 0xDE, 0xD4)), 1),
                new Rect(0.5, 0.5, BaseWidth - 1, BaseHeight - 1),
                6,
                6);

            var labelTypeface = new Typeface(new FontFamily("Segoe UI Variable, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var valueTypeface = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var label = CreateText(L.T("monitor.tuairim.overlay"), labelTypeface, 12, Brushes.White);
            var value = CreateText($"{percent}%", valueTypeface, 12, new SolidColorBrush(Color.FromRgb(0x89, 0xDE, 0xD4)));
            context.DrawText(label, new Point(10, 8));
            context.DrawText(value, new Point(BaseWidth - 10 - value.WidthIncludingTrailingWhitespace, 8));

            var track = new Rect(10, 34, BaseWidth - 20, 8);
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x35, 0x39, 0x3D)), null, track, 4, 4);
            if (percent > 0)
            {
                var fill = new Rect(track.X, track.Y, track.Width * percent / 100.0, track.Height);
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x89, 0xDE, 0xD4)), null, fill, 4, 4);
            }
        }

        var bitmap = new RenderTargetBitmap(BaseWidth, BaseHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText CreateText(string text, Typeface typeface, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, 1);
}
