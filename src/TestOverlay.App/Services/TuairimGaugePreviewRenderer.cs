using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TestOverlay.App.Services;

public static class TuairimGaugePreviewRenderer
{
    public const int BaseWidth = 96;
    public const int BaseHeight = 36;

    public static BitmapSource Render(int percent = 0)
    {
        percent = Math.Clamp(percent, 0, 100);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                ThemeService.Brush("ThemeSurfaceE6111315Brush", "#E6111315"),
                new Pen(ThemeService.Brush("OverlayAccentBrush", "#89DED4"), 1),
                new Rect(0.5, 0.5, BaseWidth - 1, BaseHeight - 1),
                6,
                6);

            var labelTypeface = new Typeface(new FontFamily("Noto Sans KR, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var valueTypeface = new Typeface(new FontFamily("Noto Sans KR, Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var label = CreateText(L.T("monitor.tuairim.overlay"), labelTypeface, 11, ThemeService.Brush("OverlayTextBrush", "#ECECEC"));
            var value = CreateText($"{percent}%", valueTypeface, 11, ThemeService.Brush("OverlayAccentBrush", "#89DED4"));
            context.DrawText(label, new Point(10, 8));
            context.DrawText(value, new Point(BaseWidth - 10 - value.WidthIncludingTrailingWhitespace, 8));

        }

        var bitmap = new RenderTargetBitmap(BaseWidth, BaseHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText CreateText(string text, Typeface typeface, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, 1);
}
