using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public static class InternalBuffTimerPreviewRenderer
{
    public const int BaseWidth = 200;
    public const int RowHeight = 22;
    public const int VerticalPadding = 16;

    public static IReadOnlyList<string> BuffNameKeys { get; } = MonitoredBuffCatalog.AllBuffNameKeys;

    public static BitmapSource Render(
        IReadOnlyList<InternalBuffTimer> timers,
        IReadOnlyCollection<string>? visibleBuffNameKeys = null)
    {
        var timerByKey = timers.ToDictionary(timer => timer.NameKey, StringComparer.Ordinal);
        var visibleKeys = BuffNameKeys
            .Where(key => visibleBuffNameKeys is null || visibleBuffNameKeys.Contains(key))
            .ToList();
        var baseHeight = GetBaseHeight(visibleKeys.Count);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(230, 17, 19, 21)),
                new Pen(new SolidColorBrush(Color.FromRgb(0x89, 0xDE, 0xD4)), 1),
                new Rect(0.5, 0.5, BaseWidth - 1, baseHeight - 1),
                6,
                6);

            var nameTypeface = new Typeface(new FontFamily("Noto Sans KR, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var timeTypeface = new Typeface(new FontFamily("Noto Sans KR, Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            for (var index = 0; index < visibleKeys.Count; index++)
            {
                var key = visibleKeys[index];
                var y = 8 + index * RowHeight;
                timerByKey.TryGetValue(key, out var timer);
                var hasTimer = timer is not null;
                var value = hasTimer ? $"{timer!.RemainingSeconds / 60:00}:{timer.RemainingSeconds % 60:00}" : "--:--";
                var brush = hasTimer && timer!.RemainingSeconds <= 30
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xAB))
                    : new SolidColorBrush(Color.FromRgb(0x89, 0xDE, 0xD4));
                var time = CreateText(value, timeTypeface, 11, brush);
                var availableNameWidth = BaseWidth - 28 - time.WidthIncludingTrailingWhitespace;
                var nameText = BuildDisplayName(key, timer);
                var name = CreateText(nameText, nameTypeface, 11, Brushes.White);
                if (name.WidthIncludingTrailingWhitespace > availableNameWidth)
                {
                    var fittedSize = 11 * availableNameWidth / name.WidthIncludingTrailingWhitespace;
                    name = CreateText(nameText, nameTypeface, fittedSize, Brushes.White);
                }
                context.DrawText(name, new Point(10, y));
                context.DrawText(time, new Point(BaseWidth - 10 - time.WidthIncludingTrailingWhitespace, y));
            }

            if (visibleKeys.Count == 0)
            {
                var empty = CreateText(L.T("monitor.buff.none.selected"), nameTypeface, 11, Brushes.Gray);
                context.DrawText(empty, new Point(10, 8));
            }
        }

        var bitmap = new RenderTargetBitmap(BaseWidth, baseHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static int GetBaseHeight(int visibleBuffCount) =>
        VerticalPadding + Math.Max(1, visibleBuffCount) * RowHeight;

    public static string BuildDisplayName(string nameKey, InternalBuffTimer? timer)
    {
        if (!MonitoredBuffCatalog.IsMusicBuff(nameKey))
        {
            return L.T(nameKey);
        }

        var tags = new List<string>();
        if (timer?.HasHarmony == true)
        {
            tags.Add(L.T("monitor.buff.tag.harmony"));
        }
        if (timer?.HasTuanExtension == true)
        {
            tags.Add(L.T("monitor.buff.tag.tuan"));
        }

        return tags.Count == 0
            ? L.T(nameKey)
            : $"{L.T(nameKey)} [{string.Join("] [", tags)}]";
    }

    private static FormattedText CreateText(string text, Typeface typeface, double size, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            1);
}
