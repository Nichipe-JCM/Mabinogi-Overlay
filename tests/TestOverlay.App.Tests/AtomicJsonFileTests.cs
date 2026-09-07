using System.IO;
using System.Text.Json;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class AtomicJsonFileTests
{
    [Fact]
    public void Load_ReturnsValidBackupWhenPrimaryCannotBeRepaired()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.json");
        try
        {
            File.WriteAllText(path + ".bak", "{\"Name\":\"backup\"}");
            File.WriteAllText(path, "invalid");
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = AtomicJsonFile.Load<OverlayProfile>(path, new JsonSerializerOptions());
            Assert.Equal("backup", result!.Value.Name);
            Assert.True(result.RecoveredFromBackup);
            Assert.NotNull(result.RestoreException);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("{\"Candidates\":[null]}")]
    [InlineData("{\"Slots\":[null]}")]
    [InlineData("{\"BuffAnchors\":[null]}")]
    public void Load_InvalidCollectionElementsRecoverFromBackup(string invalidJson)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.json");
        try
        {
            File.WriteAllText(path + ".bak", JsonSerializer.Serialize(new OverlayProfile { Name = "backup" }));
            File.WriteAllText(path, invalidJson);
            var result = AtomicJsonFile.Load<OverlayProfile>(path, new JsonSerializerOptions(), OverlayProfileValidator.Validate);
            Assert.Equal("backup", result!.Value.Name);
            Assert.True(result.RecoveredFromBackup);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Load_RejectsOversizedPrimaryAndUsesSmallBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "profile.json");
        try
        {
            File.WriteAllText(path + ".bak", "{\"Name\":\"backup\"}");
            File.WriteAllText(path, new string(' ', 101));
            var result = AtomicJsonFile.Load<OverlayProfile>(path, new JsonSerializerOptions(), maximumBytes: 100);
            Assert.Equal("backup", result!.Value.Name);
        }
        finally { Directory.Delete(directory, true); }
    }
}
