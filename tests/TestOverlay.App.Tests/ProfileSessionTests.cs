using System.IO;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class ProfileSessionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mabinogi-overlay-profile-session-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryFlush_PreservesDirtyStateWhenSaveFails()
    {
        var session = CreateDirtySession();

        var result = session.TryFlush(() => false);

        Assert.False(result);
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void TryFlush_ClearsDirtyStateOnlyAfterSaveSucceeds()
    {
        var session = CreateDirtySession();

        var result = session.TryFlush(() => true);

        Assert.True(result);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void TryFlush_DoesNotInvokeSaveWhenProfileIsClean()
    {
        var session = new ProfileSession(new ProfileStore(_directory));
        var invoked = false;

        var result = session.TryFlush(() =>
        {
            invoked = true;
            return false;
        });

        Assert.True(result);
        Assert.False(invoked);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ProfileSession CreateDirtySession() =>
        new(new ProfileStore(_directory))
        {
            IsDirty = true
        };
}
