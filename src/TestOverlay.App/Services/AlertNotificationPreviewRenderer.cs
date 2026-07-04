using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TestOverlay.App.Services;

public static class AlertNotificationPreviewRenderer
{
    public const int BaseWidth = 220;
    public const int BaseHeight = 82;
    public const int RowHeight = 22;

    public static BitmapSource Render()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(235, 17, 19, 21)),
                new Pen(new SolidColorBrush(Color.FromRgb(0x89, 0xDE, 0xD4)), 1),
                new Rect(0.5, 0.5, BaseWidth - 1, BaseHeight - 1),
                6,
                6);

            var typeface = new Typeface(
                new FontFamily("Noto Sans KR, Malgun Gothic"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal);
            var messages = new[]
            {
                L.T("monitor.alert.preview.buff"),
                L.T("monitor.alert.preview.tuairim"),
                L.T("monitor.alert.preview.multiple")
            };
            for (var index = 0; index < messages.Length; index++)
            {
                var text = CreateText(messages[index], typeface);
                context.DrawText(text, new Point(10, 8 + index * RowHeight));
            }
        }

        var bitmap = new RenderTargetBitmap(BaseWidth, BaseHeight, 96, 96, PixelFormats.Pbgra32);
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
