using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class MonitorRecognitionSessionTests
{
    [Fact]
    public void StopInvalidatesResultAndWaitsForOldWorkBeforeRestart()
    {
        using var session = new MonitorRecognitionSession();
        using var old = session.TryBegin()!;
        Assert.NotNull(old);
        Assert.True(old.IsCurrent);
        Assert.Null(session.TryBegin());
        session.Reset();
        Assert.True(old.Token.IsCancellationRequested);
        Assert.False(old.IsCurrent);
        Assert.Null(session.TryBegin());
        old.Dispose();
        using var next = session.TryBegin()!;
        Assert.True(next.IsCurrent);
        Assert.False(next.Token.IsCancellationRequested);
        old.Dispose();
        Assert.True(session.IsBusy);
    }

    [Fact]
    public void WindowDisposalCancelsButRetainsTokenUntilWorkReturns()
    {
        var session = new MonitorRecognitionSession();
        using var attempt = session.TryBegin()!;
        session.Dispose();
        Assert.True(attempt.Token.IsCancellationRequested);
        Assert.False(attempt.IsCurrent);
        using var registration = attempt.Token.Register(() => { });
        Assert.Null(session.TryBegin());
        attempt.Dispose();
        Assert.False(session.IsBusy);
        session.Dispose();
    }
}
