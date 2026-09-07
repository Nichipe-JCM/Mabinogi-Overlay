using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class CaptureWorkQueueTests
{
    [Fact]
    public async Task ResetDiscardsOldResultAndSkipsQueuedOldCapture()
    {
        var queue = new CaptureWorkQueue();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        var first = queue.RunAsync(() => { entered.Set(); release.Wait(token); return "old"; }, cancellationToken: token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), token));
        var invoked = false;
        var second = queue.RunAsync(() => { invoked = true; return "queued"; }, cancellationToken: token);
        var cleaned = false;
        var reset = queue.ResetAsync(() => cleaned = true);
        release.Set();
        Assert.Null(await first);
        Assert.Null(await second);
        await reset;
        Assert.False(invoked);
        Assert.True(cleaned);
        Assert.Equal("new", await queue.RunAsync(() => "new", cancellationToken: token));
    }

    [Fact]
    public async Task CancellationDoesNotLeaveQueueLocked()
    {
        var queue = new CaptureWorkQueue();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.RunAsync(() => "bad", cancellationToken: canceled.Token));
        Assert.Equal("ok", await queue.RunAsync(() => "ok", cancellationToken: TestContext.Current.CancellationToken));
    }
}
