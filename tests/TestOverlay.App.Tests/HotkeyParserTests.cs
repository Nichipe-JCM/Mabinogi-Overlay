using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class HotkeyParserTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F8")]
    [InlineData("Alt+F12")]
    [InlineData("Windows+Shift+K")]
    [InlineData("Ctrl+1")]
    public void TryParse_AcceptsModifierAndKeyCombinations(string text)
    {
        var parsed = HotkeyParser.TryParse(text, out var hotkey);

        Assert.True(parsed);
        Assert.Equal(text, hotkey.DisplayText);
        Assert.NotEqual(0u, hotkey.Modifiers);
        Assert.NotEqual(0u, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("F8")]
    [InlineData("Ctrl")]
    [InlineData("Shift")]
    [InlineData("Alt")]
    [InlineData("1")]
    public void TryParse_AcceptsStandaloneKeys(string text)
    {
        var parsed = HotkeyParser.TryParse(text, out var hotkey);

        Assert.True(parsed);
        Assert.Equal(0u, hotkey.Modifiers);
        Assert.NotEqual(0u, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("F8+K")]
    public void TryParse_RejectsIncompleteOrUnknownCombinations(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out _));
    }
}
