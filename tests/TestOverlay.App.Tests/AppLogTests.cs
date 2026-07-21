using System.IO;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class AppLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"TestOverlay.App.LogTests-{Guid.NewGuid():N}");

    [Fact]
    public void Dispose_FlushesQueuedMessages()
    {
        using (var log = new AppLog(_directory))
        {
            log.Info("queued-message");
        }

        Assert.Contains("queued-message", File.ReadAllText(Path.Combine(_directory, "app.log")));
    }

    [Fact]
    public void LargeLog_RotatesAndRetainsCurrentFile()
    {
        using (var log = new AppLog(_directory, maximumLogBytes: 64 * 1024, retainedLogFiles: 2))
        {
            var payload = new string('x', 2048);
            for (var index = 0; index < 80; index++)
            {
                log.Info($"entry-{index}:{payload}");
            }
        }

        Assert.True(File.Exists(Path.Combine(_directory, "app.log")));
        Assert.True(File.Exists(Path.Combine(_directory, "app.1.log")));
        Assert.False(File.Exists(Path.Combine(_directory, "app.3.log")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
