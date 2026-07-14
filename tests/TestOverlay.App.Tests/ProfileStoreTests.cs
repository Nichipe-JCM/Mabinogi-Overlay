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

    [Fact]
    public void SaveAndLoad_RoundTripsRepresentativeWorkspaceState()
    {
        var store = new ProfileStore(_directory);
        var profile = CreateValidProfile();
        profile.Name = "representative";
        profile.CanvasWidth = 960;
        profile.CanvasHeight = 420;
        profile.BuffMonitorEnabled = true;
        profile.SelectedBuffNameKeys.Add("battlefield");
        profile.BuffMonitorRoi = new OverlayProfileRect { X = 10, Y = 20, Width = 300, Height = 80 };
        profile.Candidates.Add(CreateCandidate(7));
        profile.Sections.Add(new OverlayProfileSection
        {
            Id = 3,
            SeedCandidateId = 7,
            CandidateIds = [7],
            SmallGapX = 2,
            SmallGapY = 5,
            LargeGap = 16
        });
        profile.Slots.Add(new OverlayProfileSlot
        {
            SourceCandidateId = 7,
            SourceWidth = 32,
            SourceHeight = 32,
            OverlayWidth = 48,
            OverlayHeight = 48,
            Opacity = 0.8,
            Scale = 1.5,
            HasOpacityOverride = true
        });

        store.Save(profile, profile.Name);
        var loaded = store.Load(profile.Name);

        Assert.NotNull(loaded);
        Assert.Equal(960, loaded.CanvasWidth);
        Assert.True(loaded.BuffMonitorEnabled);
        Assert.Equal("battlefield", Assert.Single(loaded.SelectedBuffNameKeys));
        Assert.Equal(7, Assert.Single(loaded.Candidates).Id);
        Assert.Equal(3, Assert.Single(loaded.Sections).Id);
        Assert.Equal(0.8, Assert.Single(loaded.Slots).Opacity);
    }

    [Fact]
    public void Load_LegacySlotsOnlyProfile_AppliesModernDefaults()
    {
        var store = new ProfileStore(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            store.GetProfilePath("legacy-slots"),
            """
            {
              "Name": "legacy-slots",
              "CanvasWidth": 360,
              "CanvasHeight": 160,
              "ScreenLeft": 120,
              "ScreenTop": 120,
              "Opacity": 0.8,
              "StopHotkey": "Ctrl+Shift+F8",
              "Slots": [
                {
                  "SourceX": 10,
                  "SourceY": 20,
                  "SourceWidth": 29,
                  "SourceHeight": 29,
                  "OverlayX": 0,
                  "OverlayY": 0,
                  "OverlayWidth": 44,
                  "OverlayHeight": 44
                }
              ]
            }
            """);

        var loaded = store.Load("legacy-slots");

        Assert.NotNull(loaded);
        Assert.Equal(30, loaded.RefreshFps);
        Assert.Equal(29, loaded.SlotInnerWidth);
        Assert.Empty(loaded.Candidates);
        Assert.Equal(1, Assert.Single(loaded.Slots).Scale);
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
