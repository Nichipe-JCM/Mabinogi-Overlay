using System.IO;
using TestOverlay.App.Models;
using TestOverlay.App.Services;
using Xunit;

namespace TestOverlay.App.Tests;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"TestOverlay.App.Tests-{Guid.NewGuid():N}");

    [Fact]
    public void Save_RejectsDuplicateCandidateIds()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.Candidates.Add(CreateCandidate(1));
        profile.Candidates.Add(CreateCandidate(1));

        var exception = Assert.Throws<InvalidDataException>(() => store.Save(profile, "invalid"));

        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(store.GetProfilePath("invalid")));
    }

    [Fact]
    public void Load_CorruptPrimary_RecoversFromBackupAndRepairsPrimary()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.Name = "first";
        var path = store.Save(profile, "recover");
        profile.Name = "second";
        store.Save(profile, "recover");
        File.WriteAllText(path, "{corrupt-json");

        var recovered = store.Load("recover");

        Assert.NotNull(recovered);
        Assert.Equal("first", recovered.Name);
        Assert.True(store.LastLoadRecoveredFromBackup);
        Assert.Contains("\"Name\": \"first\"", File.ReadAllText(path));
    }

    [Fact]
    public void ListProfileNames_IncludesBackupOnlyProfile()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        var path = store.Save(profile, "backup-only");
        store.Save(profile, "backup-only");
        File.Delete(path);

        var names = store.ListProfileNames();

        Assert.Contains("backup-only", names);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static OverlayProfile CreateValidProfile() => new();

    private static OverlayProfileCandidate CreateCandidate(int id) => new()
    {
        Id = id,
        SourceWidth = 32,
        SourceHeight = 32,
        Kind = OverlayElementKind.Quickslot
    };
}
