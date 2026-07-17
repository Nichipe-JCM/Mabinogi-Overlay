using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TestOverlay.App.Services;

public static class AlertNotificationPreviewRenderer
{
    public const int BaseWidth = 132;
    public const int RowHeight = 20;

    public static int GetBaseHeight(int rows) => 30 + Math.Clamp(rows, 1, 4) * RowHeight;

    public static BitmapSource Render(int rows = 2)
    {
        rows = Math.Clamp(rows, 1, 4);
        var baseHeight = GetBaseHeight(rows);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(235, 17, 19, 21)),
                new Pen(new SolidColorBrush(Color.FromRgb(0x89, 0xDE, 0xD4)), 1),
                new Rect(0.5, 0.5, BaseWidth - 1, baseHeight - 1),
                6,
                6);

            var typeface = new Typeface(
                new FontFamily("Noto Sans KR, Malgun Gothic"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal);
            var samples = new[]
            {
                (MonitoredBuffCatalog.BattleOverture, "00:30"),
                (MonitoredBuffCatalog.MarchSong, "00:20"),
                (MonitoredBuffCatalog.DivineLink, "00:10")
            };
            for (var index = 0; index < rows; index++)
            {
                var y = 8 + index * RowHeight;
                if (index < samples.Length)
                {
                    context.DrawImage(BuffVisualCatalog.ActiveIcon(samples[index].Item1), new Rect(10, y - 1, 18, 18));
                    context.DrawText(CreateText(samples[index].Item2, typeface), new Point(36, y));
                }
                else
                {
                    context.DrawText(CreateText(L.F("monitor.alert.visual.tuairim", 95), typeface), new Point(10, y));
                }
            }
        }

        var bitmap = new RenderTargetBitmap(BaseWidth, baseHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static FormattedText CreateText(string text, Typeface typeface) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            11,
            new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0xCC)),
            1);
}
