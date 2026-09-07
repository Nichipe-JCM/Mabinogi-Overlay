using System.IO;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class PendingSettingsSaveTests
{
    [Fact]
    public void FailedSaveRetainsChangesUntilRetrySucceeds()
    {
        var pending = new PendingSettingsSave();
        pending.MarkDirty();
        Assert.False(pending.TryFlush(() => throw new IOException("locked")));
        Assert.True(pending.IsDirty);
        Assert.NotNull(pending.LastError);
        Assert.True(pending.TryFlush(() => { }));
        Assert.False(pending.IsDirty);
        Assert.Null(pending.LastError);
        Assert.True(pending.TryFlush(() => throw new Exception("Clean state must not save")));
    }
}
