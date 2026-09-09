using System.Windows;
using System.Windows.Media;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class ThemeControlResourcesTests
{
    [Fact]
    public void PopupAndControlAliasesFollowPaletteAndExistingReferencesSurviveSwitches()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = new ResourceDictionary();
                var frameworkPopup = new SolidColorBrush(Colors.Gray);
                frameworkPopup.Freeze();
                resources["ComboBoxDropDownBackground"] = frameworkPopup;
                void Palette(string background, string foreground, string accent)
                {
                    foreach (var key in new[] { "Bg", "Panel", "PanelSoft", "Border" })
                        resources["Overlay" + key + "Brush"] = new SolidColorBrush(ThemeService.Parse(background));
                    foreach (var key in new[] { "Text", "Muted", "AccentText" })
                        resources["Overlay" + key + "Brush"] = new SolidColorBrush(ThemeService.Parse(foreground));
                    resources["OverlayAccentBrush"] = new SolidColorBrush(ThemeService.Parse(accent));
                    ThemeControlResources.Apply(resources);
                }
                Palette("#301800", "#EA0006", "#FFB0B0");
                var popup = (SolidColorBrush)resources["ComboBoxDropDownBackground"];
                var text = (SolidColorBrush)resources["ComboBoxForeground"];
                Assert.Equal(ThemeService.Parse("#301800"), popup.Color);
                foreach (var key in new[] { "ComboBoxItemForegroundSelected", "ButtonForeground", "TextControlForeground", "CheckBoxForeground", "ToolTipForeground", "ContextMenuForeground" })
                    Assert.Equal(ThemeService.Parse("#EA0006"), ((SolidColorBrush)resources[key]).Color);
                Assert.Equal(ThemeService.Parse("#FFB0B0"), ((SolidColorBrush)resources["ComboBoxItemPillFillBrush"]).Color);
                Assert.NotEqual(popup.Color, ((SolidColorBrush)resources["ComboBoxBackgroundPointerOver"]).Color);
                Assert.NotEqual(text.Color, ((SolidColorBrush)resources["ComboBoxForegroundDisabled"]).Color);

                Palette("#FFFFFF", "#202124", "#0F6CBD");
                Assert.Same(popup, resources["ComboBoxDropDownBackground"]);
                Assert.Same(text, resources["ComboBoxForeground"]);
                Assert.Equal(Colors.White, popup.Color);
                Assert.Equal(ThemeService.Parse("#202124"), text.Color);
                Palette("#111315", "#ECECEC", "#89DED4");
                Assert.Equal(ThemeService.Parse("#111315"), popup.Color);
                Assert.Equal(ThemeService.Parse("#ECECEC"), text.Color);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
