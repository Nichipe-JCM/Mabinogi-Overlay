using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class LayoutEditHistoryTests
{
    [Fact]
    public void OneRecordedDrag_RequiresOneUndo()
    {
        var history = new LayoutEditHistory<double>();
        history.Record(1.5, 0.9, static (left, right) => left == right);

        Assert.True(history.TryUndo(0.9, out var previous));
        Assert.Equal(1.5, previous);
        Assert.False(history.TryUndo(previous, out _));
    }

    [Fact]
    public void NewEditAfterUndo_ClearsRedoHistory()
    {
        var history = new LayoutEditHistory<int>();
        history.Record(1, 2, static (left, right) => left == right);
        Assert.True(history.TryUndo(2, out _));

        history.Record(1, 3, static (left, right) => left == right);

        Assert.False(history.TryRedo(3, out _));
    }
}
