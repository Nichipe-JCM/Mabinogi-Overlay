using System.Windows;
using System.Windows.Media;
using TestOverlay.App.Models;
using Wpf.Ui.Appearance;

namespace TestOverlay.App.Services;

public static class ThemeService
{
    private static Dictionary<string, Color>? _defaults;
    public static bool TryColor(string? value, out Color color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#' || !value.AsSpan(1).ToArray().All(char.IsAsciiHexDigit)) return false;
        color = (Color)ColorConverter.ConvertFromString(value); return true;
    }
    public static ThemeSettings Normalize(ThemeSettings? value)
    {
        var defaults = new ThemeSettings();
        return new ThemeSettings
        {
            Mode = value?.Mode is "White" or "Black" or "Custom" ? value.Mode : "Default",
            Background = TryColor(value?.Background, out _) ? value!.Background.ToUpperInvariant() : defaults.Background,
            Accent = TryColor(value?.Accent, out _) ? value!.Accent.ToUpperInvariant() : defaults.Accent,
            Foreground = TryColor(value?.Foreground, out _) ? value!.Foreground.ToUpperInvariant() : defaults.Foreground
        };
    }
    public static (Color Background, Color Accent, Color Foreground) Colors(ThemeSettings settings)
    {
        var theme = Normalize(settings);
        var values = theme.Mode switch
        {
            // Fluent's blue ramp: darker for light surfaces, lighter for dark surfaces.
            "White" => ("#FFFFFF", "#0F6CBD", "#202124"),
            "Black" => ("#000000", "#479EF5", "#F2F2F2"),
            "Custom" => (theme.Background, theme.Accent, theme.Foreground),
            _ => ("#111315", "#89DED4", "#ECECEC")
        };
        return (Parse(values.Item1), Parse(values.Item2), Parse(values.Item3));
    }
    public static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount), (byte)Math.Round(from.G + (to.G - from.G) * amount), (byte)Math.Round(from.B + (to.B - from.B) * amount));
    private static double Brightness(Color color) => (color.R * .2126 + color.G * .7152 + color.B * .0722) / 255;
    public static void Apply(ThemeSettings settings)
    {
        var resources = Application.Current.Resources;
        _defaults ??= resources.Keys.OfType<string>()
            .Where(key => (key.StartsWith("Overlay") || key.StartsWith("CandidateCheck") || key.StartsWith("ThemeSurface")) && resources[key] is SolidColorBrush)
            .ToDictionary(key => key, key => ((SolidColorBrush)resources[key]).Color);
        var theme = Normalize(settings);
        var (background, accent, foreground) = Colors(theme);
        var light = Brightness(background) > .5;
        var baseTheme = light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(baseTheme, Wpf.Ui.Controls.WindowBackdropType.None, false);
        ApplicationAccentColorManager.Apply(accent, baseTheme, false, false);
        var palette = new Dictionary<string, Color>(_defaults);
        if (theme.Mode != "Default")
        {
            palette["OverlayBgBrush"] = background;
            palette["OverlayPanelBrush"] = Blend(background, foreground, .045);
            palette["OverlayPanelElevatedBrush"] = Blend(background, foreground, .085);
            palette["OverlayPanelSoftBrush"] = Blend(background, foreground, .02);
            palette["OverlayBorderBrush"] = Blend(background, foreground, .23);
            palette["OverlayTextBrush"] = foreground;
            palette["OverlayMutedBrush"] = Blend(background, foreground, .68);
            palette["OverlayAccentBrush"] = palette["OverlayBlueBrush"] = palette["CandidateCheckBrush"] = accent;
            palette["OverlayAccentTextBrush"] = Brightness(accent) > .5 ? ColorsBlack : ColorsWhite;
            palette["OverlayDangerBrush"] = Parse(light ? "#B3261E" : "#FFB4AB");
            foreach (var key in _defaults.Keys.Where(key => key.StartsWith("ThemeSurface")))
            {
                var original = _defaults[key];
                var rgb = original.ToString()[3..];
                var changed = rgb is "89DED4" or "19C9BD" or "3ED7CE" ? accent
                    : rgb == "FFB4AB" ? palette["OverlayDangerBrush"]
                    : Brightness(original) > .5 ? foreground
                    : Blend(background, foreground, Math.Clamp((Brightness(original) - .065) * 1.25, .01, .45));
                changed.A = original.A; palette[key] = changed;
            }
        }
        foreach (var (key, color) in palette)
        {
            var brush = new SolidColorBrush(color); brush.Freeze(); resources[key] = brush;
            if (key.EndsWith("Brush") && !key.StartsWith("ThemeSurface")) resources[key[..^5] + "Color"] = color;
        }
        // WPF UI's implicit controls use these brushes rather than the app's named styles.
        resources["TextFillColorPrimaryBrush"] = resources["OverlayTextBrush"];
        resources["TextFillColorSecondaryBrush"] = resources["OverlayMutedBrush"];
        resources["TextFillColorTertiaryBrush"] = resources["OverlayMutedBrush"];
    }
    private static readonly Color ColorsBlack = Color.FromRgb(7, 20, 20);
    private static readonly Color ColorsWhite = Color.FromRgb(255, 255, 255);
}
