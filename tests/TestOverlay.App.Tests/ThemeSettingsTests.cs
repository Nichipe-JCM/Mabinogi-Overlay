using System.Text.Json;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class ThemeSettingsTests
{
    [Fact]
    public void ExistingSettingsKeepTheOriginalPalette()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        var colors = ThemeService.Colors(settings.Theme);
        Assert.Equal("#FF111315", colors.Background.ToString());
        Assert.Equal("#FF89DED4", colors.Accent.ToString());
        Assert.Equal("#FFECECEC", colors.Foreground.ToString());
    }
    [Theory]
    [InlineData("White", "#FFFFFFFF", "#FF202124")]
    [InlineData("Black", "#FF000000", "#FFF2F2F2")]
    public void PresetsHaveTheirOwnBackgroundAndText(string mode, string background, string foreground)
    {
        var colors = ThemeService.Colors(new ThemeSettings { Mode = mode });
        Assert.Equal(background, colors.Background.ToString()); Assert.Equal(foreground, colors.Foreground.ToString());
    }
    [Fact]
    public void CustomColorsSurviveSettingsSerialization()
    {
        var settings = new AppSettings { Theme = new ThemeSettings { Mode = "Custom", Background = "#123456", Accent = "#FF9900", Foreground = "#ABCDEF" } };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(settings.Theme, loaded.Theme);
        Assert.Equal("#FF123456", ThemeService.Colors(loaded.Theme).Background.ToString());
    }
    [Fact]
    public void CorruptThemeFallsBackWithoutBreakingOtherSettings()
    {
        var normalized = ThemeService.Normalize(new ThemeSettings { Mode = "unknown", Background = "transparent", Accent = null!, Foreground = "#00000000" });
        Assert.Equal(new ThemeSettings(), normalized);
        Assert.Equal(new ThemeSettings(), ThemeService.Normalize(null));
    }
    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12GG56")]
    [InlineData("#ABC")]
    [InlineData("#80123456")]
    public void InvalidCustomInputIsRejected(string value) => Assert.False(ThemeService.TryColor(value, out _));
    [Theory]
    [InlineData("White", "#0F6CBD", "#ECECEC")]
    [InlineData("Black", "#479EF5", "#2B2B2B")]
    public void StandardAccentsMeetTextContrastOnBackgroundAndRaisedPanels(string mode, string accent, string raised)
    {
        var colors = ThemeService.Colors(new ThemeSettings { Mode = mode });
        Assert.Equal(ThemeService.Parse(accent), colors.Accent);
        static double Luminance(System.Windows.Media.Color c)
        {
            static double Linear(byte b) { var s = b / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        static double Contrast(System.Windows.Media.Color a, System.Windows.Media.Color b)
        {
            var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
        }
        Assert.True(Contrast(colors.Accent, colors.Background) >= 4.5);
        Assert.True(Contrast(colors.Accent, ThemeService.Parse(raised)) >= 4.5);
    }
}
