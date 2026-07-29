using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed partial class LocalizationParityTests
{
    [Fact]
    public void EnglishAndKorean_HaveMatchingKeysAndFormatPlaceholders()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Localization");
        var english = ReadLanguageFile(Path.Combine(directory, "en-US.lang"));
        var korean = ReadLanguageFile(Path.Combine(directory, "ko-KR.lang"));

        Assert.Equal(english.Keys.Order(), korean.Keys.Order());
        foreach (var key in english.Keys)
        {
            Assert.Equal(FormatPlaceholders(english[key]), FormatPlaceholders(korean[key]));
        }
    }

    private static Dictionary<string, string> ReadLanguageFile(string path)
    {
        Assert.True(File.Exists(path), $"Localization file was not copied to the test output: {path}");
        return File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => (Line: line, Separator: line.IndexOf('=')))
            .Where(item => item.Separator > 0)
            .ToDictionary(
                item => item.Line[..item.Separator].Trim(),
                item => item.Line[(item.Separator + 1)..].Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string[] FormatPlaceholders(string value) =>
        PlaceholderRegex()
            .Matches(value)
            .Select(match => match.Groups[1].Value)
            .Order()
            .ToArray();

    [GeneratedRegex(@"\{(\d+)(?:[^}]*)\}")]
    private static partial Regex PlaceholderRegex();
}
