using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestOverlay.App.Models;

namespace TestOverlay.App.Services;

public static class InternalBuffTimerPreviewRenderer
{
    public const int BaseWidth = 104;
    public const int RowHeight = 24;
    public const int VerticalPadding = 12;

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
                ThemeService.Brush("ThemeSurfaceE6111315Brush", "#E6111315"),
                new Pen(ThemeService.Brush("OverlayAccentBrush", "#89DED4"), 1),
                new Rect(0.5, 0.5, BaseWidth - 1, baseHeight - 1),
                6,
                6);

            var timeTypeface = new Typeface(new FontFamily("Noto Sans KR, Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var badgeTypeface = new Typeface(new FontFamily("Noto Sans KR, Malgun Gothic"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            for (var index = 0; index < visibleKeys.Count; index++)
            {
                var key = visibleKeys[index];
                var y = 6 + index * RowHeight;
                timerByKey.TryGetValue(key, out var timer);
                var hasTimer = timer is not null;
                var value = hasTimer ? $"{timer!.RemainingSeconds / 60:00}:{timer.RemainingSeconds % 60:00}" : "--:--";
                var brush = hasTimer && timer!.RemainingSeconds <= 30
                    ? ThemeService.Brush("OverlayDangerBrush", "#FFB4AB")
                    : ThemeService.Brush("OverlayAccentBrush", "#89DED4");
                var time = CreateText(value, timeTypeface, 11, brush);
                context.DrawImage(BuffVisualCatalog.ActiveIcon(key), new Rect(8, y, 18, 18));
                var badge = BuildBadge(timer);
                if (!string.IsNullOrEmpty(badge))
                {
                    context.DrawText(
                        CreateText(badge, badgeTypeface, 8, new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x75))),
                        new Point(29, y + 3));
                }
                context.DrawText(time, new Point(BaseWidth - 8 - time.WidthIncludingTrailingWhitespace, y + 1));
            }

            if (visibleKeys.Count == 0)
            {
                var nameTypeface = new Typeface(new FontFamily("Noto Sans KR, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var empty = CreateText(L.T("monitor.buff.none.selected"), nameTypeface, 11, ThemeService.Brush("OverlayMutedBrush", "#A6ABAF"));
                context.DrawText(empty, new Point(8, 6));
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

    public static string BuildBadge(InternalBuffTimer? timer)
    {
        if (timer is null)
        {
            return string.Empty;
        }

        return timer switch
        {
            { HasHarmony: true, HasTuanExtension: true } => "HT",
            { HasHarmony: true } => "H",
            { HasTuanExtension: true } => "T",
            _ => string.Empty
        };
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
