using System.IO;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class PortableDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"TestOverlay.PortableMigration-{Guid.NewGuid():N}");

    [Fact]
    public void Migrate_CopiesPortableDataIntoSeparatedUserDirectories()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "local");
        Directory.CreateDirectory(Path.Combine(legacy, "save"));
        Directory.CreateDirectory(Path.Combine(legacy, "Logs"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "save", "default.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "save", "erin-timer.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "Logs", "app.log"), "legacy");

        var result = PortableDataMigration.Migrate(legacy, target);

        Assert.True(result.Attempted);
        Assert.True(File.Exists(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "Profiles", "default.json")));
        Assert.True(File.Exists(Path.Combine(target, "erin-timer.json")));
        Assert.True(File.Exists(Path.Combine(target, "Logs", "LegacyPortable", "app.log")));
        Assert.False(File.Exists(Path.Combine(target, "Profiles", "erin-timer.json")));
    }

    [Fact]
    public void Migrate_DoesNotOverwriteExistingUserData()
    {
        var legacy = Path.Combine(_root, "legacy");
        var target = Path.Combine(_root, "local");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy");
        File.WriteAllText(Path.Combine(target, "settings.json"), "current");

        PortableDataMigration.Migrate(legacy, target);

        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "settings.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
