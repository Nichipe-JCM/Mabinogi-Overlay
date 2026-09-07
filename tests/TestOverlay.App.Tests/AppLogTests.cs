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

    [Fact]
    public void UnwritablePrimary_SwitchesToFallbackAndPreservesMessage()
    {
        Directory.CreateDirectory(_directory);
        var invalidPrimaryDirectory = Path.Combine(_directory, "primary-is-a-file");
        File.WriteAllText(invalidPrimaryDirectory, "not-a-directory");
        var fallbackDirectory = Path.Combine(_directory, "fallback");

        using var log = new AppLog(
            invalidPrimaryDirectory,
            fallbackLogDirectory: fallbackDirectory);
        var criticalPath = log.WriteCritical(
            "fallback-fatal",
            new InvalidOperationException("fallback-detail"));
        log.Info("fallback-message");

        log.Dispose();

        Assert.Equal(fallbackDirectory, log.LogDirectory);
        Assert.Equal(Path.Combine(fallbackDirectory, "app.log"), criticalPath);
        var fallbackLog = File.ReadAllText(Path.Combine(fallbackDirectory, "app.log"));
        Assert.Contains("Primary log unavailable", fallbackLog);
        Assert.Contains("fallback-fatal", fallbackLog);
        Assert.Contains("fallback-message", fallbackLog);
    }

    [Fact]
    public void WriteCritical_WritesSynchronouslyAndReturnsActualPath()
    {
        using var log = new AppLog(_directory);

        var path = log.WriteCritical("fatal-message", new InvalidOperationException("fatal-detail"));

        Assert.Equal(Path.Combine(_directory, "app.log"), path);
        var contents = File.ReadAllText(path!);
        Assert.Contains("FATAL fatal-message", contents);
        Assert.Contains("fatal-detail", contents);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
