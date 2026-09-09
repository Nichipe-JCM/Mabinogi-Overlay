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
}
