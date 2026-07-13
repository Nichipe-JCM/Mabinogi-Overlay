using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class HotkeyParserTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F8")]
    [InlineData("Alt+F12")]
    [InlineData("Windows+Shift+K")]
    public void TryParse_AcceptsModifierAndKeyCombinations(string text)
    {
        var parsed = HotkeyParser.TryParse(text, out var hotkey);

        Assert.True(parsed);
        Assert.Equal(text, hotkey.DisplayText);
        Assert.NotEqual(0u, hotkey.Modifiers);
        Assert.NotEqual(0u, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("F8")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+NotAKey")]
    public void TryParse_RejectsIncompleteOrUnknownCombinations(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out _));
    }
}
