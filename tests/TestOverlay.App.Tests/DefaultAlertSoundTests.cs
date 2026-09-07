using System.IO;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class DefaultAlertSoundTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"TestOverlay.DefaultAlertSound.Tests-{Guid.NewGuid():N}");

    [Fact]
    public void ResolvePath_ReturnsPackagedSoundWhenPresent()
    {
        var audioDirectory = Path.Combine(_directory, "Audio");
        Directory.CreateDirectory(audioDirectory);
        var expectedPath = Path.Combine(audioDirectory, "DefaultAlert.mp3");
        File.WriteAllBytes(expectedPath, [1, 2, 3]);

        var resolvedPath = DefaultAlertSound.ResolvePath(_directory);

        Assert.Equal(expectedPath, resolvedPath);
    }

    [Fact]
    public void ResolvePath_ReturnsNullWhenPackageContentIsMissing()
    {
        Assert.Null(DefaultAlertSound.ResolvePath(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
