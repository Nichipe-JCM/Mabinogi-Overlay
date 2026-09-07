using System.Windows;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class MonitorValueRecognitionServiceTests
{
    [Fact]
    public void Batch_lines_are_assigned_to_nearest_buff_rows()
    {
        var anchors = new[]
        {
            Anchor("battle", 20),
            Anchor("divine", 60),
            Anchor("condition", 100)
        };
        var lines = new[]
        {
            new RecognizedTextLine("6분 39초", new Rect(220, 18, 52, 14)),
            new RecognizedTextLine("2분 21초", new Rect(220, 58, 52, 14)),
            new RecognizedTextLine("잘못된 값", new Rect(220, 98, 52, 14))
        };

        var result = MonitorValueRecognitionService.MatchBuffTimeLines(anchors, lines);

        Assert.Equal(399, result["battle"].RemainingSeconds);
        Assert.Equal(141, result["divine"].RemainingSeconds);
        Assert.False(result.ContainsKey("condition"));
    }

    [Fact]
    public void Batch_line_outside_row_tolerance_is_not_assigned()
    {
        var anchors = new[] { Anchor("divine", 20) };
        var lines = new[]
        {
            new RecognizedTextLine("2:45", new Rect(220, 80, 40, 14))
        };

        var result = MonitorValueRecognitionService.MatchBuffTimeLines(anchors, lines);

        Assert.Empty(result);
    }

    [Fact]
    public void Batch_line_is_reserved_for_the_closest_row_when_an_adjacent_row_is_missing()
    {
        var anchors = new[]
        {
            Anchor("first", 20),
            Anchor("second", 36)
        };
        var lines = new[]
        {
            new RecognizedTextLine("1:30", new Rect(220, 36, 40, 14))
        };

        var result = MonitorValueRecognitionService.MatchBuffTimeLines(anchors, lines);

        Assert.False(result.ContainsKey("first"));
        Assert.Equal(90, result["second"].RemainingSeconds);
    }

    private static BuffIconMatch Anchor(string nameKey, double top) =>
        new(nameKey, new Rect(20, top, 16, 16), 1, true, 1);
}
