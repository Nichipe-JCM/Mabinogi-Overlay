using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class SingleFlightGateTests
{
    [Fact]
    public void TryEnter_AllowsOnlyOneCallerUntilExit()
    {
        var gate = new SingleFlightGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        gate.Exit();

        Assert.True(gate.TryEnter());
    }
}
