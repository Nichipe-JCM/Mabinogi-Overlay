using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TestOverlay.App.Services;

public static class CustomTimerPreviewRenderer
{
    public const int BaseWidth = 180;
    public const int RowHeight = 22;

    public static BitmapSource Render(int timerCount = 2)
    {
        var baseHeight = GetBaseHeight(timerCount);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                ThemeService.Brush("ThemeSurfaceE6111315Brush", "#E6111315"),
                new Pen(ThemeService.Brush("OverlayAccentBrush", "#89DED4"), 1),
                new Rect(0.5, 0.5, BaseWidth - 1, baseHeight - 1),
                6,
                6);
            var nameFace = new Typeface("Noto Sans KR, Malgun Gothic");
            var timeFace = new Typeface(
                new FontFamily("Noto Sans KR, Malgun Gothic"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal);
            for (var index = 0; index < Math.Max(1, timerCount); index++)
            {
                DrawRow(
                    context,
                    nameFace,
                    timeFace,
                    L.F("custom.timer.default.name", index + 1),
                    index == 0 ? "01:30" : "00:10",
                    8 + index * RowHeight);
            }
        }

        var bitmap = new RenderTargetBitmap(BaseWidth, baseHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static int GetBaseHeight(int timerCount) => 16 + Math.Max(1, timerCount) * RowHeight;

    private static void DrawRow(
        DrawingContext context,
        Typeface nameFace,
        Typeface timeFace,
        string name,
        string time,
        double y)
    {
        var nameText = Text(name, nameFace, ThemeService.Brush("OverlayTextBrush", "#ECECEC"));
        var timeText = Text(time, timeFace, ThemeService.Brush("OverlayAccentBrush", "#89DED4"));
        context.DrawText(nameText, new Point(10, y));
        context.DrawText(timeText, new Point(BaseWidth - 10 - timeText.WidthIncludingTrailingWhitespace, y));
    }

    private static FormattedText Text(string value, Typeface face, Brush brush) => new(
        value,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        face,
        11,
        brush,
        1);
}
