using System.Threading;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void ActivationBeforeWindowIsReadyIsDeliveredAfterListeningStarts()
    {
        var name = @"Local\MabinogiOverlay.Tests." + Guid.NewGuid();
        using var primary = new SingleInstanceCoordinator(name);
        Assert.True(primary.TryAcquirePrimary());
        var wasSecondary = false;
        var thread = new Thread(() =>
        {
            using var secondary = new SingleInstanceCoordinator(name);
            wasSecondary = !secondary.TryAcquirePrimary();
            secondary.SignalPrimaryInstance();
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(wasSecondary);
        using var received = new ManualResetEventSlim();
        primary.Listen(received.Set);
        Assert.True(received.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
