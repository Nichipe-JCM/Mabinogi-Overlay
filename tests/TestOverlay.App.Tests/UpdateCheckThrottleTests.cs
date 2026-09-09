using System.IO;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class UpdateCheckThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void OnlyOneAttemptIsAllowedUntilExactlyOneMinuteHasPassed()
    {
        var clock = new Clock(); var gate = new UpdateCheckThrottle(clock: clock);
        Assert.True(gate.TryAcquire()); Assert.False(gate.TryAcquire());
        clock.Now = clock.Now.AddSeconds(59);
        Assert.False(gate.TryAcquire()); Assert.Equal(1, gate.RemainingSeconds);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.True(gate.TryAcquire());
    }
    [Fact]
    public void RestartKeepsTheRemainingWait()
    {
        var directory = Path.Combine(Path.GetTempPath(), "overlay-throttle-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "check.json"); var clock = new Clock();
        try
        {
            Assert.True(new UpdateCheckThrottle(path, clock).TryAcquire());
            clock.Now = clock.Now.AddSeconds(20);
            var restarted = new UpdateCheckThrottle(path, clock);
            Assert.Equal(40, restarted.RemainingSeconds); Assert.False(restarted.TryAcquire());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
